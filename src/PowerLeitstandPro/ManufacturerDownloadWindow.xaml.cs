using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;

namespace PowerControlProWpf;

public partial class ManufacturerDownloadWindow : Window
{
    private readonly ManufacturerApp _app;
    private readonly IManufacturerService _service;
    private readonly bool _english;
    private readonly IWinGetService? _winGet;
    private WinGetPackage? _package;
    private readonly CancellationTokenSource _cancel = new();
    private ManufacturerDownload? _download;
    private bool _running, _finished, _installer;
    internal bool StartedInstaller { get; private set; }
    internal Exception? Failure { get; private set; }
    private string L(string de, string en) => _english ? en : de;

    internal ManufacturerDownloadWindow(ManufacturerApp app, IManufacturerService service, ThemePalette palette, bool english, IWinGetService? winGet = null)
    {
        InitializeComponent(); _app = app; _service = service; _english = english; _winGet = winGet;
        foreach (var (key, index) in new[] { ("DialogBackground", 0), ("DialogText", 5), ("DialogSubtleText", 6), ("DialogAccent", 7), ("DialogInput", 4) })
            Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Colors[index]));
        DialogAppearance.ApplySurface(this, DialogFrame, palette);
        UnifiedDialogAppearance.Resources(this, palette);
        UnifiedDialogAppearance.Button(this, StartButton, DialogButtonKind.Primary);
        UnifiedDialogAppearance.Button(this, CancelButton, DialogButtonKind.Danger);
        UnifiedDialogAppearance.Button(this, HeaderCloseButton, DialogButtonKind.Close);
        FontFamily = new FontFamily(palette.FontFamily);
        DownloadBar.ApplyPalette(palette);
        HeaderText.Text = L("Hersteller-Software", "Manufacturer software"); VersionText.Text = app.Title(english);
        FileText.Text = new Uri(app.Website).Host; StartButton.Content = L("↓ Herunterladen und starten", "↓ Download and launch");
        CancelCaption.Text = L("Abbrechen", "Cancel");
        HintText.Text = L("Nach Bestätigung: Download, Signaturprüfung und Originalinstaller. Dessen Lizenz- und Windows-Dialoge bleiben erhalten.", "After confirmation: download, signature verification and original installer. Its licence and Windows prompts remain available.");
        PhaseText.Text = L("Gerät und aktuelle Quelle prüfen…", "Checking device and current source…"); StartButton.IsEnabled = false;
        if (_winGet is not null)
        {
            HeaderText.Text = L("Installation mit WinGet", "Installation via WinGet");
            StartButton.Content = L("↓ Mit WinGet installieren", "↓ Install via WinGet");
            HintText.Text = L("Nach Bestätigung öffnet WinGet seine Konsole. Lizenz- und Windows-Abfragen dort beantworten.", "After confirmation WinGet opens its console. Answer licence and Windows prompts there.");
        }
        ContentRendered += async (_, _) => await ResolveAsync();
        Closing += GuardClosing;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { CancelOperation(this, new RoutedEventArgs()); e.Handled = true; } };
        VersionText.Margin = new Thickness(0, 0, 0, 8);
        FileText.Margin = new Thickness(0, 0, 0, 8);
        PhaseText.FontSize = 16; PhaseText.TextWrapping = TextWrapping.Wrap; PhaseText.Margin = new Thickness(0, 0, 0, 12);
        DownloadBar.Height = 26; DownloadBar.Margin = new Thickness(0, 0, 0, 8);
        HintText.FontSize = 13; HintText.Margin = new Thickness(0, 10, 0, 0);
        DialogLayout.Compose(this, DialogFrame, palette, HeaderText.Text, english,
            DialogLayout.Stack(VersionText, FileText, PhaseText, DownloadBar, TransferredText, HintText),
            DialogLayout.Actions(StartButton, CancelButton), HeaderCloseButton);
        Closed += (_, _) => { DownloadBar.IsRunning = false; _cancel.Dispose(); };
    }

    private async Task ResolveAsync()
    {
        _running = true;
        try
        {
            if (_winGet is not null)
            {
                if (!await _winGet.ProbeAsync(_cancel.Token)) throw new IOException(L("WinGet ist nicht verfügbar. Bitte zuerst in der Übersicht einrichten.", "WinGet is not available. Please set it up in the overview first."));
                if (!await _service.CheckWinGetHardwareAsync(_app, _cancel.Token)) throw new IOException(L("Passende Hardware nicht eindeutig bestätigt. Bitte die Herstellerseite verwenden.", "Matching hardware could not be confirmed. Please use the manufacturer website."));
                _package = await _winGet.ResolveAsync(_app.Id, _cancel.Token);
                if (_package is null) throw new IOException(L("Das genaue Paket ist nicht verfügbar. Bitte die Herstellerseite verwenden.", "The exact package is unavailable. Please use the manufacturer website."));
                _cancel.Token.ThrowIfCancellationRequested();
                FileText.Text = _package.Id; FileText.ToolTip = _package.Details;
                PhaseText.Text = L("Bereit · wartet auf Bestätigung", "Ready · awaiting confirmation");
                TransferredText.Text = "Microsoft WinGet · winget";
                DownloadBar.IsRunning = false; DownloadBar.IsIndeterminate = false; DownloadBar.Value = 0; StartButton.IsEnabled = true;
                return;
            }
            _download = await _service.ResolveAsync(_app, _cancel.Token);
            _cancel.Token.ThrowIfCancellationRequested();
            if (_download is null)
            {
                PhaseText.Text = L("Direktinstallation nicht freigegeben", "Direct installation not available");
                HintText.Text = L("Modellunterstützung oder aktueller Downloadweg nicht eindeutig bestätigt. Bitte die Herstellerseite der Karte verwenden.", "Model support or current download source could not be confirmed. Please use the card's manufacturer website.");
                Finish(); return;
            }
            FileText.Text = _download.FileName; PhaseText.Text = L("Bereit · wartet auf Bestätigung", "Ready · awaiting confirmation");
            TransferredText.Text = _download.Source.Host;
            DownloadBar.IsRunning = false; DownloadBar.IsIndeterminate = false; DownloadBar.Value = 0;
            StartButton.IsEnabled = true;
        }
        catch (OperationCanceledException e) { if (_cancel.IsCancellationRequested) { _finished = true; Close(); } else ShowFailure(e); }
        catch (Exception e) { ShowFailure(e); }
        finally { _running = false; }
    }

    private async void StartDownload(object sender, RoutedEventArgs e) => await DownloadAndLaunchAsync();
    internal async Task DownloadAndLaunchAsync()
    {
        if (_winGet is not null) { await InstallViaWinGetAsync(); return; }
        if (_download is null || _running || _finished) return;
        var text = L("Diese Software herunterladen und anschließend den Originalinstaller starten?", "Download this software and then launch its original installer?") +
            "\n\n" + _app.Title(_english) + "\n" + _download.Hardware + "\n" + _download.Source.Host + "\n\n" +
            L("Die Herstellerbedingungen und eventuelle Administratorabfragen werden im Originalinstaller angezeigt. Es werden keine Lizenzbedingungen automatisch bestätigt.", "The manufacturer's licence terms and any administrator prompts appear in the original installer. No licence terms will be accepted automatically.");
        if (ConfirmForTests is not null ? !ConfirmForTests(text) : ThemedMessageBox.Show(this, text, "PowerLeitstand PRO", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _running = true; StartButton.IsEnabled = false; DownloadBar.IsRunning = true;
        try
        {
            var progress = new Progress<ManufacturerProgress>(value =>
            {
                if (_finished || _installer) return;
                PhaseText.Text = value.Phase == "verify" ? L("Herstellerpaket wird geprüft…", "Verifying manufacturer package…") : L("Download läuft…", "Downloading…");
                DownloadBar.IsIndeterminate = value.Phase != "download" || value.Total is not > 0;
                if (!DownloadBar.IsIndeterminate) DownloadBar.Value = Math.Clamp(value.Bytes * 100d / value.Total!.Value, 0, 100);
                TransferredText.Text = value.Phase == "download" ? (value.Bytes / 1048576d).ToString("0.0") + " MB" : "";
            });
            var file = await _service.DownloadAsync(_app, _download, progress, _cancel.Token);
            _cancel.Token.ThrowIfCancellationRequested();
            _installer = true; CancelButton.IsEnabled = HeaderCloseButton.IsEnabled = false;
            DownloadBar.IsIndeterminate = true;
            PhaseText.Text = L("Installerstart wird geprüft…", "Verifying installer launch…");
            HintText.Text = L("Bitte den Herstellerdialog bedienen. Die App wartet auf dessen Prozessende; das ist noch keine Installationsbestätigung.", "Please follow the manufacturer dialog. The app waits for its process to exit; this does not confirm successful installation.");
            await _service.LaunchAsync(_app, file, true, () =>
            {
                StartedInstaller = true;
                PhaseText.Text = L("Originalinstaller geöffnet…", "Original installer opened…");
            });
            PhaseText.Text = L("Installer-Prozess beendet", "Installer process exited");
            HintText.Text = L("Ergebnis im Herstellerprogramm prüfen. Hintergrunddienste können weiterarbeiten.", "Check the result in the manufacturer's application. Background services may still be active."); Finish();
        }
        catch (OperationCanceledException e) { PhaseText.Text = _cancel.IsCancellationRequested ? L("Download abgebrochen", "Download cancelled") : UiText.Error(e, _english); Finish(); }
        catch (Exception exception) { ShowFailure(exception); }
        finally { _running = false; }
    }
    private async Task InstallViaWinGetAsync()
    {
        if (_package is null || _running || _finished || _winGet is null) return;
        var text = L("Dieses Programm über WinGet installieren?", "Install this program via WinGet?") + "\n\n" + _app.Title(_english) + "\n" + _package.Id + "\n\n" +
            L("WinGet zeigt Lizenz- und Quellenbedingungen in seiner Konsole. Es wird nichts automatisch bestätigt. Eine gestartete Installation muss dort beendet werden; dieses Fenster wartet auf das Prozessende.", "WinGet displays licence and source agreements in its console. Nothing is accepted automatically. Once started, finish the installation there; this window waits for the process to exit.");
        if (ConfirmForTests is not null ? !ConfirmForTests(text) : ThemedMessageBox.Show(this, text, "PowerLeitstand PRO", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _running = true; StartButton.IsEnabled = false;
        DownloadBar.IsRunning = DownloadBar.IsIndeterminate = true;
        try
        {
            _cancel.Token.ThrowIfCancellationRequested();
            _installer = true; CancelButton.IsEnabled = HeaderCloseButton.IsEnabled = false;
            HintText.Text = L("Bitte WinGet-Konsole und Herstellerdialog bedienen. Die App wartet auf das Prozessende.", "Please follow the WinGet console and manufacturer dialog. The app waits for the process to exit.");
            var code = await _winGet.InstallAsync(_app.Id, () => { StartedInstaller = true; PhaseText.Text = L("WinGet-Installation läuft…", "WinGet installation running…"); });
            if (code != 0) throw new IOException(L("WinGet hat die Installation nicht erfolgreich abgeschlossen. Code: ", "WinGet did not complete installation successfully. Code: ") + "0x" + unchecked((uint)code).ToString("X8"));
            var installed = (await _service.InspectAsync()).GetValueOrDefault(_app.Id);
            PhaseText.Text = installed?.Installation == ManufacturerInstallState.Installed ? L("Installation bestätigt", "Installation confirmed") : L("WinGet beendet · App erneut prüfen", "WinGet finished · check application again");
            HintText.Text = L("Das Herstellerprogramm wird beim Schließen erneut geprüft. Hintergrunddienste können weiterarbeiten.", "The manufacturer application is checked again when closing. Background services may still be active."); Finish();
        }
        catch (OperationCanceledException) { PhaseText.Text = L("Installation nicht gestartet", "Installation not started"); Finish(); }
        catch (Exception e) { ShowFailure(e); }
        finally { _running = false; }
    }
    internal Func<string, bool>? ConfirmForTests { get; set; }
    private void ShowFailure(Exception exception)
    {
        Failure = exception; PhaseText.Text = L("Vorgang fehlgeschlagen", "Operation failed");
        HintText.Text = UiText.Error(exception, _english); Finish();
    }
    private void Finish()
    {
        _finished = true; DownloadBar.IsRunning = false; StartButton.Visibility = Visibility.Collapsed;
        CancelButton.IsEnabled = HeaderCloseButton.IsEnabled = true; CancelCaption.Text = L("Schließen", "Close");
    }
    private void CancelOperation(object sender, RoutedEventArgs e)
    {
        if (_installer && !_finished) return;
        if (_finished || !_running) { _finished = true; Close(); return; }
        _cancel.Cancel(); CancelButton.IsEnabled = false; PhaseText.Text = L("Abbruch wird abgeschlossen…", "Finishing cancellation…");
    }
    private void GuardClosing(object? sender, CancelEventArgs e)
    {
        if (_finished || !_running) return;
        e.Cancel = true; CancelOperation(this, new RoutedEventArgs());
    }
}
