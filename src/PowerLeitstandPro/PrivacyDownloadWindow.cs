using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using CheckBox = System.Windows.Controls.CheckBox;
using Orientation = System.Windows.Controls.Orientation;

namespace PowerControlProWpf;

internal sealed class PrivacyDownloadWindow : Window
{
    private readonly bool _english;
    private readonly WindowsUpdatesPage _page;
    private readonly CheckBox _license = new();
    private readonly TextBlock _state, _target;
    private readonly Button _start, _cancel, _close, _licenseLink;
    private readonly AnimatedProgressBar _progress = new() { Height = 28, Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed };
    private CancellationTokenSource? _cancellation;
    private bool _working, _launched;
    internal IPrivacyDownloadService Service { get; set; }
    internal CheckBox Consent => _license;
    internal Button StartButton => _start;
    internal AnimatedProgressBar Progress => _progress;
    internal bool Working => _working;
    private string L(string de, string en) => _english ? en : de;
    internal PrivacyDownloadWindow(Window owner, WindowsUpdatesPage page, ThemePalette palette, bool english, IPrivacyDownloadService? service = null)
    {
        Owner = owner; _page = page; _english = english; Service = service ?? new PrivacyDownloadService();
        Title = "O&O ShutUp10"; WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Background = Brushes.Transparent; AllowsTransparency = true;
        foreach (var (key, index) in new[] { ("InputBrush", 4), ("TextBrush", 5), ("AccentBrush", 7), ("InputBorderBrush", 8), ("StandardActionButtonBrush", 9) })
            Resources[key] = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(palette.Colors[index]));
        var frame = new Border(); Content = frame; var body = new StackPanel();
        body.Children.Add(DialogLayout.Note("O&O ShutUp10 · Free · x64", palette, 17));
        var description = DialogLayout.Note(L("Datenschutz-Einstellungen für Windows 10 und 11.\nPortable Anwendung · keine Installation erforderlich.", "Privacy settings for Windows 10 and 11.\nPortable application · no installation required."), palette, 12); description.Margin = new(0, 7, 0, 12); body.Children.Add(description);
        _target = DialogLayout.Note(L("Downloadordner: ", "Downloads folder: ") + Service.DownloadsDirectory, palette, 11); body.Children.Add(_target);
        _licenseLink = new Button { Content = L("↗ Lizenzbedingungen öffnen", "↗ Open license terms"), Height = 32, Margin = new(0, 12, 0, 12) };
        _licenseLink.Click += (_, _) =>
        {
            try { OnlineConnection.RequireNetwork(); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://docs.oo-software.com/" + (english ? "en" : "de") + "/eula-3") { UseShellExecute = true }); }
            catch (Exception e) { if (_state is not null) _state.Text = UiText.Error(e, english); }
        }; body.Children.Add(_licenseLink);
        var consentRow = new Grid { Margin = new(0, 0, 0, 12) }; consentRow.ColumnDefinitions.Add(new() { Width = new GridLength(30) }); consentRow.ColumnDefinitions.Add(new());
        _license.Style = (Style)page.FindResource("UpdateSelection"); _license.VerticalAlignment = VerticalAlignment.Center;
        _license.Checked += (_, _) => UpdateAvailability(); _license.Unchecked += (_, _) => UpdateAvailability(); consentRow.Children.Add(_license);
        var consentText = DialogLayout.Note(L("Ich habe die Lizenzbedingungen gelesen und akzeptiere sie.", "I have read and accept the license terms."), palette, 12); Grid.SetColumn(consentText, 1); consentRow.Children.Add(consentText);
        System.Windows.Automation.AutomationProperties.SetName(_license, consentText.Text); body.Children.Add(consentRow);
        _state = DialogLayout.Note(L("Nach dem Download wird die Herstellersignatur geprüft und O&O geöffnet. Datenschutzeinstellungen werden nicht automatisch geändert.", "After downloading, the publisher signature is checked and O&O is opened. Privacy settings are not changed automatically."), palette, 12);
        body.Children.Add(_state); _progress.Margin = new(0, 12, 0, 0); _progress.ApplyPalette(palette);
        _progress.Background = (Brush)owner.FindResource("InputBrush"); _progress.BorderBrush = _progress.Foreground = (Brush)owner.FindResource("AccentBrush"); body.Children.Add(_progress);
        _start = new Button { Content = L("↓ Herunterladen und öffnen", "↓ Download and open") }; _start.Click += async (_, _) => await StartAsync();
        _cancel = new Button { Content = new TextBlock { Text = L("× Abbrechen", "× Cancel") } }; _cancel.Click += (_, _) => { if (_working) _cancellation?.Cancel(); else Close(); };
        _close = new Button(); _close.Click += (_, _) => { if (!_working) Close(); else if (!_launched) _cancellation?.Cancel(); };
        DialogLayout.Compose(this, frame, palette, "O&O ShutUp10", english, body, DialogLayout.Actions(_start, _cancel), _close);
        UnifiedDialogAppearance.Button(this, _cancel, DialogButtonKind.Danger); UpdateAvailability();
        Closing += (_, e) => { if (_working) { e.Cancel = true; if (!_launched) _cancellation?.Cancel(); } };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { if (_working && !_launched) _cancellation?.Cancel(); else if (!_working) Close(); e.Handled = true; } };
    }
    private void UpdateAvailability()
    {
        if (_start is null) return;
        _start.IsEnabled = !_working && _license.IsChecked == true && !_page.HostBusy();
        _license.IsEnabled = _licenseLink.IsEnabled = !_working;
        _cancel.IsEnabled = _close.IsEnabled = !_launched;
    }
    internal async Task StartAsync()
    {
        if (_working || _license.IsChecked != true || _page.HostBusy() || _page.IsWorking) return;
        _working = true; _launched = false; _cancellation = new(); _page.PrivacyWorking = true; _page.WorkStateChanged?.Invoke(true); UpdateAvailability(); _page.RefreshAvailability();
        _progress.Visibility = Visibility.Visible; _progress.IsRunning = true; _progress.IsIndeterminate = true;
        _state.Text = L("Aktuelle offizielle Downloadquelle prüfen …", "Checking current official download source …");
        try
        {
            var file = await Service.DownloadAsync(true, new Progress<PrivacyProgress>(value =>
            {
                _progress.IsIndeterminate = value.Total is not > 0;
                if (value.Total is > 0) _progress.Value = Math.Clamp((double)value.Received / value.Total.Value * 100, 0, 100);
                _state.Text = L("Download läuft …", "Downloading …");
            }), _cancellation.Token);
            _cancellation.Token.ThrowIfCancellationRequested(); _launched = true; UpdateAvailability();
            _progress.IsIndeterminate = true; _state.Text = L("O&O geöffnet · bitte dort beenden, um die App wieder freizugeben.", "O&O opened · close it there to unlock the app.");
            _page.WriteLog?.Invoke(L("O&O heruntergeladen und Signatur bestätigt: ", "O&O downloaded and signature verified: ") + file);
            await Service.OpenAsync(file);
            _state.Text = L("O&O beendet. Die geprüfte Datei liegt im Downloadordner.", "O&O closed. The verified file is in Downloads.");
        }
        catch (OperationCanceledException e) { _state.Text = _cancellation.IsCancellationRequested ? L("Download abgebrochen. Eigene unvollständige Datei entfernt.", "Download cancelled. The incomplete download was removed.") : UiText.Error(e, _english); }
        catch (Exception e) { _state.Text = L("O&O konnte nicht geöffnet werden: ", "Could not open O&O: ") + UiText.Error(e, _english); _page.WriteLog?.Invoke(_state.Text); }
        finally
        {
            _working = _launched = false; _progress.IsRunning = false; _progress.IsIndeterminate = false; _cancellation.Dispose(); _cancellation = null;
            _page.PrivacyWorking = false; _page.WorkStateChanged?.Invoke(false); _page.RefreshAvailability(); UpdateAvailability();
        }
    }
}
