using System.Diagnostics;
using System.IO;

namespace PowerControlProWpf;

// Resolve an existing Git executable; never install it or change the parent's PATH.
internal static class ProgramScoopGit
{
    internal const string Missing = "Scoop benötigt Git. Eine passende vorhandene Git-Programmdatei konnte nicht bestätigt werden. Bitte Git installieren oder dessen Programmdatei prüfen und erneut versuchen.";
    internal const string Unusable = "Git wurde gefunden, konnte von Scoop aber nicht verwendet werden. Bitte die Git-Installation prüfen und erneut versuchen.";
    internal const string PreparationFailed = "Scoop-Vorbereitung fehlgeschlagen. Bitte den vorherigen Protokolleintrag prüfen und erneut versuchen.";
    private static ProgramEntry Entry => ProgramCatalog.Entries.Single(e => e.Id == "git");

    internal static bool ConfirmFile(string path)
    {
        try
        {
            return Path.GetFileName(path).Equals("git.exe", StringComparison.OrdinalIgnoreCase)
                // Git for Windows' executable uses this publisher, while the catalog lists "Git Project".
                && !path.Contains(';') && ProgramFileAssociations.Validate(Entry with { Publisher = "The Git Development Community" }, path).Version is not null;
        }
        catch (Exception error) when (ProgramPathResolver.ReadFailure(error) || error is ArgumentException or InvalidOperationException) { return false; }
    }
    internal static string? FirstConfirmed(IEnumerable<string?> candidates, Func<string, bool>? validate = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? candidate in candidates.Take(96))
        {
            if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate) || candidate.StartsWith(@"\\", StringComparison.Ordinal) || candidate.Contains(';')) continue;
            string? path = ProgramPathResolver.LocalTarget(candidate, true);
            if (path is not null && seen.Add(path) && (validate ?? ConfirmFile)(path)) return path;
        }
        return null;
    }
    internal static string? Find(ProgramManagedTarget target, IEnumerable<ProgramManagerRoot> roots)
        => FirstConfirmed(Candidates(target, roots));

    private static IEnumerable<string?> Candidates(ProgramManagedTarget target, IEnumerable<ProgramManagerRoot> roots)
    {
        var sameRoot = roots.Where(r => r.Manager == "Scoop" && r.Scope == target.Scope
            && string.Equals(Path.GetFullPath(r.Directory), Path.GetFullPath(target.Root), StringComparison.OrdinalIgnoreCase)).ToArray();
        var copies = ProgramManagedInventory.Read([Entry], CancellationToken.None, sameRoot).Matches.GetValueOrDefault(Entry.Id);
        if (copies is { Count: 1 }) yield return copies[0].Location?.Executable;
        var local = ProgramLocalInventory.Read([Entry], CancellationToken.None, includeManaged: false).GetValueOrDefault(Entry.Id);
        yield return local?.LocationEvidence?.Executable ?? local?.Path;
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Take(80))
        {
            string path = directory.Trim().Trim('"');
            if (Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal))
                yield return Path.Combine(path, "git.exe");
        }
    }
    internal static void Configure(ProcessStartInfo process, string? confirmedGit)
    {
        if (confirmedGit is null || !Path.IsPathFullyQualified(confirmedGit) || confirmedGit.Contains(';')
            || !ProgramPortableUpdates.PlainPath(confirmedGit) || !File.Exists(confirmedGit)) throw new IOException(Missing);
        string directory = Path.GetDirectoryName(confirmedGit)!;
        string inherited = process.Environment.TryGetValue("PATH", out var path) ? path ?? "" : "";
        process.Environment["PATH"] = directory + (inherited.Length == 0 ? "" : ";" + inherited);
    }
}
