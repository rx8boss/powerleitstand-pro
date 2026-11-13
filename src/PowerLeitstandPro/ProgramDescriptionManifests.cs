using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PowerControlProWpf;

// Description-only fallback for machines without the optional WinGet COM registration.
// Conservative scalar/block reader for public locale manifests, never an installer parser.
internal static class ProgramDescriptionManifests
{
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase) { "raw.githubusercontent.com" };
    internal static async Task<ProgramDescriptionMetadata?> ReadAsync(string id, string version, string target, CancellationToken token,
        Func<Uri, CancellationToken, Task<string>>? read = null)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(4));
        read ??= async (uri, cancel) => Encoding.UTF8.GetString(await CatalogWeb.ReadAsync(uri, Hosts, 128 * 1024, cancel).ConfigureAwait(false));
        ProgramDescriptionText? original = null;
        try
        {
            string installer = ProgramInstallTargets.ManifestUri(id, version).AbsoluteUri;
            string prefix = installer[..^".installer.yaml".Length];
            string yaml = await read(new(prefix + ".yaml"), limit.Token).WaitAsync(limit.Token).ConfigureAwait(false);
            string? locale = DefaultLocale(id, version, yaml);
            if (locale is null) return null;
            var originalTask = LocaleAsync(locale);
            string preferred = target == "de" ? "de-DE" : "en-US";
            var localizedTask = ProgramDescriptions.Language(locale) == target ? originalTask : LocaleAsync(preferred);
            original = await originalTask.ConfigureAwait(false);
            var localized = await localizedTask.ConfigureAwait(false);
            if (localized is null && ProgramDescriptions.Language(locale) != target && !limit.IsCancellationRequested)
                localized = await LocaleAsync(target).ConfigureAwait(false);
            original ??= localized;
            return original is null ? null : new(original, localized);

            async Task<ProgramDescriptionText?> LocaleAsync(string wanted)
            {
                try
                {
                    string body = await read(new(prefix + ".locale." + wanted + ".yaml"), limit.Token).WaitAsync(limit.Token).ConfigureAwait(false);
                    return Parse(id, version, wanted, body);
                }
                catch (Exception e) when (e is IOException or HttpRequestException or OperationCanceledException) { return null; }
            }
        }
        catch (Exception e) when (e is IOException or HttpRequestException or OperationCanceledException or ArgumentException)
        { token.ThrowIfCancellationRequested(); return original is null ? null : new(original); }
    }
    internal static string? DefaultLocale(string id, string version, string yaml)
    {
        try
        {
            var lines = Lines(yaml);
            if (Field(lines, "PackageIdentifier") != id || Field(lines, "PackageVersion") != version || Field(lines, "ManifestType") != "version") return null;
            var locale = Field(lines, "DefaultLocale");
            return ProgramDescriptions.Language(locale) is null ? null : locale;
        }
        catch (Exception e) when (e is FormatException or JsonException) { return null; }
    }
    internal static ProgramDescriptionText? Parse(string id, string version, string locale, string yaml)
    {
        try
        {
            var lines = Lines(yaml);
            if (Field(lines, "PackageIdentifier") != id || Field(lines, "PackageVersion") != version
                || !string.Equals(Field(lines, "PackageLocale"), locale, StringComparison.OrdinalIgnoreCase)
                || Field(lines, "ManifestType") is not ("locale" or "defaultLocale") || ProgramDescriptions.Language(locale) is null) return null;
            string? description;
            try { description = Field(lines, "Description"); }
            catch (Exception e) when (e is FormatException or JsonException) { description = null; }
            description ??= Field(lines, "ShortDescription");
            return string.IsNullOrWhiteSpace(description) || description.Length > 32768 ? null : new(description.Trim(), locale);
        }
        catch (Exception e) when (e is FormatException or JsonException) { return null; }
    }
    private static string[] Lines(string yaml)
    {
        if (yaml.Length > 128 * 1024 || yaml.Contains('\t')) throw new FormatException();
        var lines = yaml.TrimStart('\uFEFF').Replace("\r", "").Split('\n');
        if (lines.Any(l => l is "---" or "..." || l.StartsWith('%'))) throw new FormatException();
        return lines;
    }
    private static string? Field(string[] lines, string key)
    {
        var indices = Enumerable.Range(0, lines.Length).Where(i => lines[i].StartsWith(key + ":", StringComparison.Ordinal)).ToArray();
        if (indices.Length > 1) throw new FormatException();
        if (indices.Length == 0) return null;
        int index = indices[0]; string value = lines[index][(key.Length + 1)..].Trim();
        if (value.StartsWith('|') || value.StartsWith('>'))
        {
            string indicator = value.Split('#')[0].TrimEnd();
            if (!Regex.IsMatch(indicator, @"^[|>][+-]?$") || key is not ("Description" or "ShortDescription")) throw new FormatException();
            var body = lines.Skip(index + 1).TakeWhile(l => l.Length == 0 || l[0] == ' ').ToArray();
            var nonempty = body.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            if (nonempty.Length == 0) return null;
            int indent = nonempty.Min(l => l.TakeWhile(c => c == ' ').Count());
            if (indent == 0) throw new FormatException();
            var text = body.Select(l => string.IsNullOrWhiteSpace(l) ? "" : l[indent..]).ToArray();
            if (indicator[0] == '|') return string.Join('\n', text).TrimEnd();
            var folded = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                folded.Append(text[i]);
                if (i + 1 < text.Length) folded.Append(text[i].Length > 0 && text[i + 1].Length > 0 && text[i][0] != ' ' && text[i + 1][0] != ' ' ? ' ' : '\n');
            }
            return folded.ToString().TrimEnd();
        }
        if (value.StartsWith('"'))
        {
            // JSON string escapes are a safe subset of YAML double-quoted scalars.
            // Unsupported YAML escapes retain the independently parsed short text.
            int end = 1; bool escaped = false;
            for (; end < value.Length; end++)
            { if (!escaped && value[end] == '"') break; if (!escaped && value[end] == '\\') escaped = true; else escaped = false; }
            if (end == value.Length || !CommentOrEmpty(value[(end + 1)..])) throw new FormatException();
            return JsonSerializer.Deserialize<string>(value[..(end + 1)]);
        }
        if (value.StartsWith('\''))
        {
            var scalar = new StringBuilder(); int i = 1;
            for (; i < value.Length; i++)
            {
                if (value[i] != '\'') { scalar.Append(value[i]); continue; }
                if (i + 1 < value.Length && value[i + 1] == '\'') { scalar.Append('\''); i++; continue; }
                break;
            }
            if (i == value.Length || !CommentOrEmpty(value[(i + 1)..])) throw new FormatException();
            return scalar.ToString();
        }
        int comment = value.IndexOf(" #", StringComparison.Ordinal);
        if (comment >= 0) value = value[..comment].TrimEnd();
        if (value.Length == 0 || value[0] is '&' or '*' or '!' or '[' or '{' || value is "null" or "~") return null;
        var continuation = lines.Skip(index + 1).TakeWhile(l => l.Length > 0 && l[0] == ' ').ToArray();
        if (continuation.Length > 0)
        {
            if (key is not ("Description" or "ShortDescription")) throw new FormatException();
            value += " " + string.Join(' ', continuation.Select(l => l.Trim()));
        }
        return value;
    }
    private static bool CommentOrEmpty(string text) => text.TrimStart() is { Length: 0 } || text.TrimStart().StartsWith('#');
}
