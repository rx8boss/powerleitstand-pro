using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using ComboBox = System.Windows.Controls.ComboBox;

namespace PowerControlProWpf;

internal sealed partial class ProgramInfoCard
{
    private ComboBox? _vlcPicker;
    private void AddVlcCopyPicker(StackPanel body)
    {
        if (_entry.Id != "vlc" || _view.Service is not ProgramInstallService) return;
        _vlcPicker = new ComboBox { Name = "ProgramInfoVlcCopy", MinHeight = 30, IsEnabled = false };
        AutomationProperties.SetName(_vlcPicker, L("VLC-Installationskopie auswählen", "Select VLC installation copy"));
        var field = Field(L("Installationskopie", "Installed copy"), _vlcPicker); field.Visibility = Visibility.Collapsed;
        body.Children.Add(field);
    }
    private async Task LoadVlcCopiesAsync()
    {
        if (_vlcPicker is null || _view.Service is not ProgramInstallService service) return;
        var copies = await service.VlcCopiesAsync(_cancel.Token);
        if (_closed || copies.Count == 0) return;
        var selected = _view.Inventory.Evidence?.GetValueOrDefault(_entry.Id)?.Path;
        foreach (var copy in copies)
        {
            var item = new ComboBoxItem { Content = copy.Architecture + " · " + (copy.InstallerType == "wix" ? "MSI" : "EXE") + " · " + copy.Version, Tag = copy, ToolTip = copy.Executable };
            _vlcPicker.Items.Add(item);
            if (string.Equals(copy.Executable, selected, StringComparison.OrdinalIgnoreCase) || string.Equals(copy.Root, selected?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) _vlcPicker.SelectedItem = item;
        }
        ((FrameworkElement)_vlcPicker.Parent).Visibility = Visibility.Visible;
        _vlcPicker.IsEnabled = _view.CanChooseProgramFile;
        _vlcPicker.SelectionChanged += async (_, _) =>
        {
            if (_closed || !_view.CanChooseProgramFile || _vlcPicker.SelectedItem is not ComboBoxItem { Tag: VlcRegisteredCopy copy }) return;
            _vlcPicker.IsEnabled = false; _leave.Stop();
            try { await _view.RememberProgramFileAsync(_entry, copy.Executable); if (!_closed) { Close(); _view.ReopenInfo(_entry, _anchor); } }
            catch (Exception error) { if (!_closed) { _error.Text = UiText.Error(error, _view.English); _error.Visibility = Visibility.Visible; _vlcPicker.IsEnabled = true; ResetIdle(); } }
        };
    }
}
