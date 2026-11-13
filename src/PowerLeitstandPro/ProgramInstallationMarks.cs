using System.IO;
using System.Text.Json;

namespace PowerControlProWpf;

// User confirmations are deliberately separate from detected versions and update evidence.
internal sealed class ProgramInstallationMarks
{
    private readonly string _file;
    private HashSet<string> _ids = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, DateTimeOffset> _installedAt = new(StringComparer.OrdinalIgnoreCase);
    internal string? Error { get; private set; }
    internal bool Contains(string id) => _ids.Contains(id);
    internal DateTimeOffset? InstalledAt(string id) => _installedAt.TryGetValue(id, out var value) ? value : null;
    internal string? MostRecentlyInstalledId => _installedAt.OrderByDescending(x => x.Value).Select(x => x.Key).FirstOrDefault();
    internal ProgramInstallationMarks(string file)
    {
        _file = file;
        try
        {
            if (!File.Exists(file)) return;
            if (new FileInfo(file).Length > 1024 * 1024) throw new IOException("Manual installation file too large");
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                // Previous releases stored only the user's manual confirmations.
                _ids = document.RootElement.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()).Where(ValidId).Select(x => x!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            else if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (document.RootElement.TryGetProperty("manual", out var manual) && manual.ValueKind == JsonValueKind.Array)
                    _ids = manual.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString())
                        .Where(ValidId).Select(x => x!).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (document.RootElement.TryGetProperty("installedAt", out var history) && history.ValueKind == JsonValueKind.Object)
                    _installedAt = history.EnumerateObject().Where(x => ValidId(x.Name) && x.Value.ValueKind == JsonValueKind.String
                            && DateTimeOffset.TryParse(x.Value.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.AssumeUniversal, out _))
                        .ToDictionary(x => x.Name, x => DateTimeOffset.Parse(x.Value.GetString()!, System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AssumeUniversal), StringComparer.OrdinalIgnoreCase);
            }
            else throw new JsonException("Invalid installation mark file");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { Error = e.Message; }
    }
    private static bool ValidId(string? id) => id is { Length: > 0 and <= 200 }
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');
    internal void Set(string id, bool installed)
    {
        if (!ValidId(id)) throw new ArgumentException("Invalid catalog identity");
        var next = new HashSet<string>(_ids, StringComparer.OrdinalIgnoreCase);
        if (installed) next.Add(id); else next.Remove(id);
        Save(next, _installedAt);
    }
    internal void RecordInstalled(string id, DateTimeOffset? time = null)
    {
        if (!ValidId(id)) throw new ArgumentException("Invalid catalog identity");
        var next = new Dictionary<string, DateTimeOffset>(_installedAt, StringComparer.OrdinalIgnoreCase) { [id] = time ?? DateTimeOffset.UtcNow };
        Save(_ids, next);
    }
    internal void ForgetInstallation(string id)
    {
        if (!_installedAt.ContainsKey(id)) return;
        var next = new Dictionary<string, DateTimeOffset>(_installedAt, StringComparer.OrdinalIgnoreCase); next.Remove(id);
        Save(_ids, next);
    }
    private void Save(HashSet<string> manual, Dictionary<string, DateTimeOffset> installedAt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var temporary = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var content = new { manual = manual.Order(StringComparer.OrdinalIgnoreCase), installedAt = installedAt.ToDictionary(x => x.Key, x => x.Value.ToUniversalTime().ToString("O")) };
            File.WriteAllText(temporary, JsonSerializer.Serialize(content));
            File.Move(temporary, _file, true); _ids = new(manual, StringComparer.OrdinalIgnoreCase);
            _installedAt = new(installedAt, StringComparer.OrdinalIgnoreCase); Error = null;
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
