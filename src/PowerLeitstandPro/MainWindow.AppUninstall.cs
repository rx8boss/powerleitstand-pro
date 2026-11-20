using System.Diagnostics;
using System.IO;
using System.Windows;

namespace PowerControlProWpf;

public partial class MainWindow
{
    private void OpenAppUninstall(object sender, RoutedEventArgs e)
    {
        if (IsBusy)
        {
            ThemedMessageBox.Show(this, _english ? "Finish the active operation before uninstalling." : "Beende zuerst den laufenden Vorgang.",
                "PowerLeitstand PRO", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var executable = Environment.ProcessPath ?? throw new IOException(_english ? "The executable path could not be read." : "Die Programmdatei konnte nicht ermittelt werden.");
            var registration = AppUninstall.Detect(executable);
            var dialog = new AppUninstallWindow(this, ThemePalette.For(SelectedTheme()), _english, executable, registration);
            if (dialog.ShowDialog() != true || IsBusy) return;
            if (registration is null)
            {
                using var explorer = Process.Start(new ProcessStartInfo(Path.GetDirectoryName(executable)!) { UseShellExecute = true });
                return;
            }
            AppUninstall.Launch(executable, registration, dialog.KeepData, _english);
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception error)
        {
            ThemedMessageBox.Show(this, UiText.Error(error, _english), "PowerLeitstand PRO", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}