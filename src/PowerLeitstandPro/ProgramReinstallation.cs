using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace PowerControlProWpf;

internal enum ProgramReinstallRoute { Unavailable, WinGet, Portable, GitHub, Provider, Managed }
internal sealed record ProgramReinstallCapability(ProgramReinstallRoute Route, string Reason, string Source,
    string? PackageId = null, string? InstalledVersion = null, string? PackageVersion = null,
    string? Path = null, string? Variant = null, string? Scope = null, VlcRegisteredCopy? VlcCopy = null)
{
    internal bool Automatic => Route is ProgramReinstallRoute.WinGet or ProgramReinstallRoute.Portable or ProgramReinstallRoute.GitHub or ProgramReinstallRoute.Managed;
}
internal sealed record ProgramReinstallProof(ProgramReinstallCapability Installed, bool SameVersion, string? VlcManifestHash = null);

internal sealed partial class ProgramInstallService
{
    // One decision for catalog entries AND approved online results. Completeness of an unrelated
    // source (e.g. Store when targeting WinGet) is not evidence against this particular copy.
    internal ProgramReinstallCapability ReinstallCapability(ProgramEntry entry, ProgramInventory inventory)
    {
        var proof = inventory.Evidence?.GetValueOrDefault(entry.Id) ?? _lastEvidence.GetValueOrDefault(entry.Id);
        var source = inventory.InstalledSources?.GetValueOrDefault(entry.Id) ?? proof?.Source ?? "Unknown";
        ProgramReinstallCapability Result(ProgramReinstallRoute route, string reason, string? package = null) =>
            new(route, reason, source, package, ProgramVersions.ConfirmedVersion(inventory.Installed.GetValueOrDefault(entry.Id)),
                package is null ? null : ProgramVersions.ConfirmedVersion(_recentExport.GetValueOrDefault(package)
                    ?? _installedPackageVersions.GetValueOrDefault(package)), proof?.Path, proof?.Variant, proof?.LocationEvidence?.Scope);
        if (!Approved(entry) || !inventory.Installed.ContainsKey(entry.Id)) return Result(ProgramReinstallRoute.Unavailable, "not-confirmed");
        if (inventory.IsPartial || inventory.IsStale || inventory.UnverifiedInstalledIds?.Contains(entry.Id) == true) return Result(ProgramReinstallRoute.Unavailable, "checking");
        if (source == "Windows-App") return Result(ProgramReinstallRoute.Provider, inventory.StoreCheckComplete ? "store" : "store-unconfirmed");
        if (proof is null) return Result(ProgramReinstallRoute.Provider, "source-unconfirmed");
        if (inventory.ManagedPackages?.GetValueOrDefault(entry.Id)?.Any(ManagedReinstallSupported) == true)
            return Result(ProgramReinstallRoute.Managed, "verified-managed");
        if (_ambiguousReinstallIds.Contains(entry.Id) && entry.Id != "vlc") return Result(ProgramReinstallRoute.Provider, "multiple-copies");
        if (proof.ManagedCopies is { Count: > 0 } managed) return managed.Count == 1
            ? Result(ProgramReinstallRoute.Provider, "package-manager") with { Source = managed[0].Manager }
            : Result(ProgramReinstallRoute.Provider, "multiple-copies");
        if (source is "Scoop" or "Chocolatey") return Result(ProgramReinstallRoute.Provider, "package-manager");
        // Preserve the user's explicit EA choice: no forced WinGet uninstall/reinstall.
        if (ProgramPublisherLauncher.RequiresPublisherUpdate(entry.Id, proof)) return Result(ProgramReinstallRoute.Provider, "publisher");
        if (ProgramGitHubUpdates.Eligible(entry.Id, proof)) return Result(ProgramReinstallRoute.GitHub, "verified-github", entry.Id);
        if (ProgramPortableUpdates.Eligible(entry.Id, proof)) return entry.Id == "tukui-client" && !_portableUpdates.HasTukuiPackage(proof)
            ? Result(ProgramReinstallRoute.Provider, "source-unconfirmed") : Result(ProgramReinstallRoute.Portable, "verified-portable", entry.Id);
        if (source is "Portable" or "Portable-Setup" || !proof.WinGetCompatible)
            return Result(ProgramReinstallRoute.Provider, "source-unconfirmed");
        if (entry.Id == "vlc" && _vlcCopies is not null)
        {
            var copy = ProgramVlcReinstallation.Select(proof, _vlcCopies());
            return copy is null ? Result(ProgramReinstallRoute.Provider, "vlc-copy-unconfirmed")
                : Result(ProgramReinstallRoute.WinGet, "verified-winget", entry.PackageId) with { VlcCopy = copy, PackageVersion = copy.Version };
        }
        if (entry.PackageId is null || !_installedPackageIds.TryGetValue(entry.Id, out var id) || !PackageMatches(entry, id))
            return Result(ProgramReinstallRoute.Provider, "package-unconfirmed");
        return Result(ProgramReinstallRoute.WinGet, "verified-winget", id);
    }

