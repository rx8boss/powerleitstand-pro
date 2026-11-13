using System.Runtime.InteropServices;
using Microsoft.Management.Deployment;

namespace PowerControlProWpf;

internal static class ProgramDescriptionMetadataReader
{
    private static readonly SemaphoreSlim Gate = new(1);
    private static int _unavailable;
    internal static async Task<ProgramDescriptionMetadata?> ReadAsync(string id, string version, string language, CancellationToken token)
        => await ReadNativeAsync(id, version, language, token).ConfigureAwait(false)
            ?? await ProgramDescriptionManifests.ReadAsync(id, version, language, token).ConfigureAwait(false);
    // Catalog-only COM work stays off the WPF thread. A timed-out native worker keeps
    // its gate until it really finishes, so repeated clicks cannot spawn a COM storm.
    private static async Task<ProgramDescriptionMetadata?> ReadNativeAsync(string id, string version, string language, CancellationToken token)
    {
        if (Volatile.Read(ref _unavailable) != 0) return null;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            await Gate.WaitAsync(limit.Token).ConfigureAwait(false);
            var work = Task.Run(() =>
            {
                try { return Read(id, version, language); }
                catch (COMException e) when (e.HResult == unchecked((int)0x80040154))
                { Interlocked.Exchange(ref _unavailable, 1); return null; }
                catch (Exception e) when (e is COMException or TypeLoadException or DllNotFoundException or InvalidOperationException or NotSupportedException or System.IO.IOException)
                { return null; }
                finally { Gate.Release(); }
            });
            return await work.WaitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
    }
    private static ProgramDescriptionMetadata? Read(string id, string version, string language)
    {
        var manager = new PackageManager();
        var reference = manager.GetPackageCatalogByName("winget");
        if (reference is null) return null;
        var connection = reference.Connect();
        if (connection.Status != ConnectResultStatus.Ok) return null;
        var options = new FindPackagesOptions { ResultLimit = 2 };
        options.Filters.Add(new PackageMatchFilter { Field = PackageMatchField.Id, Option = PackageFieldMatchOption.EqualsCaseInsensitive, Value = id });
        var result = connection.PackageCatalog.FindPackages(options);
        if (result.Status != FindPackagesResultStatus.Ok || result.WasLimitExceeded || result.Matches.Count != 1) return null;
        var package = result.Matches[0].CatalogPackage;
        if (!string.Equals(package.Id, id, StringComparison.OrdinalIgnoreCase)) return null;
        var versions = package.AvailableVersions.Where(v => v.Version == version).ToArray();
        if (versions.Length != 1) return null;
        var info = package.GetPackageVersionInfo(versions[0]);
        if (info is null || info.Version != version) return null;
        // ApplyLocale merges missing fields from the default localization. Locale alone
        // therefore cannot prove that an inherited long Description was translated.
        // "und" requests no particular language and keeps WinGet's default localization.
        var baseline = info.GetCatalogPackageMetadata("und");
        var localized = info.GetCatalogPackageMetadata(language == "de" ? "de-DE" : "en-US");
        return Select(baseline.Locale, baseline.Description, baseline.ShortDescription,
            localized.Locale, localized.Description, localized.ShortDescription);
    }
    internal static ProgramDescriptionMetadata? Select(string? baselineLocale, string? baselineLong, string? baselineShort,
        string? targetLocale, string? targetLong, string? targetShort)
    {
        static string? Value(string? value) => string.IsNullOrWhiteSpace(value) || value.Length > 32768 ? null : value.Trim();
        baselineLong = Value(baselineLong); baselineShort = Value(baselineShort);
        targetLong = Value(targetLong); targetShort = Value(targetShort);
        var baseline = baselineLong ?? baselineShort;
        var translated = ProgramDescriptions.Language(baselineLocale) == ProgramDescriptions.Language(targetLocale)
            ? targetLong ?? targetShort
            : targetLong is not null && targetLong != baselineLong ? targetLong
            : targetShort is not null && targetShort != baselineShort ? targetShort : null;
        var original = baseline is not null ? new ProgramDescriptionText(baseline, baselineLocale)
            : translated is not null ? new(translated, targetLocale) : null;
        return original is null ? null : new(original, translated is null ? null : new(translated, targetLocale));
    }
}
