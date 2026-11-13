using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PowerControlProWpf;

internal sealed record AppLogError(string Source, string Operation, string Message, string? Code = null, string? OperationId = null);

// A bounded store for events and actual errors. A failed write never changes the application operation.
internal sealed class AppLogStore(string directory, long maxBytes = 2 * 1024 * 1024, int archives = 3, Func<DateTimeOffset>? clock = null)
{
    private static readonly object Gate = new();
    private readonly HashSet<string> _errors = new(StringComparer.Ordinal);
    private readonly Queue<string> _errorOrder = new();
    private static readonly UTF8Encoding Utf8 = new(false);
    internal string? LastWriteFailure { get; private set; }
    private DateTimeOffset Now => (clock ?? (() => DateTimeOffset.Now))();
    internal static string Clean(string text) => Regex.Replace(Regex.Replace(text, @"\x1B\[[0-?]*[ -/]*[@-~]", ""), @"[\x00-\x1f\x7f]+", " ").Trim();
    private string Line(string text)
    {
        string prefix = "[" + Now.ToString("dd.MM.yyyy, HH:mm:ss") + "] ";
        string cleaned = Clean(text);
        int budget = (int)Math.Min(8192, Math.Max(64, maxBytes - Utf8.GetByteCount(prefix) - 8));
        if (Utf8.GetByteCount(cleaned) > budget)
        {
            var bounded = new StringBuilder(); int used = 0;
            foreach (var rune in cleaned.EnumerateRunes())
            { if (used + rune.Utf8SequenceLength > budget - 4) break; bounded.Append(rune); used += rune.Utf8SequenceLength; }
            cleaned = bounded + " …";
        }
        return prefix + cleaned;
    }
    internal string? Event(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        lock (Gate) { LastWriteFailure = null; string line = Line(text); Append("events", line); return line; }
    }
    internal string? Error(AppLogError error)
    {
        if (string.IsNullOrWhiteSpace(error.Message)) return null;
        lock (Gate)
        {
            LastWriteFailure = null;
            if (error.OperationId is { Length: > 0 } operation)
            {
                string key = operation + "|" + error.Source + "|" + error.Operation + "|" + error.Code + "|" + Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(error.Message)));
                if (!_errors.Add(key)) return null;
                _errorOrder.Enqueue(key); while (_errorOrder.Count > 512) _errors.Remove(_errorOrder.Dequeue());
            }
            string line = Line("ERROR · " + error.Source + " · " + error.Operation + (error.Code is null ? "" : " · Code " + error.Code) + ": " + error.Message);
            Append("errors", line); Append("events", line); return line;
        }
    }
    private void Append(string stem, string line)
    {
        try
        {
            if (maxBytes < 128 || archives < 1) throw new IOException("Invalid log limits");
            if (!Path.IsPathFullyQualified(directory)) throw new IOException("Log folder must be absolute");
            for (string? part = Path.GetFullPath(Path.Combine(directory, stem + ".log")); part is not null; part = Path.GetDirectoryName(part))
                if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked log storage is not supported");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, stem + ".log");
            if (File.Exists(path) && new FileInfo(path).Length + Utf8.GetByteCount(line + Environment.NewLine) > maxBytes) Rotate(path, stem);
            File.AppendAllText(path, line + Environment.NewLine, Utf8);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        { LastWriteFailure = error.Message; }
    }
    private void Rotate(string path, string stem)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked log file cannot be archived");
        string name = stem + "-" + Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..8] + ".zip";
        string archive = Path.Combine(directory, name), partial = archive + ".partial";
        try
        {
            byte[] digest; long length; DateTime modified;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                length = input.Length; modified = File.GetLastWriteTimeUtc(path); digest = SHA256.HashData(input); input.Position = 0;
                using (var zip = ZipFile.Open(partial, ZipArchiveMode.Create))
                using (var output = zip.CreateEntry(stem + ".log", CompressionLevel.Optimal).Open()) input.CopyTo(output);
                using var verify = ZipFile.OpenRead(partial); using var restored = verify.GetEntry(stem + ".log")!.Open();
                if (!SHA256.HashData(restored).SequenceEqual(digest)) throw new IOException("Log archive verification failed");
            }
            if (new FileInfo(path).Length != length || File.GetLastWriteTimeUtc(path) != modified) throw new IOException("Log changed during archiving");
            File.Move(partial, archive); File.Delete(path);
            var owned = Directory.EnumerateFiles(directory, stem + "-*.zip").Select(p => new FileInfo(p))
                .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0 && Regex.IsMatch(f.Name, "^" + stem + @"-\d{8}-\d{6}-\d{3}-[a-f0-9]{8}\.zip$"))
                .OrderByDescending(f => f.Name, StringComparer.Ordinal).ToArray();
            foreach (var old in owned.Skip(archives))
            {
                try { old.Delete(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { LastWriteFailure = error.Message; }
            }
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
}