    internal async Task<IReadOnlyList<ProgramPackage>> ResolveReinstallOptionsAsync(ProgramEntry entry, ProgramInventory inventory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var target = ReinstallCapability(entry, inventory);
        if (!target.Automatic) return [];
        var packages = new List<ProgramPackage>();
        if (target.Route == ProgramReinstallRoute.Managed)
        {
            if (_managedPackages is not null && _lastEvidence.GetValueOrDefault(entry.Id) is { } managed)
                foreach (var offered in (await Task.Run(() => _managedPackages.ExistingAsync(entry, managed, token, fresh: true), token)).Where(ManagedReinstallSupported))
                    packages.Add(offered.Package() with { ReinstallProof = new(target, offered.Version == offered.Target.OldVersion) });
        }
        else if (target.Route == ProgramReinstallRoute.WinGet)
        {
            // Package-manager version, not a possibly different EXE product version.
            string? old = target.PackageVersion;
            if (old is not null)
            {
                var same = await ReadOption(old);
                if (same is not null) packages.Add(same);
            }
            var latest = await ReadOption(null);
            if (latest is not null && (old is null || latest.Version == old || ProgramVersions.IsNewer(old, latest.Version))
                && packages.All(p => p.Version != latest.Version))
                packages.Add(latest);
            async Task<ProgramPackage?> ReadOption(string? version)
            {
                try
                {
                    var offered = await ReadReinstallWinGetAsync(entry, version, token);
                    if (offered is null) return null;
                    string? hash = target.VlcCopy is { } copy ? await _vlcManifest(offered, copy, token) : null;
                    if (target.VlcCopy is not null && hash is null) return null;
                    return offered with { ReinstallProof = new(target, target.PackageVersion is { } oldVersion && ProgramPortableUpdates.SameVersion(oldVersion, offered.Version!), hash) };
                }
                catch (Exception e) when (e is IOException or System.Net.Http.HttpRequestException or System.ComponentModel.Win32Exception or OperationCanceledException)
                { token.ThrowIfCancellationRequested(); return null; }
            }
        }
        else if (_lastEvidence.GetValueOrDefault(entry.Id) is { } local)
        {
            if (target.Route == ProgramReinstallRoute.Portable)
            {
                var direct = await _portableUpdates.ResolveAsync(entry.Id, local, token, fresh: true);
                if (direct is not null && (ProgramPortableUpdates.SameVersion(direct.InstalledVersion, direct.Version)
                    || ProgramVersions.IsNewer(direct.InstalledVersion, direct.Version)))
                    packages.Add(new(entry.PackageId ?? entry.Id, direct.Version, direct.Url, DateTime.UtcNow,
                        VersionSource: "Publisher", PortableUpdate: direct,
                        ReinstallProof: new(target, ProgramPortableUpdates.SameVersion(direct.InstalledVersion, direct.Version))));
            }
            else
            {
                var direct = await ProgramGitHubUpdates.ResolveAsync(entry, local, token, reinstall: true);
                // WiX's same-version action is repair; do not present it as a full reinstall.
                if (direct is not null && !(direct.Kind == "WiX" && ProgramPortableUpdates.SameVersion(direct.InstalledVersion, direct.Version)))
                    packages.Add(new(entry.Id, direct.Version, direct.Url, DateTime.UtcNow, VersionSource: "GitHub", GitHubUpdate: direct,
                        ReinstallProof: new(target, ProgramPortableUpdates.SameVersion(direct.InstalledVersion, direct.Version))));
            }
        }
        token.ThrowIfCancellationRequested();
        if (target.VlcCopy is { } selectedCopy && packages.Count == 0)
            throw new IOException("Passender VLC-Installer für " + selectedCopy.Label + " nicht bestätigt. Keine andere Variante wurde gestartet. / Matching VLC installer is unconfirmed. No different variant was started.");
        return packages;
    }

