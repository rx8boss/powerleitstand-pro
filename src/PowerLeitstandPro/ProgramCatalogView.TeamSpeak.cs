using System.IO;
using System.Windows;

namespace PowerControlProWpf;

internal sealed partial class ProgramCatalogView
{
    internal async Task InstallTeamSpeakAsync(TeamSpeakInstallRequest request)
    {
        if (!CanChooseProgramFile) return;
        if (TeamSpeakInstallPolicy.Decide(request.Edition, request.Version, request.Snapshot, out _) != request.Kind) return;
        string action = request.ReinstallRequested ? L("Neu installieren · aktuelle Herstellerversion", "Reinstall · current publisher version")
            : request.Kind switch { TeamSpeakInstallKind.Update => L("Update installieren", "Install update"), TeamSpeakInstallKind.Reinstall => L("Neu installieren", "Reinstall"), _ => L("Installieren", "Install") };
        string question = "TeamSpeak " + request.Edition + " · " + request.Version + "\n" + action + "?\n\n"
            + L("Download direkt vom Hersteller. Mit der Installation akzeptierst du die TeamSpeak-Lizenzbedingungen auf der Herstellerseite. Administratorabfragen bleiben sichtbar. Ein gestarteter Installer wird nicht gewaltsam beendet.", "Download directly from the publisher. By installing, you accept the TeamSpeak license terms on the publisher website. Administrator prompts remain visible. A running installer will not be forcibly stopped.")
            + "\nhttps://www.teamspeak.com/en/downloads/"
            + (request.Kind == TeamSpeakInstallKind.Update ? L("\n\nVor dem Update wird die zugehörige TeamSpeak-Instanz geordnet geschlossen. Offene Arbeit bitte speichern.", "\n\nThe matching TeamSpeak instance will be closed gracefully before updating. Please save your work.") : "")
            + (request.Kind == TeamSpeakInstallKind.Switch ? L("\n\nAusgabe-/Kanalwechsel: Die vorhandene TeamSpeak-5-/6-Installation kann durch TeamSpeak 6 ersetzt werden. Dies ist kein gewöhnliches Update. Einstellungen und Konten vorher prüfen.", "\n\nEdition/channel switch: the existing TeamSpeak 5/6 installation may be replaced by TeamSpeak 6. This is not a regular update. Check settings and accounts first.") : "")
            + (request.Version.Contains("-beta", StringComparison.OrdinalIgnoreCase) ? L("\n\nZiel ist eine Betaversion.", "\n\nThe target is a beta release.") : "");
        SelectionOpen = true; bool confirmed;
        try { confirmed = request.Kind == TeamSpeakInstallKind.Update && !request.ReinstallRequested
            || (ConfirmForTests?.Invoke(question) ?? ThemedMessageBox.Show(Window.GetWindow(this), question, "TeamSpeak", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes); }
        finally { SelectionOpen = false; }
        if (!confirmed || !CanChooseProgramFile) return;
        var original = ProgramCatalog.Entries.Single(x => x.Id == "teamspeak");
        var entry = original with { Name = "TeamSpeak " + request.Edition };
        SelectionOpen = true;
        try { await PauseBackgroundInspectionAsync(); }
        finally { SelectionOpen = false; }
        if (!CanChooseProgramFile) return;
        if (Service is ProgramInstallService inspection) inspection.InvalidateSaved("teamspeak");
        IsWorking = true; _stopping = false; _prepare = new(); CloseInfo(); WorkStateChanged?.Invoke(true); RefreshAvailability();
        try
        {
            OperationPanel?.Dispose(); OperationPanel = new ProgramProgressPanel(this, [entry]);
            _phase = L("TeamSpeak-Installer prüfen …", "Checking TeamSpeak installer …");
            OperationPanel.Current(entry, _phase); OperationPanel.Step(entry.Id, new(ProgramStep.Check, ProgramStepState.Active));
            var plan = await TeamSpeakInstaller.PrepareAsync(request, _prepare.Token);
            if (request.Kind == TeamSpeakInstallKind.Update && request.Copy is { } copy && Service is ProgramInstallService closer)
            {
                _phase = L("Programm wird beendet: ", "Closing application: ") + entry.Name; OperationPanel.Current(entry, _phase); RefreshAvailability();
                if (await closer.CloseForUpdateAsync(original, new(copy.Version, copy.Source, copy.Path, false), _prepare.Token))
                    plan = await TeamSpeakInstaller.PrepareAsync(request, _prepare.Token);
                _prepare.Token.ThrowIfCancellationRequested();
            }
            void Step(ProgramStepEvent value) => Dispatcher.Invoke(() =>
            {
                OperationPanel?.Step(entry.Id, value);
                string stage = value.Step switch { ProgramStep.Check => L("Prüfung", "Check"), ProgramStep.Download => "Download", ProgramStep.Install => L("Installation", "Installation"), _ => L("Abschlussprüfung", "Verification") };
                if (value.State == ProgramStepState.Done) WriteLog?.Invoke(entry.Name + ": " + stage + L(" bestätigt.", " verified."));
            });
            var result = await TeamSpeakInstaller.ApplyAsync(plan, Step, value => Dispatcher.Invoke(() => OperationPanel?.Download(value)), _prepare.Token);
            OperationPanel.Location(entry.Id, result.Path);
            if (result.Verified && result.ExitCode == 0)
            {
                var installed = Inventory.Installed.ToDictionary(x => x.Key, x => x.Value); installed[entry.Id] = request.Version;
                var evidence = Inventory.Evidence?.ToDictionary(x => x.Key, x => x.Value) ?? [];
                evidence[entry.Id] = new(request.Version, "Registry", result.Path, WinGetCompatible: false);
                Inventory = Inventory with { Installed = installed, Evidence = evidence, Updates = Inventory.Updates?.Where(x => x.Key != entry.Id).ToDictionary(x => x.Key, x => x.Value),
                    GitHubUpdates = Inventory.GitHubUpdates?.Where(x => x.Key != entry.Id).ToDictionary(x => x.Key, x => x.Value), UpdateCheckComplete = false, FromCache = false };
                if (request.Kind is TeamSpeakInstallKind.Install or TeamSpeakInstallKind.Switch) RecordInstalled(entry.Id);
                Selected.Remove(entry.Id); OperationPanel.State(entry.Id, L("Bestätigt", "Verified"));
            }
            else OperationPanel.State(entry.Id, result.ExitCode == 3010 ? L("Neustart erforderlich", "Restart required") : L("Nicht bestätigt · siehe Protokoll", "Not verified · see log"));
            WriteLog?.Invoke(entry.Name + L(": Ausführung beendet, Code ", ": execution finished, code ") + "0x" + result.ExitCode.ToString("X8")
                + (result.Verified ? L(" · Zielversion bestätigt", " · target verified") : L(" · Zielversion nicht bestätigt", " · target not verified")));
            if (result.Reason is { } reason) WriteLog?.Invoke(entry.Name + ": " + UiText.Translate(reason, English));
        }
        catch (OperationCanceledException) { _stopping = true; OperationPanel?.State(entry.Id, L("Gestoppt", "Stopped")); WriteLog?.Invoke(L("TeamSpeak-Download gestoppt.", "TeamSpeak download stopped.")); }
        catch (Exception error) { OperationPanel?.State(entry.Id, L("Fehler · siehe Protokoll", "Error · see log")); WriteLog?.Invoke(UiText.Error(error, English)); }
        finally { _prepare.Dispose(); _prepare = null; IsWorking = false; OperationPanel?.Finish(_stopping); WorkStateChanged?.Invoke(false); RenderTiles(); }
    }
}
