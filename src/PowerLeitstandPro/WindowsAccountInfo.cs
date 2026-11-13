using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace PowerControlProWpf;

internal enum WindowsAccountKind { Unknown, Local, Microsoft, Work }

internal sealed record WindowsAccountInfo(WindowsAccountKind Kind, string? Name = null)
{
    private const string MicrosoftPrefix = @"MicrosoftAccount\";

    internal static WindowsAccountInfo Parse(string output)
    {
        var parts = output.Trim().Split('|', 3);
        var source = parts[0].Trim().Replace(" ", "");
        if (source.Equals("MicrosoftAccount", StringComparison.OrdinalIgnoreCase))
        {
            var name = parts.Length == 3 && !string.IsNullOrWhiteSpace(parts[2])
                ? parts[2].Trim()
                : parts.ElementAtOrDefault(1)?.Trim();
            if (name?.StartsWith(MicrosoftPrefix, StringComparison.OrdinalIgnoreCase) == true)
                name = name[MicrosoftPrefix.Length..];
            return new(WindowsAccountKind.Microsoft, string.IsNullOrWhiteSpace(name) ? null : name);
        }
        if (source.Equals("Local", StringComparison.OrdinalIgnoreCase)) return new(WindowsAccountKind.Local);
        if (source.Equals("ActiveDirectory", StringComparison.OrdinalIgnoreCase) ||
            source.Equals("AzureAD", StringComparison.OrdinalIgnoreCase) ||
            source.Equals("MicrosoftEntraGroup", StringComparison.OrdinalIgnoreCase)) return new(WindowsAccountKind.Work);
        return new(WindowsAccountKind.Unknown);
    }

    internal static Task<WindowsAccountInfo> ReadAsync() => Task.Run(async () =>
    {
        try
        {
            // The process can run as a different account after elevation.
            var user = ReadSessionField(5); // WTSUserName
            var domain = ReadSessionField(7); // WTSDomainName
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(domain)) return new(WindowsAccountKind.Unknown);
            if (domain.Equals("MicrosoftAccount", StringComparison.OrdinalIgnoreCase)) return new(WindowsAccountKind.Microsoft, user);
            if (domain.Equals("AzureAD", StringComparison.OrdinalIgnoreCase)) return new(WindowsAccountKind.Work);
            var sid = ((SecurityIdentifier)new NTAccount(domain, user).Translate(typeof(SecurityIdentifier))).Value;
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"));
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false);" +
                "$u=Get-LocalUser -SID '" + sid + "' -ErrorAction Stop;" +
                "[Console]::WriteLine(([string]$u.PrincipalSource)+'|'+$u.Name+'|'+$u.FullName)");
            return Parse(await UserQueryProcess.ReadAsync(start, TimeSpan.FromSeconds(8)));
        }
        catch (Exception) { return new(WindowsAccountKind.Unknown); }
    });

    private static string? ReadSessionField(int field)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, -1, field, out var buffer, out _)) return null;
        try { return Marshal.PtrToStringUni(buffer); }
        finally { WTSFreeMemory(buffer); }
    }

    [DllImport("wtsapi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int field, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll", ExactSpelling = true)]
    private static extern void WTSFreeMemory(IntPtr buffer);
}
