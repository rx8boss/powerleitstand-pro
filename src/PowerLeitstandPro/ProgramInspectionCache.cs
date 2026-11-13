using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PowerControlProWpf;

// Display data only. Never serialize installer commands, manifests, receipts or executable offers.
internal sealed class ProgramInspectionCache(string file)
{
    internal const int MaxBytes = 1024 * 1024;
    internal sealed record Row(string Id, string CatalogKey, string? Version, string Source, string? Path,
        string? Variant, string Identity, string? Target);
    internal sealed record Document(int Schema, DateTime CheckedUtc, Row[] Rows, bool Complete = true);
    internal string? LastError { get; private set; }
    internal static string CatalogKey(ProgramEntry entry) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('\n', entry.Id, entry.PackageId, entry.Name, entry.Publisher, entry.Website))));
    internal static string Identity(ProgramInstallation proof) => string.Join('|', proof.Source, proof.Variant,
        proof.LocationEvidence?.Scope, proof.LocationEvidence?.Source,
        string.Join(';', (proof.ManagedCopies ?? []).Select(p => string.Join(':', p.Manager, p.PackageId, p.Scope, p.ManagerRoot)).Order()));
    private void PlainStorage()
    {
        if (!Path.IsPathFullyQualified(file) || file.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("Invalid cache path");
        for (string? part = Path.GetFullPath(file); part is not null; part = Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked cache storage is not used");
    }
    private Document? ReadFile(IEnumerable<ProgramEntry> entries, DateTime now)
    {
        LastError = null;
        try
        {
            if (!File.Exists(file)) return null;
            PlainStorage(); if (new FileInfo(file).Length > MaxBytes) throw new IOException("Cache too large");
            var doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(file));
            if (doc is null || doc.Schema != 1 || doc.Rows is null || doc.Rows.Length > 512
                || doc.CheckedUtc.Kind != DateTimeKind.Utc || doc.CheckedUtc > now.AddMinutes(5) || now - doc.CheckedUtc > TimeSpan.FromDays(30)) return null;
            var known = entries.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
            bool Text(string? value, int length = 2048) => value is null || value.Length <= length && !value.Any(char.IsControl);
            if (doc.Rows.Any(r => r is null || string.IsNullOrWhiteSpace(r.Id) || !Text(r.Id, 256)
                || !Text(r.CatalogKey, 64) || string.IsNullOrWhiteSpace(r.Source) || !Text(r.Source, 64)
                || !Text(r.Path) || r.Path is not null && (!Path.IsPathFullyQualified(r.Path) || r.Path.StartsWith(@"\\", StringComparison.Ordinal))
                || !Text(r.Variant) || !Text(r.Identity, 8192)
                || r.Version is not null && ProgramVersions.ConfirmedVersion(r.Version) is null
                || r.Target is not null && ProgramVersions.ConfirmedVersion(r.Target) is null)
                || doc.Rows.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != doc.Rows.Length) return null;
            return doc with { Rows = doc.Rows.Where(r => known.TryGetValue(r.Id, out var e) && r.CatalogKey == CatalogKey(e)).ToArray() };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { LastError = e.Message; return null; }
    }
    internal Document? Read(IEnumerable<ProgramEntry> entries, DateTime now)
    {
        var known = entries.ToArray(); var complete = ReadFile(known, now);
        var partial = new ProgramInspectionCache(file + ".partial").ReadFile(known, now);
        if (partial is null || complete is not null && complete.CheckedUtc > partial.CheckedUtc) return complete;
        return partial;
    }
    internal static ProgramInventory Display(Document doc)
    {
        var evidence = doc.Rows.ToDictionary(r => r.Id, r => new ProgramInstallation(r.Version, r.Source, r.Path, false, r.Variant));
        return new(false, doc.Rows.ToDictionary(r => r.Id, r => r.Version), Evidence: evidence,
            InstalledSources: evidence.ToDictionary(p => p.Key, p => p.Value.Source), CheckedUtc: doc.CheckedUtc,
            FromCache: true, IsStale: true, StoreCheckComplete: false,
            PendingUpdates: doc.Rows.Where(r => r.Target is not null).ToDictionary(r => r.Id, r => r.Target!), SavedPartial: !doc.Complete);
    }
    internal bool Save(ProgramInventory inventory, IEnumerable<ProgramEntry> entries)
    {
        if (!inventory.Complete || !inventory.UpdateCheckComplete || !inventory.StoreCheckComplete || !inventory.UpdateSourcesComplete || inventory.IsPartial || inventory.IsStale
            || inventory.Error is not null || inventory.OnlineError is not null || inventory.StoreError is not null || inventory.CheckedUtc is null) return false;
        var rows = entries.Where(e => inventory.Installed.ContainsKey(e.Id)).Take(513).Select(e =>
        {
            var proof = inventory.Evidence?.GetValueOrDefault(e.Id) ?? new(inventory.Installed[e.Id], "WinGet");
            return new Row(e.Id, CatalogKey(e), inventory.Installed[e.Id], proof.Source, proof.Path, proof.Variant, Identity(proof),
                inventory.Updates?.GetValueOrDefault(e.Id) ?? inventory.GitHubUpdates?.GetValueOrDefault(e.Id));
        }).ToArray();
        if (rows.Length > 512) return false;
        bool written = Write(new(1, inventory.CheckedUtc.Value, rows));
        if (written) try
        {
            var partial = file + ".partial";
            if (File.Exists(partial) && ProgramPortableUpdates.PlainPath(partial)) File.Delete(partial);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        return written;
    }
    internal bool SaveConfirmedParts(ProgramInventory inventory, IEnumerable<ProgramEntry> entries)
    {
        if (inventory.IsStale || inventory.IsPartial || inventory.CheckedUtc is null || inventory.Evidence is not { Count: > 0 }) return false;
        var known = entries.ToArray(); var previous = Read(known, DateTime.UtcNow);
        var rows = previous?.Rows.ToDictionary(r => r.Id) ?? [];
        foreach (var entry in known)
        {
            if (!inventory.Evidence.TryGetValue(entry.Id, out var proof) || !inventory.Installed.ContainsKey(entry.Id)) continue;
            var identity = Identity(proof); var old = rows.GetValueOrDefault(entry.Id);
            if (old is not null && old.Target is not null && (old.Identity != identity
                || proof.Source is not ("Scoop" or "Chocolatey") && !string.Equals(old.Path, proof.Path, StringComparison.OrdinalIgnoreCase))) continue;
            var target = inventory.Updates?.GetValueOrDefault(entry.Id) ?? inventory.GitHubUpdates?.GetValueOrDefault(entry.Id) ?? old?.Target;
            var version = inventory.Installed[entry.Id];
            var comparison = proof.ManagedCopies is { Count: 1 } copies ? copies[0].PackageVersion : version;
            if (target is not null && ProgramInstallService.CurrentEnough(comparison, target)) target = null;
            rows[entry.Id] = new(entry.Id, CatalogKey(entry), version, proof.Source, proof.Path, proof.Variant, identity, target);
        }
        if (rows.Count is 0 or > 512) return false;
        // Separate bounded file: failed sources cannot overwrite the last complete snapshot.
        return new ProgramInspectionCache(file + ".partial").Write(new(1, inventory.CheckedUtc.Value, rows.Values.ToArray(), false));
    }
    internal bool Write(Document document)
    {
        LastError = null; string? temporary = null;
        try
        {
            PlainStorage(); var bytes = JsonSerializer.SerializeToUtf8Bytes(document);
            if (bytes.Length > MaxBytes) throw new IOException("Cache too large");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, bytes); PlainStorage(); File.Move(temporary, file, true); return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { LastError = e.Message; return false; }
        finally { if (temporary is not null) try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    internal void Invalidate(string? id, IEnumerable<ProgramEntry> entries)
    {
        var known = entries.ToArray();
        var document = ReadFile(known, DateTime.UtcNow);
        // An interrupted operation must not resurrect old update candidates on restart.
        if (document is not null) Write(document with { CheckedUtc = id is null ? DateTime.UnixEpoch : document.CheckedUtc,
            Rows = id is null ? [] : document.Rows.Where(r => r.Id != id).ToArray() });
        var partialCache = new ProgramInspectionCache(file + ".partial");
        var partial = partialCache.ReadFile(known, DateTime.UtcNow);
        if (partial is not null) partialCache.Write(partial with { CheckedUtc = id is null ? DateTime.UnixEpoch : partial.CheckedUtc,
            Rows = id is null ? [] : partial.Rows.Where(r => r.Id != id).ToArray() });
    }
    internal bool WriteLocal(Document document) => new ProgramInspectionCache(file + ".partial").Write(document with { Complete = false });
}
