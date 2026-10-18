using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace PowerControlProWpf;

internal interface IAutoStartRegistration
{
    string? Read();
    void Write(string command);
    void Delete();
}

internal sealed class RegistryAutoStartRegistration(string keyPath = RegistryAutoStartRegistration.KeyPath,
    string valueName = RegistryAutoStartRegistration.ValueName) : IAutoStartRegistration
{
    internal const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "PowerControlProWpf";

    public string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        var value = key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value switch
        {
            null => null,
            string command => command,
            _ => throw new InvalidDataException("Der Autostart-Eintrag hat ein ungültiges Format.")
        };
    }

    public void Write(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
        key.SetValue(valueName, command, RegistryValueKind.String);
    }

    public void Delete()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}

internal sealed record AutoStartStatus(bool? Enabled, bool Updated, string? Command, string? Error);

internal sealed class AutoStartService(IAutoStartRegistration registration)
{
    internal static AutoStartService Current { get; } = new(new RegistryAutoStartRegistration());

    // Snapshot/test hosts must never register themselves as the user's application.
    internal static string? CurrentExecutable => Assembly.GetEntryAssembly() == typeof(AutoStartService).Assembly
        ? Environment.ProcessPath : null;

    internal AutoStartStatus Check(string? executable, bool repair = true)
    {
        string? command = null;
        bool? enabled = null;
        try
        {
            command = registration.Read();
            enabled = command is not null;
            if (!enabled.Value || !repair || executable is null)
                return new(enabled, false, command, null);
            var expected = CommandFor(executable);
            if (string.Equals(command, expected, StringComparison.OrdinalIgnoreCase))
                return new(true, false, command, null);
            // Replace only our one existing value; never turn absent autostart on.
            registration.Write(expected);
            command = registration.Read();
            if (!string.Equals(command, expected, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Der neue Autostart-Pfad konnte nicht bestätigt werden.");
            return new(true, true, command, null);
        }
        catch (Exception exception) when (IsRegistrationError(exception))
        {
            return new(enabled, false, command, exception.Message);
        }
    }

    internal AutoStartStatus SetEnabled(bool enabled, string? executable)
    {
        try
        {
            if (enabled) registration.Write(CommandFor(executable));
            else registration.Delete();
            var status = Check(executable, repair: false);
            if (status.Error is not null) return status;
            if (status.Enabled != enabled || enabled && !string.Equals(status.Command, CommandFor(executable), StringComparison.OrdinalIgnoreCase))
                return status with { Error = "Die Autostart-Einstellung konnte nicht bestätigt werden." };
            return status;
        }
        catch (Exception exception) when (IsRegistrationError(exception))
        {
            return Check(executable, repair: false) with { Error = exception.Message };
        }
    }

    private static string CommandFor(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || executable.Contains('"') ||
            !Path.IsPathFullyQualified(executable) ||
            !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileName(executable), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Der Pfad der ausführbaren App konnte nicht sicher ermittelt werden.");
        var path = Path.GetFullPath(executable);
        if (!File.Exists(path)) throw new FileNotFoundException("Die aktuelle Programmdatei wurde nicht gefunden.", path);
        return $"\"{path}\" --tray";
    }

    private static bool IsRegistrationError(Exception exception) => exception is
        IOException or InvalidDataException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException;
}
