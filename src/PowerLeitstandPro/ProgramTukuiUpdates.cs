using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Timer = System.Threading.Timer;

namespace PowerControlProWpf;

// No verified standalone digest feed: bind the official package to its exact signed author and checked bytes.
internal sealed class ProgramTukuiUpdates
{
    internal const string Url = "https://api.tukui.org/v1/client/windows";
    internal const long Limit = 40L * 1024 * 1024;
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase) { "api.tukui.org" };
    private readonly Func<Action<ProgramDownload>, CancellationToken, Task<byte[]>> _read;
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly object _sync = new();
    private sealed record Checked(PortableUpdate Update, byte[] Bytes, DateTime ExpiresUtc);
    private Checked? _checked;
    private Timer? _expiry;
    internal ProgramTukuiUpdates(Func<Action<ProgramDownload>, CancellationToken, Task<byte[]>>? read = null, string? root = null)
    { _read = read ?? ReadAsync; _root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerLeitstand PRO", "Portable Updates"); }
    internal static bool Eligible(ProgramInstallation proof) => proof.Source is "Portable" or "SelectedFile"
        && proof.Path is not null && !proof.WinGetCompatible && proof.ManagedCopies is not { Count: > 0 };
    internal static bool Signed(string path) => ManufacturerSignature.Verify(path, "Open Source Developer",
        ["Open Source Developer Paweł Jastrzębski", "Open Source Developer, Paweł Jastrzębski"]);
    private static async Task<byte[]> ReadAsync(Action<ProgramDownload> report, CancellationToken token)
    {
        using var connection = new OnlineRequest(token);
        using var response = await CatalogWeb.OpenAsync(new(Url), Hosts, connection.Token);
        await using var input = await response.Content.ReadAsStreamAsync(connection.Token); using var output = new MemoryStream();
        await ProgramPortableUpdates.CopyDownloadAsync(input, output, response.Content.Headers.ContentLength, report, connection.Token, Limit);
        return output.ToArray();
    }
    internal async Task<PortableUpdate?> ProbeAsync(ProgramInstallation proof, Action<ProgramDownload> report, CancellationToken token)
    {
        if (!Eligible(proof) || !ProgramPortableUpdates.PlainPath(proof.Path!)) return null;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        string? stage = null;
        try
        {
            Clear(); var path = Path.GetFullPath(proof.Path!);
            var old = ProgramPortableUpdates.FileVersion("tukui-client", path);
            if (new FileInfo(path).Length > Limit || old is null || ProgramPortableUpdates.Machine(path) != 0x8664 || !Signed(path))
                throw new IOException("Die Tukui-Datei oder Entwicklersignatur wurde nicht bestätigt.");
            string originalHash = ProgramPortableUpdates.Hash(path);
            byte[] bytes = await _read(report, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
            if (bytes.LongLength is <= 0 or > Limit) throw new IOException("Der Tukui-Download ist ungültig.");
            using (var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
                if (zip.Entries.Count != 1 || zip.Entries[0].FullName != "Tukui Client.exe" || zip.Entries[0].Length is <= 0 or > Limit)
                    throw new IOException("Das portable Tukui-Archiv wurde nicht bestätigt.");
            stage = Path.Combine(_root, "Tukui-Pruefung-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
            string archive = Path.Combine(stage, "download.zip"), payload = Path.Combine(stage, "Tukui Client.exe");
            await File.WriteAllBytesAsync(archive, bytes, token).ConfigureAwait(false);
            var update = new PortableUpdate("tukui-client", path, old, old, Url, Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length, "Tukui Client.exe", originalHash);
            ProgramPortableUpdates.ExtractPayload(update, archive, payload);
            string? version = ProgramPortableUpdates.FileVersion("tukui-client", payload);
            if (version is null || ProgramPortableUpdates.Machine(payload) != 0x8664 || !Signed(payload)
                || ProgramVersions.IsNewer(version, old)) throw new IOException("Die Tukui-Datei oder Entwicklersignatur wurde nicht bestätigt.");
            if (ProgramPortableUpdates.Hash(path) != originalHash) throw new IOException("Eine portable Programmdatei wurde zwischenzeitlich geändert.");
            token.ThrowIfCancellationRequested(); update = update with { Version = version }; var value = new Checked(update, bytes, DateTime.UtcNow.AddMinutes(5));
            lock (_sync)
            {
                _checked = value;
                _expiry = new Timer(_ => { lock (_sync) { if (ReferenceEquals(_checked, value)) Clear(); } }, null, TimeSpan.FromMinutes(5), Timeout.InfiniteTimeSpan);
            }
            return update;
        }
        finally
        {
            if (stage is not null) try { Directory.Delete(stage, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _gate.Release();
        }
    }
    internal PortableUpdate? Cached(ProgramInstallation proof)
    {
        Checked? value; lock (_sync) value = _checked;
        return Eligible(proof) && value is not null && value.ExpiresUtc > DateTime.UtcNow && string.Equals(proof.Path, value.Update.Path, StringComparison.OrdinalIgnoreCase)
            && ProgramPortableUpdates.PlainPath(value.Update.Path) && ProgramPortableUpdates.Hash(value.Update.Path) == value.Update.OriginalHash
            ? value.Update : null;
    }
    internal bool HasPackage(ProgramInstallation proof)
    { lock (_sync) return Eligible(proof) && _checked is { } value && value.ExpiresUtc > DateTime.UtcNow && string.Equals(proof.Path, value.Update.Path, StringComparison.OrdinalIgnoreCase); }
    internal async Task CopyAsync(PortableUpdate update, string target, Action<ProgramDownload> report, CancellationToken token)
    {
        Checked? value; lock (_sync) value = _checked;
        if (value is null || value.ExpiresUtc <= DateTime.UtcNow || value.Update != update || Convert.ToHexString(SHA256.HashData(value.Bytes)) != update.Sha256)
            throw new IOException("Das geprüfte Tukui-Paket ist abgelaufen. Bitte erneut prüfen.");
        using var input = new MemoryStream(value.Bytes); await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await ProgramPortableUpdates.CopyDownloadAsync(input, output, value.Bytes.Length, report, token, Limit);
    }
    internal void Clear()
    { lock (_sync) { _checked = null; _expiry?.Dispose(); _expiry = null; } }
}
