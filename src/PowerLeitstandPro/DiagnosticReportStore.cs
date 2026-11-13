using System.Globalization;
using System.IO;

namespace PowerControlProWpf;

internal enum DiagnosticReportStatus { Completed, Empty, Failed, Cancelled, Opened }

internal sealed class DiagnosticReportStore(string logDirectory, string folderName)
{
    internal const int Limit = 5;
    private const string TimestampFormat = "yyyy-MM-dd_HH-mm-ss.fffffff";
    private static readonly object Gate = new();
    private readonly string _folder = Path.Combine(logDirectory, "Diagnose", folderName);
    internal string RelativeFolder => Path.Combine("Diagnose", folderName);

    internal sealed record Entry(string FileName, DateTimeOffset Date, DiagnosticReportStatus Status);

    internal (Entry Report, int CleanupFailures) Save(DiagnosticReportStatus status, string contents)
    {
        lock (Gate)
        {
            var folder = GetFolder(create: true);
            var date = DateTimeOffset.UtcNow;
            var fileName = $"{date.ToString(TimestampFormat, CultureInfo.InvariantCulture)}_{status}_{Guid.NewGuid():N}.log";
            var temporary = Path.Combine(folder, fileName + ".tmp");
            var ownsTemporary = false;
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                {
                    ownsTemporary = true;
                    using var writer = new StreamWriter(stream);
                    writer.Write(contents);
                }
                File.Move(temporary, Path.Combine(folder, fileName));
                ownsTemporary = false;
            }
            finally
            {
                if (ownsTemporary) TryDelete(temporary);
            }

            int failures;
            try { failures = List().Skip(Limit).Count(entry => !TryDelete(Path.Combine(folder, entry.FileName))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failures = 1; }
            return (new Entry(fileName, date, status), failures);
        }
    }

    internal IReadOnlyList<Entry> List()
    {
        lock (Gate)
        {
            var folder = GetFolder(create: false);
            if (!Directory.Exists(folder)) return [];
            var entries = new List<Entry>();
            foreach (var path in Directory.EnumerateFiles(folder, "*.log"))
            {
                var entry = Parse(Path.GetFileName(path));
                if (entry is not null && IsRegularFile(path)) entries.Add(entry);
            }
            return entries.OrderByDescending(entry => entry.Date).ThenByDescending(entry => entry.FileName, StringComparer.Ordinal).ToArray();
        }
    }

    internal int Delete(IReadOnlyList<Entry> confirmed)
    {
        lock (Gate)
        {
            var current = List().ToHashSet();
            return confirmed.Where(current.Contains).Count(entry => !TryDelete(Path.Combine(_folder, entry.FileName)));
        }
    }

    internal string OpenablePath(Entry entry)
    {
        lock (Gate)
        {
            var folder = GetFolder(create: false);
            if (Parse(entry.FileName) is not { } parsed || parsed != entry)
                throw new IOException("Ungültiger Diagnosebericht.");
            var path = Path.Combine(folder, entry.FileName);
            if (!IsRegularFile(path)) throw new FileNotFoundException("Der Diagnosebericht wurde nicht gefunden.");
            return path;
        }
    }

    internal string OpenableFolder()
    {
        lock (Gate) return GetFolder(create: true);
    }

    internal string ReadResult(Entry entry)
    {
        using var reader = File.OpenText(OpenablePath(entry));
        // Allow space for the command and metadata after bounding the collected output.
        const int maxPreviewCharacters = RepairDriverQuery.OutputLimit + 32_000;
        var buffer = new char[(int)Math.Min(reader.BaseStream.Length, maxPreviewCharacters) + 1];
        var count = reader.ReadBlock(buffer, 0, buffer.Length);
        if (count == buffer.Length) throw new IOException("The diagnostic report is too large to preview.");
        var text = new string(buffer, 0, count).ReplaceLineEndings("\n");
        foreach (var heading in new[] { "\n\nErgebnis\n", "\n\nResult\n" })
        {
            var start = text.IndexOf(heading, StringComparison.Ordinal);
            if (start >= 0) return text[(start + heading.Length)..].Trim();
        }
        return text.Trim();
    }

    private string GetFolder(bool create)
    {
        if (Path.GetFileName(folderName) != folderName || folderName is "." or ".." || string.IsNullOrWhiteSpace(folderName))
            throw new IOException("Ungültiger Diagnoseordner.");
        // Never follow a link or junction to a different folder when opening or pruning reports.
        for (var current = new DirectoryInfo(_folder); current is not null; current = current.Parent)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Der Diagnoseordner liegt in einer Verzeichnisverknüpfung.");
        }
        if (create) Directory.CreateDirectory(_folder);
        return _folder;
    }

    private static Entry? Parse(string fileName)
    {
        if (Path.GetFileName(fileName) != fileName || !fileName.EndsWith(".log", StringComparison.Ordinal)) return null;
        var pieces = Path.GetFileNameWithoutExtension(fileName).Split('_');
        if (pieces.Length != 4 || !Guid.TryParseExact(pieces[3], "N", out _) ||
            !Enum.TryParse<DiagnosticReportStatus>(pieces[2], out var status) || pieces[2] != status.ToString() ||
            !DateTimeOffset.TryParseExact(pieces[0] + "_" + pieces[1], TimestampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var date)) return null;
        return new(fileName, date, status);
    }

    private static bool IsRegularFile(string path)
    {
        try { return (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}

