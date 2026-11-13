using System.Security.Cryptography;
using System.Text;

namespace PowerControlProWpf;

internal sealed record WindowsUpdateItem(string Id, int Revision, string Title, bool Driver,
    string Category, string Detail, double SizeBytes, bool Downloaded, bool Optional,
    bool RequiresUserInput, string Eula)
{
    public string Key => Id + ":" + Revision;
    public string EulaHash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Eula)));
}
internal sealed record WindowsUpdateSearch(WindowsUpdateItem[] Items, bool Partial);
internal sealed record WindowsUpdateSelection(string Id, int Revision, string EulaHash);
internal sealed record WindowsUpdateRequest(string Operation, WindowsUpdateSelection[] Selection);
internal sealed record WindowsUpdateEvent(string Phase, string Text = "", double? Percent = null,
    string? Key = null, int? Code = null, bool RebootRequired = false);

internal interface IWindowsUpdateService
{
    Task<WindowsUpdateSearch> SearchAsync(bool? drivers, CancellationToken cancellation);
    Task RunElevatedAsync(WindowsUpdateRequest request, Action<WindowsUpdateEvent> progress, CancellationToken cancellation);
}

internal static class WindowsUpdatePolicy
{
    internal static void Validate(WindowsUpdateRequest request)
    {
        if (request.Operation != "install") throw new InvalidOperationException("Invalid update operation.");
        if (request.Selection is null || request.Selection.Length > 500 ||
            request.Selection.Length == 0 ||
            request.Selection.Any(x => x is null || !Guid.TryParseExact(x.Id, "D", out _) || x.Revision < 0 ||
                x.EulaHash is null || x.EulaHash.Length != 64 || !x.EulaHash.All(Uri.IsHexDigit)) ||
            request.Selection.Select(x => x.Id + ":" + x.Revision).Distinct().Count() != request.Selection.Length)
            throw new InvalidOperationException("Invalid update selection.");
    }

    internal static void VerifySelection(WindowsUpdateSelection[] selected, IEnumerable<WindowsUpdateItem> available)
    {
        var current = available.ToDictionary(x => x.Key);
        foreach (var item in selected)
        {
            if (!current.TryGetValue(item.Id + ":" + item.Revision, out var found) || found.RequiresUserInput ||
                !string.Equals(found.EulaHash, item.EulaHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Die Updateauswahl oder Lizenzbedingungen haben sich geändert. Bitte erneut suchen.");
        }
    }
}
