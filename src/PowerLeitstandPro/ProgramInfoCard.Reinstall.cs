using System.Diagnostics;
using System.Windows;

namespace PowerControlProWpf;

internal sealed partial class ProgramInfoCard
{
    private void OpenStore()
    {
        try
        {
            var family = ProgramVersions.StoreFamilies[_entry.Id];
            Process.Start(new ProcessStartInfo("ms-windows-store://pdp/?PFN=" + Uri.EscapeDataString(family)) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            _error.Text = UiText.Error(error, _view.English);
            _error.Visibility = Visibility.Visible;
        }
        ResetIdle();
    }

    private async Task ReinstallAsync()
    {
        if (_view.Service is not ProgramInstallService service || !_view.Inventory.Installed.ContainsKey(_entry.Id)) return;
        var capability = service.ReinstallCapability(_entry, _view.Inventory);
        if (capability.Automatic)
        {
            try { Close(); await _view.InstallEntriesAsync([_entry], allowReinstall: true); }
            catch (Exception error) { _view.WriteLog?.Invoke(UiText.Error(error, _view.English)); }
            return;
        }
        _leave.Stop();
        try
        {
            string explanation = ProgramInstallService.ReinstallExplanation(capability, _view.English);
            if (capability.Route is ProgramReinstallRoute.Unavailable || capability.Reason == "store-unconfirmed")
            { _error.Text = explanation; _error.Visibility = Visibility.Visible; return; }
            bool store = capability.Source == "Windows-App";
            string question = explanation + "\n\n" + (store
                ? L("Microsoft Store für dieses Programm öffnen? Es wird nichts automatisch entfernt oder zurückgesetzt.", "Open Microsoft Store for this program? Nothing will be automatically removed or reset.")
                : L("Offizielle Herstellerseite öffnen? Die vorhandene Installation wird nicht automatisch verändert.", "Open the official publisher website? The existing installation will not be automatically changed."));
            if (ThemedMessageBox.Show(_owner, question, _entry.Name, MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
            if (store) OpenStore();
            else _view.Service.OpenWebsite(_entry);
        }
        catch (Exception error)
        { if (!_closed) { _error.Text = UiText.Error(error, _view.English); _error.Visibility = Visibility.Visible; } }
        finally { if (!_closed) ResetIdle(); }
    }
}