    private async Task<ProgramPackage?> ReadReinstallWinGetAsync(ProgramEntry entry, string? version, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var result = await _query(Command(entry, false, version), timeout.Token);
        return ParseReinstallWinGet(PackageId(entry)!, version, result);
    }
    internal static ProgramPackage? ParseReinstallWinGet(string id, string? expectedVersion, WinGetResult result)
    {
        if (result.ExitCode != 0 || !result.Output.Contains("[" + id + "]", StringComparison.OrdinalIgnoreCase)) return null;
        var matches = Regex.Matches(result.Output, @"(?im)^\s*Version\s*:\s*([A-Za-z0-9._+\-]+)\s*$");
        if (matches.Count != 1) return null;
        string version = matches[0].Groups[1].Value;
        if (ProgramVersions.ConfirmedVersion(version) is null || expectedVersion is not null && version != expectedVersion) return null;
        return new(id, version, result.Output, DateTime.UtcNow, VersionSource: "WinGet");
    }
    internal bool ReinstallStillMatches(ProgramEntry entry, ProgramPackage package, ProgramInventory inventory) =>
        package.ReinstallProof is { } proof && proof.Installed.Automatic && package.CanInstall && package.Version is not null
        && proof.Installed == ReinstallCapability(entry, inventory)
        && (proof.Installed.Route != ProgramReinstallRoute.WinGet || string.Equals(package.Id, proof.Installed.PackageId, StringComparison.OrdinalIgnoreCase))
        && (package.ManagedPackage is null || _managedPackages?.Matches(entry, package.ManagedPackage, inventory.Evidence) == true);

    internal bool ReinstallResultVerified(ProgramEntry entry, ProgramPackage package, ProgramInventory inventory)
    {
        if (package.ReinstallProof is not { } selected || !inventory.Installed.ContainsKey(entry.Id)) return false;
        if (package.ManagedPackage is { } managed) return ProgramManagedPackages.ResultVerified(managed, inventory, entry.Id);
        if (selected.Installed.VlcCopy is { } copy)
        {
            if (inventory.IsStale || inventory.IsPartial || inventory.UnverifiedInstalledIds?.Contains(entry.Id) == true || _vlcCopies is null) return false;
            var matches = _vlcCopies().Where(c => ProgramVlcReinstallation.SamePlace(copy, c)).ToArray();
            return matches.Length == 1 && package.Version is not null && ProgramPortableUpdates.SameVersion(matches[0].Version, package.Version);
        }
        var current = ReinstallCapability(entry, inventory); var previous = selected.Installed;
        if (current.Route != previous.Route || current.Source != previous.Source || current.PackageId != previous.PackageId
            || !string.Equals(current.Path, previous.Path, StringComparison.OrdinalIgnoreCase) || current.Scope != previous.Scope) return false;
        string? version = current.Route == ProgramReinstallRoute.WinGet ? current.PackageVersion : current.InstalledVersion;
        return version is not null && (version == package.Version || ProgramPortableUpdates.SameVersion(version, package.Version!));
    }

