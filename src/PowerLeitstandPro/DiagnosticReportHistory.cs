using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Border = System.Windows.Controls.Border;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace PowerControlProWpf;

internal static class DiagnosticReportHistory
{
    internal static FrameworkElement Build(Window owner, ThemePalette palette,
        bool english, DiagnosticReportStore store, string title)
    {
        string T(string german, string translated) => english ? translated : german;
        UnifiedDialogAppearance.Resources(owner, palette);
        Button Action(string caption, DialogButtonKind kind = DialogButtonKind.Primary)
        {
            var button = new Button { Content = caption, Height = 30, Padding = new Thickness(8, 0, 8, 0) };
            UnifiedDialogAppearance.Button(owner, button, kind);
            return button;
        }

        var heading = DialogLayout.Note("", palette, 14);
        heading.Name = "DiagnosticHistoryHeading";
        heading.VerticalAlignment = VerticalAlignment.Center;
        heading.FontWeight = FontWeights.Bold;
        heading.SetResourceReference(TextBlock.ForegroundProperty, "UnifiedAccent");
        var rows = new StackPanel();
        var delete = Action(T("Löschen", "Delete"), DialogButtonKind.Danger);
        delete.Name = "DiagnosticDeleteReports";
        ProgramButtonIcons.Set(delete, "delete", T("Löschen", "Delete"));
        delete.MinWidth = 100;
        var folder = Action(T("Ordner öffnen", "Open folder"));
        folder.Name = "DiagnosticOpenFolder";
        ProgramButtonIcons.Set(folder, "folder", T("Ordner öffnen", "Open folder"));
        folder.MinWidth = 125;
        folder.Margin = new Thickness(8, 0, 0, 0);
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Children = { delete, folder } };
        DockPanel.SetDock(actions, Dock.Right);
        var header = new DockPanel
        {
            Margin = new Thickness(3, 0, 0, 8),
            Children = { actions, heading }
        };
        header.SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < 405;
            DockPanel.SetDock(actions, narrow ? Dock.Top : Dock.Right);
            actions.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
            actions.Margin = new Thickness(0, 0, 0, narrow ? 6 : 0);
        };
        var previewTitle = DialogLayout.Note("", palette, 13);
        previewTitle.FontWeight = FontWeights.SemiBold;
        previewTitle.SetResourceReference(TextBlock.ForegroundProperty, "UnifiedAccent");
        var previewText = new TextBox
        {
            Name = "DiagnosticReportPreviewText",
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontSize = 12,
            BorderThickness = new Thickness(0), Padding = new Thickness(0), Margin = new Thickness(0, 5, 0, 0),
            Background = Brushes.Transparent
        };
        previewText.SetResourceReference(TextBox.ForegroundProperty, "UnifiedText");
        var preview = new Border
        {
            Visibility = Visibility.Collapsed, Padding = new Thickness(9), Margin = new Thickness(0, 0, 0, 8),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            BorderBrush = BrushFor(palette.Colors[8]), Child = DialogLayout.Stack(previewTitle, previewText)
        };
        var history = new Border
        {
            Name = "DiagnosticHistory",
            Child = DialogLayout.Stack(header, preview, rows),
            Padding = new Thickness(9, 6, 9, 10),
            Margin = new Thickness(0, 12, 0, 0),
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            BorderBrush = BrushFor(palette.Colors[8]), Background = BrushFor(palette.Colors[4])
        };
        var rowSurface = BrushFor(palette.Colors[4]);
        var surface = rowSurface.Color;
        var tint = BrushFor(palette.Colors[9]).Color;
        rowSurface.Color = System.Windows.Media.Color.FromRgb(
            (byte)((surface.R * 2 + tint.R) / 3), (byte)((surface.G * 2 + tint.G) / 3), (byte)((surface.B * 2 + tint.B) / 3));
        IReadOnlyList<DiagnosticReportStore.Entry> displayed = [];
        Button? selected = null;

        void ShowError(Exception ex) => ThemedMessageBox.Show(owner, UiText.Error(ex, english), T("Diagnoseprotokolle", "Diagnostic reports"),
            MessageBoxButton.OK, MessageBoxImage.Warning);

        void Open(string executable, Func<string> path)
        {
            try
            {
                var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                var info = new ProcessStartInfo(Path.Combine(windows, executable)) { UseShellExecute = false };
                info.ArgumentList.Add(path());
                using var process = Process.Start(info);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or System.Security.SecurityException)
            { ShowError(ex); }
        }

        void Refresh()
        {
            selected = null;
            preview.Visibility = Visibility.Collapsed;
            rows.Children.Clear();
            try
            {
                displayed = store.List();
                foreach (var entry in displayed)
                {
                    var local = entry.Date.ToLocalTime();
                    var culture = CultureInfo.GetCultureInfo(english ? "en-GB" : "de-DE");
                    var when = local.Date == DateTime.Today
                        ? T("Heute, ", "Today, ") + local.ToString("HH:mm", culture)
                        : local.ToString("d", culture) + ", " + local.ToString("HH:mm", culture);
                    var status = entry.Status switch
                    {
                        DiagnosticReportStatus.Completed => T("Abgeschlossen", "Completed"),
                        DiagnosticReportStatus.Empty => T("Keine Treffer", "No matches"),
                        DiagnosticReportStatus.Failed => T("Fehlgeschlagen", "Failed"),
                        DiagnosticReportStatus.Cancelled => T("Abgebrochen", "Cancelled"),
                        _ => T("Windows-Fenster geöffnet", "Windows tool opened")
                    };
                    var open = Action(T("Datei öffnen ↗", "Open file ↗"));
                    open.Name = "DiagnosticOpenReport";
                    open.Height = open.MinHeight = 26;
                    open.FontSize = 12;
                    open.FontWeight = FontWeights.SemiBold;
                    open.Background = System.Windows.Media.Brushes.Transparent;
                    open.BorderThickness = new Thickness(0);
                    open.SetResourceReference(Button.ForegroundProperty, "UnifiedAccent");
                    var fullDate = local.ToString("G", culture);
                    open.ToolTip = fullDate;
                    System.Windows.Automation.AutomationProperties.SetName(open, T("Protokoll öffnen", "Open report") + " " + fullDate);
                    open.Click += (_, _) => Open(@"System32\notepad.exe", () => store.OpenablePath(entry));
                    var date = DialogLayout.Note(when, palette, 12);
                    date.VerticalAlignment = VerticalAlignment.Center;
                    date.ToolTip = fullDate;
                    var state = DialogLayout.Note(status, palette, 12);
                    state.VerticalAlignment = VerticalAlignment.Center;
                    var columns = new Grid();
                    columns.ColumnDefinitions.Add(new ColumnDefinition());
                    columns.ColumnDefinitions.Add(new ColumnDefinition());
                    Grid.SetColumn(state, 1);
                    columns.Children.Add(date);
                    columns.Children.Add(state);
                    var select = Action("");
                    select.Name = "DiagnosticSelectReport";
                    select.Height = double.NaN;
                    select.MinHeight = 26;
                    select.Background = Brushes.Transparent;
                    select.BorderThickness = new Thickness(0);
                    select.ToolTip = T("Zusammenfassung anzeigen", "Show summary") + " · " + fullDate;
                    System.Windows.Automation.AutomationProperties.SetName(select, (string)select.ToolTip);
                    select.Click += (_, _) =>
                    {
                        try
                        {
                            var result = store.ReadResult(entry);
                            if (selected is not null) selected.BorderThickness = new Thickness(0);
                            selected = select;
                            select.SetResourceReference(Button.BorderBrushProperty, "UnifiedAccent");
                            select.BorderThickness = new Thickness(1);
                            previewTitle.Text = T("Zusammenfassung", "Summary") + $" · {fullDate} · {status}";
                            previewText.Text = result.Length == 0 ? T("Keine Ausgabe gespeichert.", "No output was saved.") : result;
                            preview.Visibility = Visibility.Visible;
                            previewTitle.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => previewTitle.BringIntoView());
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                        {
                            ShowError(ex);
                        }
                    };
                    columns.IsHitTestVisible = false;
                    var rowContent = new Grid { Children = { select, columns } };
                    DockPanel.SetDock(open, Dock.Right);
                    rows.Children.Add(new Border
                    {
                        Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(11, 2, 5, 2),
                        BorderBrush = BrushFor(palette.Colors[8]), Background = rowSurface,
                        CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
                        Child = new DockPanel { Children = { open, rowContent } }
                    });
                }
                if (displayed.Count == 0)
                    rows.Children.Add(DialogLayout.Note(T("Für dieses Werkzeug sind noch keine Protokolle gespeichert.",
                        "There are no saved reports for this tool yet."), palette));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                displayed = [];
                rows.Children.Add(DialogLayout.Note(T("Die Protokolle konnten nicht gelesen werden.", "The reports could not be read."), palette));
            }
            heading.Text = T($"Protokolle · {displayed.Count} von max. {DiagnosticReportStore.Limit}",
                $"Reports · {displayed.Count} of max. {DiagnosticReportStore.Limit}");
            delete.IsEnabled = displayed.Count > 0;
        }
        folder.Click += (_, _) => Open("explorer.exe", store.OpenableFolder);
        delete.Click += (_, _) =>
        {
            Refresh();
            var confirmed = displayed;
            if (confirmed.Count == 0) return;
            var question = confirmed.Count == 1
                ? T($"1 gespeichertes Protokoll für „{title}“ löschen?", $"Delete 1 saved report for “{title}”?")
                : T($"Alle {confirmed.Count} gespeicherten Protokolle für „{title}“ löschen?",
                    $"Delete all {confirmed.Count} saved reports for “{title}”?");
            var message = string.Join(Environment.NewLine, question, Path.Combine("Logs", store.RelativeFolder),
                T("Der Ordner bleibt erhalten.", "The folder is kept."));
            if (ThemedMessageBox.Show(owner, message, T("Protokolle löschen", "Delete reports"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, fitHeightToOwner: false) != MessageBoxResult.Yes) return;
            try
            {
                var failures = store.Delete(confirmed);
                if (failures != 0)
                    ThemedMessageBox.Show(owner, T($"{failures} Protokoll(e) konnten nicht gelöscht werden.",
                        $"{failures} report(s) could not be deleted."), T("Protokolle löschen", "Delete reports"),
                        MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { ShowError(ex); }
            Refresh();
        };
        history.Loaded += (_, _) =>
        {
            UnifiedDialogAppearance.Button(owner, delete, DialogButtonKind.Danger);
            Refresh();
        };
        return history;
    }

    private static SolidColorBrush BrushFor(string hex) => new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
}

