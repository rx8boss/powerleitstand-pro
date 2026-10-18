using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using ColorConverter = System.Windows.Media.ColorConverter;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

namespace PowerControlProWpf;

public partial class UpdateDownloadWindow : Window
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly AvailableUpdate _update;
    private readonly Func<bool> _isBusy;
    private readonly bool _english;
    private bool _started, _finished, _handoffStarted;
    internal Exception? Failure { get; private set; }

    internal UpdateDownloadWindow(AvailableUpdate update, ThemePalette palette, bool english, Func<bool> isBusy)
    {
        InitializeComponent();
        _update = update; _isBusy = isBusy; _english = english;
        Brush Solid(string value) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        foreach (var (key, index) in new[] { ("DialogBackground", 0), ("DialogText", 5), ("DialogSubtleText", 6), ("DialogAccent", 7), ("DialogInput", 4) })
            Resources[key] = Solid(palette.Colors[index]);
        var surface = (Color)ColorConverter.ConvertFromString(palette.Colors[3]);
        var light = (surface.R * 299 + surface.G * 587 + surface.B * 114) / 1000 > 150;
        Resources["DialogDangerBackground"] = Solid(light ? "#98283F" : "#6A1F32");
        Resources["DialogDangerBorder"] = Solid(light ? "#A91F3D" : "#FF5A74");
        Resources["DialogDangerHover"] = Solid(light ? "#B93550" : "#98283F");
        DialogAppearance.ApplySurface(this, DialogFrame, palette);
        DialogAppearance.ApplyRedButton(CancelButton, (Style)FindResource("CancelButtonStyle"));
        CrossButtonAppearance.Prepare(HeaderCloseButton);
        FontFamily = new FontFamily(palette.FontFamily);
        HeaderText.Text = english ? "Download update" : "Update herunterladen";
        VersionText.Text = $"PowerLeitstand PRO · v{update.Version}";
        PhaseText.Text = english ? "Preparing download …" : "Download wird vorbereitet …";
        HintText.Text = english ? "The file will be verified. The app will then restart." : "Anschließend wird die Datei geprüft. Danach startet die App neu.";
        CancelCaption.Text = english ? "Cancel" : "Abbrechen";
        HeaderCloseButton.ToolTip = english ? "Cancel download" : "Download abbrechen";
        ContentRendered += RunDownload;
        Closing += GuardClosing;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { RequestCancellation(); e.Handled = true; } };
        Closed += (_, _) => _cancellation.Dispose();
    }

    private async void RunDownload(object? sender, EventArgs e)
    {
        if (_started) return;
        _started = true;
        var installed = false;
        try
        {
            var progress = new Progress<UpdateProgress>(ApplyProgress);
            await UpdateService.PrepareAndLaunchAsync(_update, _isBusy, _cancellation.Token, progress);
            installed = true;
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
        catch (Exception exception) { Failure = exception; }
        finally
        {
            _finished = true;
            DialogResult = installed;
        }
    }

    private void ApplyProgress(UpdateProgress progress)
    {
        if (_finished || _cancellation.IsCancellationRequested) return;
        var culture = CultureInfo.GetCultureInfo(_english ? "en-US" : "de-DE");
        string Mb(long value) => (value / 1_000_000d).ToString("F1", culture);
        DownloadBar.IsIndeterminate = progress.Phase != UpdatePhase.Downloading || progress.TotalBytes is not > 0;
        if (progress.Phase == UpdatePhase.Downloading)
        {
            PhaseText.Text = _english ? "Downloading …" : "Download läuft …";
            if (progress.TotalBytes is > 0)
            {
                DownloadBar.Value = Math.Clamp(progress.ReceivedBytes * 100d / progress.TotalBytes.Value, 0, 100);
                PercentText.Text = $"{Math.Floor(DownloadBar.Value):0} %";
                TransferredText.Text = _english ? $"{Mb(progress.ReceivedBytes)} MB of {Mb(progress.TotalBytes.Value)} MB downloaded"
                    : $"{Mb(progress.ReceivedBytes)} MB von {Mb(progress.TotalBytes.Value)} MB heruntergeladen";
            }
            else
            {
                PercentText.Text = "";
                TransferredText.Text = _english ? $"{Mb(progress.ReceivedBytes)} MB downloaded" : $"{Mb(progress.ReceivedBytes)} MB heruntergeladen";
            }
        }
        else
        {
            PercentText.Text = "";
            PhaseText.Text = progress.Phase == UpdatePhase.Verifying
                ? (_english ? "Verifying file …" : "Datei wird geprüft …")
                : (_english ? "Preparing restart …" : "Neustart wird vorbereitet …");
            if (progress.Phase == UpdatePhase.Verifying)
                HintText.Text = _english ? "The checksum is compared with the GitHub release." : "Die Prüfsumme wird mit der GitHub-Veröffentlichung verglichen.";
            if (progress.Phase == UpdatePhase.Restarting)
            {
                _handoffStarted = true;
                CancelButton.IsEnabled = HeaderCloseButton.IsEnabled = false;
                HintText.Text = _english ? "Verification successful. The app will restart with the new version." : "Prüfung erfolgreich. Die App startet mit der neuen Version neu.";
            }
        }
    }

    private void CancelDownload(object sender, RoutedEventArgs e) => RequestCancellation();
    private void RequestCancellation()
    {
        if (_finished || _handoffStarted || _cancellation.IsCancellationRequested) return;
        _cancellation.Cancel();
        PhaseText.Text = _english ? "Cancelling download …" : "Download wird abgebrochen …";
        CancelButton.IsEnabled = HeaderCloseButton.IsEnabled = false;
        HintText.Text = _english ? "The existing program remains unchanged." : "Das bisherige Programm bleibt unverändert.";
    }
    private void GuardClosing(object? sender, CancelEventArgs e)
    {
        if (_finished) return;
        e.Cancel = true;
        RequestCancellation();
    }
}