    internal string ReinstallFailureReason(ProgramEntry entry, ProgramPackage package, ProgramInventory inventory, bool english)
    {
        string L(string de, string en) => english ? en : de;
        if (package.ReinstallProof?.Installed.VlcCopy is { } copy)
            return L("Die ausgewählte VLC-Kopie wurde nach der Neuinstallation nicht eindeutig mit der Zielversion bestätigt: ", "The selected VLC copy was not uniquely verified at the target version after reinstallation: ") + copy.Label;
        var current = ReinstallCapability(entry, inventory); var old = package.ReinstallProof?.Installed;
        return current.Reason == "checking" ? L("Die abschließende Installationsprüfung ist unvollständig.", "The final installation check is incomplete.")
            : old is not null && !string.Equals(current.Path, old.Path, StringComparison.OrdinalIgnoreCase)
            ? L("Der erkannte Speicherort hat sich geändert: ", "The detected location changed: ") + (old.Path ?? "?") + " → " + (current.Path ?? "?")
            : current.Route != old?.Route || current.Source != old?.Source || current.PackageId != old?.PackageId || current.Scope != old?.Scope
            ? L("Die erkannte Installationsquelle oder Kopie hat sich geändert. Bitte den Speicherort und die Installerart prüfen.", "The detected installation source or copy changed. Check the location and installer type.")
            : L("Die Zielversion wurde für die ausgewählte Installationskopie nicht bestätigt.", "The target version was not verified for the selected installation copy.");
    }

    internal static string ReinstallExplanation(ProgramReinstallCapability capability, bool english) => capability.Reason switch
    {
        "checking" => english ? "Installation is still being checked." : "Die Installation wird noch geprüft.",
        "store" => english ? "Use Microsoft Store / Windows app settings for this Store installation. Repair and reinstall are different actions." : "Für diese Store-Installation Microsoft Store bzw. Windows-App-Einstellungen verwenden. Reparatur und Neuinstallation sind unterschiedliche Aktionen.",
        "store-unconfirmed" => english ? "Store check incomplete. Check again before reinstalling." : "Store-Prüfung unvollständig. Vor der Neuinstallation erneut prüfen.",
        "package-manager" => english ? "Use the existing " + capability.Source + " installation. A compatible automatic reinstallation is not confirmed." : "Die vorhandene " + capability.Source + "-Installation verwenden. Eine passende automatische Neuinstallation ist nicht bestätigt.",
        "multiple-copies" => english ? "Several installation copies were detected. Select the existing source before reinstalling." : "Mehrere Installationskopien erkannt. Vor der Neuinstallation die vorhandene Quelle zuordnen.",
        "vlc-copy-unconfirmed" => english ? "VLC architecture, installer type and location are not uniquely confirmed. Check the selected executable; no automatic switch between EXE/MSI or 32/64-bit copies." : "VLC-Architektur, Installerart und Speicherort sind nicht eindeutig bestätigt. Bitte die zugeordnete Programmdatei prüfen; kein automatischer Wechsel zwischen EXE/MSI oder 32-/64-Bit-Kopien.",
        "publisher" => english ? "Reinstall through the publisher. EA updates continue through EA App." : "Über den Hersteller neu installieren. EA-Updates erfolgen weiterhin über die EA App.",
        "verified-portable" => english ? "Download the verified publisher files again at the detected location; preserve settings." : "Bestätigte Herstellerdateien am erkannten Speicherort erneut herunterladen; Einstellungen erhalten.",
        "verified-github" => english ? "Reinstall the verified official GitHub package for the detected copy." : "Bestätigtes offizielles GitHub-Paket für die erkannte Kopie erneut installieren.",
        "verified-winget" => english ? "Select an available version for the confirmed WinGet installation." : "Eine verfügbare Version für die bestätigte WinGet-Installation auswählen.",
        "verified-managed" => english ? "Reinstall through the confirmed existing package manager; retain its source and settings." : "Über den bestätigten vorhandenen Paketmanager erneut installieren; Quelle und Einstellungen erhalten.",
        _ => english ? "The existing installation source is not confirmed. Use the publisher website; no automatic source switch." : "Die vorhandene Installationsquelle ist nicht bestätigt. Herstellerseite verwenden; kein automatischer Quellenwechsel."
    };
}
