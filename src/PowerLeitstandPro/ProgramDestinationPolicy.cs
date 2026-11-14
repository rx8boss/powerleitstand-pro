namespace PowerControlProWpf;

// A destination is an installer capability, never inferred just from an EXE name.
internal static class ProgramDestinationPolicy
{
    internal static string? Restriction(ProgramSelection option, ProgramInventory inventory, bool english, bool existingInstallation = false)
    {
        string L(string de, string en) => english ? en : de;
        if (option.Reinstall || existingInstallation || inventory.Installed.ContainsKey(option.Entry.Id) || option.Installer?.PortableUpdate is not null)
            return L("Update und Neuinstallation verwenden den vorhandenen Ort. Ein Ortswechsel benötigt einen gesonderten Umzug; es wird nichts automatisch verschoben.",
                "Updates and reinstalls use the existing location. Relocation needs a separate move; nothing is moved automatically.");
        if (option.Installer?.ManagedPackage is { } managed)
            return L("Der Installationsort wird von ", "The installation location is managed by ") + managed.Manager
                + L(" verwaltet. Eine freie Ordnerwahl ist für dieses Paket nicht bestätigt.", ". Custom folders are not confirmed for this package.");
        if (option.Installer is null)
            return L("Die Installation erfolgt über die Herstellerseite. Den Zielordner bestimmt der dort angebotene Installer.",
                "Installation uses the publisher website. Its installer determines the destination.");
        if (option.Installer is not { CanInstall: true, VersionSource: "WinGet", TargetProof: { SupportsLocation: true }, GitHubUpdate: null, PortableUpdate: null, ManagedPackage: null })
            return L("Freie Ordnerwahl für diesen Installer nicht bestätigt. Es wird der vom Hersteller oder Windows verwaltete Standardort verwendet.",
                "Custom folders are not confirmed for this installer. The publisher or Windows manages the default location.");
        if (option.Installer.TargetProof.PackageId != option.Installer.Id || option.Installer.TargetProof.Version != option.Installer.Version)
            return L("Zielordner-Unterstützung passt nicht zur ausgewählten Version. Bitte erneut prüfen.", "Destination support does not match the selected version. Check again.");
        return null;
    }

    internal static string? ExistingDirectory(ProgramSelection option, ProgramInventory inventory)
        => ProgramLocation.DirectoryOf(inventory.Evidence?.GetValueOrDefault(option.Entry.Id)?.Path
            ?? option.Installer?.PortableUpdate?.Path ?? option.Installer?.GitHubUpdate?.Path);
}
