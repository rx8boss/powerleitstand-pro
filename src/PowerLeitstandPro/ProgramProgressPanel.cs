using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using Panel = System.Windows.Controls.Panel;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using Control = System.Windows.Controls.Control;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace PowerControlProWpf;

// Nonmodal presentation: the operation belongs to the catalog, not to this card or its visibility.
internal sealed class ProgramProgressPanel : IDisposable
{
    private readonly ProgramCatalogView _view;
    private readonly Window _owner;
    private readonly Grid _host;
    private readonly Canvas _layer = new() { Name = "ProgramProgressLayer", Background = null };
    private readonly Border _frame = new() { Padding = new(18), CornerRadius = new(10), BorderThickness = new(1.2) };
    private readonly Dictionary<string, TextBlock> _states = [];
    private readonly HashSet<string> _terminal = [];
    private readonly TextBlock _phase, _program, _version, _note, _title;
    private readonly Dictionary<string, TextBlock> _paths = [];
    private readonly HashSet<string> _confirmedLocations = [];
    private readonly Dictionary<string, TextBlock> _markers = [];
    private readonly ProgramEntry[] _entries;
    private readonly TextBlock[] _steps = new TextBlock[4];
    private readonly Dictionary<string, ProgramStepState[]> _stepStates = [];
    private readonly Dictionary<string, TextBlock[]> _history = [];
    private readonly HashSet<string> _portable = [];
    private readonly bool _reinstall;
    internal ProgramStepState StepState(string id, ProgramStep step) => _stepStates[id][(int)step];
    private string StepName(int n, string? id = null) => n switch { 0 => L("Prüfen", "Check"), 1 => "Download", 2 when _portable.Contains(id ?? _currentId ?? "") => L("Austausch", "Replace"), 2 when _reinstall => L("Neuinstallation", "Reinstall"), 2 => L("Installation", "Install"), _ => L("Abschluss", "Finish") };
    private readonly FrameworkElement _fallback;
    private string? _currentId;
    private readonly Image _icon = new() { Width = 31, Height = 31, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button _stop, _reopen, _minimize, _collapse;
    private readonly Grid _header;
    private bool _finished, _disposed, _placed, _stopRequested;
    private WindowState _restoreState;
    private Point? _drag;
    internal bool Expanded => !_disposed && _frame.Visibility == Visibility.Visible;
    internal Border Frame => _frame;
    internal AnimatedProgressBar Progress { get; } = new() { Height = 29, IsIndeterminate = true, IsRunning = true, Margin = new(0, 10, 0, 8) };
    private string L(string de, string en) => _view.English ? en : de;
    internal ProgramProgressPanel(ProgramCatalogView view, ProgramEntry[] entries, bool reinstall = false)
    {
        _entries = entries; _view = view; _reinstall = reinstall; _owner = Window.GetWindow(view) ?? throw new InvalidOperationException("No app window"); _host = (Grid)_owner.FindName("Root");
        _restoreState = _owner.WindowState == WindowState.Minimized ? WindowState.Normal : _owner.WindowState;
        UnifiedDialogAppearance.Resources(_owner, view.Palette);
        Grid.SetRowSpan(_layer, Math.Max(1, _host.RowDefinitions.Count)); Grid.SetColumnSpan(_layer, Math.Max(1, _host.ColumnDefinitions.Count)); Panel.SetZIndex(_layer, 4500);
        _frame.SetResourceReference(Border.BackgroundProperty, "DialogSurface"); _frame.SetResourceReference(Border.BorderBrushProperty, "UnifiedAccent");
        var body = new StackPanel();
        _header = new Grid { Cursor = Cursors.SizeAll, Margin = new(0, 0, 0, 13) }; _header.ColumnDefinitions.Add(new() { Width = new GridLength(34) }); _header.ColumnDefinitions.Add(new()); _header.ColumnDefinitions.Add(new() { Width = new GridLength(28) });
        _header.Children.Add(ProgramFallbackIcon.Create(28, "UnifiedAccent"));
        var labels = new StackPanel { Margin = new(0, 0, 65, 0) }; _title = Text(_reinstall ? entries.Length == 1 ? L("Programm neu installieren", "Reinstall program") : L("Programme neu installieren", "Reinstall programs") : L("Programme installieren", "Install programs"), 20); _title.FontWeight = FontWeights.SemiBold; labels.Children.Add(_title);
        _phase = Text(_reinstall ? L("Neuinstallation wird vorbereitet …", "Preparing reinstallation …") : L("Auswahl wird vorbereitet …", "Preparing selection …"), 11, true); labels.Children.Add(_phase); Grid.SetColumn(labels, 1); _header.Children.Add(labels);
        var stripe = new System.Windows.Shapes.Path { Data = Geometry.Parse("M48,0 L50,0 L8,40 L6,40 Z M67,0 L69,0 L27,40 L25,40 Z M73,0 L87,0 L45,40 L31,40 Z"), Width = 62, Height = 32, Opacity = .25, Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 0, 12, 0), IsHitTestVisible = false };
        stripe.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "UnifiedAccent"); Grid.SetColumn(stripe, 1); _header.Children.Add(stripe);
        _minimize = Action("−"); _minimize.Name = "ProgramProgressMinimize"; _minimize.Width = 28; _minimize.Height = _minimize.MinHeight = 26; _minimize.Padding = new(0); _minimize.ToolTip = L("Einklappen", "Collapse"); _minimize.Click += (_, _) => Collapse(); Grid.SetColumn(_minimize, 2); _header.Children.Add(_minimize); body.Children.Add(_header);
        var program = new Grid(); program.ColumnDefinitions.Add(new() { Width = new GridLength(43) }); program.ColumnDefinitions.Add(new()); var iconCell = new Grid { Width = 31, Height = 31, HorizontalAlignment = HorizontalAlignment.Left }; _fallback = ProgramFallbackIcon.Create(28, "UnifiedAccent"); iconCell.Children.Add(_fallback); iconCell.Children.Add(_icon); program.Children.Add(iconCell);
        var programLabels = new StackPanel(); _program = Text("", 16); _program.FontWeight = FontWeights.SemiBold; programLabels.Children.Add(_program); _version = Text("", 11, true); programLabels.Children.Add(_version); Grid.SetColumn(programLabels, 1); program.Children.Add(programLabels); body.Children.Add(program);
        var steps = new Grid { Margin = new(0, 12, 0, 0) };
        var captions = Enumerable.Range(0, 4).Select(n => (n + 1) + " " + StepName(n)).ToArray();
        for (int n = 0; n < captions.Length; n++) { steps.ColumnDefinitions.Add(new()); var step = _steps[n] = Text(captions[n], 10, true); step.HorizontalAlignment = HorizontalAlignment.Center; Grid.SetColumn(step, n); steps.Children.Add(step); }
        body.Children.Add(steps); body.Children.Add(Progress);
        _note = Text(L("Auswahl gesperrt · Andere Seiten bleiben zum Ansehen erreichbar.", "Selection locked · other pages remain available for viewing."), 11, true); _note.Margin = new(0, 0, 0, 13);
        var rows = new StackPanel(); foreach (var entry in entries)
        {
            var row = new Grid { Margin = new(0, 4, 0, 4) }; row.ColumnDefinitions.Add(new() { Width = new GridLength(23) }); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.RowDefinitions.Add(new() { Height = GridLength.Auto }); row.RowDefinitions.Add(new() { Height = GridLength.Auto });
            row.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var marker = Text("◷", 12); marker.SetResourceReference(TextBlock.ForegroundProperty, "UnifiedAccent"); row.Children.Add(marker); _markers[entry.Id] = marker;
            var name = Text(entry.Name, 11); name.Margin = new(0, 0, 12, 0); Grid.SetColumn(name, 1); row.Children.Add(name); var state = Text(L("Wartet", "Waiting"), 11, true); Grid.SetColumn(state, 2); row.Children.Add(state); _states[entry.Id] = state;
            _stepStates[entry.Id] = new ProgramStepState[4];
            var history = new Grid { Margin = new(23, 5, 0, 2) }; var cells = _history[entry.Id] = new TextBlock[4];
            for (int n = 0; n < 4; n++) { history.ColumnDefinitions.Add(new()); var cell = cells[n] = Text(StepName(n) + " ·", 10, true); Grid.SetColumn(cell, n); history.Children.Add(cell); }
            Grid.SetRow(history, 1); Grid.SetColumnSpan(history, 3); row.Children.Add(history);
            var location = Text("", 10, true); location.Visibility = Visibility.Collapsed; location.Margin = new(23, 4, 0, 2); Grid.SetRow(location, 2); Grid.SetColumnSpan(location, 3); row.Children.Add(location); _paths[entry.Id] = location; rows.Children.Add(row);
        }
        var scroll = new ScrollViewer { Content = rows, MaxHeight = 105, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var queue = new Border { Child = scroll, Padding = new(9, 5, 9, 5), CornerRadius = new(6), BorderThickness = new(1), Margin = new(0, 0, 0, 13) }; queue.SetResourceReference(Border.BackgroundProperty, "InputBrush"); queue.SetResourceReference(Border.BorderBrushProperty, "InputBorderBrush"); body.Children.Add(queue); body.Children.Add(_note); DialogLayout.ScrollHint(scroll, view.English);
        var footer = new Grid(); footer.ColumnDefinitions.Add(new()); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _stop = Action(L("Warteschlange stoppen", "Stop queue"), true); _stop.Name = "ProgramProgressStop"; _stop.Width = 190; _stop.HorizontalAlignment = HorizontalAlignment.Left; _stop.Click += (_, _) => { if (_finished) Dispose(); else view.RequestStop(); }; footer.Children.Add(_stop);
        _collapse = Action(L("Einklappen", "Collapse")); _collapse.Name = "ProgramProgressCollapse"; _collapse.Width = 116; _collapse.Click += (_, _) => Collapse(); Grid.SetColumn(_collapse, 1); footer.Children.Add(_collapse); body.Children.Add(footer); _frame.Child = body;
        _reopen = Action(L("↻ Vorgang anzeigen", "↻ Show operation")); _reopen.Name = "ProgramProgressReopen"; _reopen.Width = 190; _reopen.Visibility = Visibility.Collapsed; _reopen.Click += (_, _) => Expand();
        _layer.Children.Add(_frame); _layer.Children.Add(_reopen); _host.Children.Add(_layer);
        _header.MouseLeftButtonDown += Down; _header.MouseMove += Move; _header.MouseLeftButtonUp += Up;
        _frame.SizeChanged += Resize; _owner.SizeChanged += Resize; _owner.Closed += Closed; _owner.StateChanged += OwnerStateChanged;
        ApplyPalette(); Resize(null, null); _frame.Dispatcher.BeginInvoke(() => Resize(null, null)); view.NotifyOverlayChanged();
    }
    private TextBlock Text(string value, double size, bool muted = false) { var text = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap }; text.SetResourceReference(TextBlock.ForegroundProperty, muted ? "UnifiedMuted" : "UnifiedText"); return text; }
    private Button Action(string caption, bool danger = false) { var button = new Button { Content = new TextBlock { Text = caption, Foreground = Brushes.White }, Height = 34, MinHeight = 34, Padding = new(10, 0, 10, 0) }; UnifiedDialogAppearance.Button(_owner, button, danger ? DialogButtonKind.Danger : DialogButtonKind.Primary); AutomationProperties.SetName(button, caption); return button; }
    internal void ApplyPalette() { Progress.ApplyPalette(_view.Palette); Progress.SetResourceReference(Control.ForegroundProperty, "UnifiedAccent"); Progress.SetResourceReference(Control.BorderBrushProperty, "UnifiedAccent"); Progress.SetResourceReference(Control.BackgroundProperty, "InputBrush"); }
    internal void Current(ProgramEntry entry, string phase, ProgramPackage? package = null, string? source = null, string? installedVersion = null)
    {
        _currentId = entry.Id; _program.Text = entry.Name; _icon.Source = _view.Assets.Icon(entry.Id); _fallback.Visibility = _icon.Source is null ? Visibility.Visible : Visibility.Collapsed; Caption(phase);
        if (package?.PortableUpdate is not null) _portable.Add(entry.Id);
        var installed = installedVersion ?? package?.ManagedPackage?.Target.OldVersion ?? package?.PortableUpdate?.InstalledVersion ?? _view.Inventory.Installed.GetValueOrDefault(entry.Id);
        _version.Text = package is null ? entry.Publisher : (installed is null ? "" : installed + " → ") + (package.Version ?? L("Version nicht ermittelbar", "Version unavailable")) + " · " + (source ?? ProgramInstallService.PackageSource(package, _view.English));
        State(entry.Id, phase, false); RenderSteps(entry.Id); Measurement(null);
    }
    internal void Caption(string phase)
    {
        int index = Array.FindIndex(_entries, e => e.Id == _currentId);
        var current = index >= 0 ? _entries[index] : null;
        var suffix = current is null ? "" : ": " + current.Name;
        if (suffix.Length > 0 && phase.EndsWith(suffix, StringComparison.Ordinal)) phase = phase[..^suffix.Length];
        _phase.Text = (index < 0 ? "" : (index + 1) + L(" von ", " of ") + _entries.Length + " · ")
            + (_reinstall && index >= 0 ? L("Neuinstallation läuft · ", "Reinstallation in progress · ") : "") + phase;
    }
    internal void Step(string id, ProgramStepEvent change)
    {
        if (_disposed || _finished || !_stepStates.TryGetValue(id, out var states)) return;
        int n = (int)change.Step;
        if (states[n] == ProgramStepState.Done && change.State != ProgramStepState.Failed) return;
        states[n] = change.State;
        if (!_terminal.Contains(id) && _states.TryGetValue(id, out var status))
            status.Text = StepName(n, id) + (change.State == ProgramStepState.Done ? " ✓" : change.State == ProgramStepState.Active ? " …" : " –");
        if (id == _currentId && change.State == ProgramStepState.Active && !_stopRequested)
        { Caption(StepName(n, id)); Measurement(null); }
        RenderSteps(id);
    }
    private void RenderSteps(string id)
    {
        for (int n = 0; n < 4; n++)
        {
            var state = _stepStates[id][n];
            void Paint(TextBlock text)
            {
                text.Text = n == 1 && state == ProgramStepState.Skipped ? L("Download entfällt", "Download not run") : StepName(n, id) + (state switch { ProgramStepState.Done => " ✓", ProgramStepState.Active => " …", ProgramStepState.Failed => " ✕", ProgramStepState.Unknown => " ?", ProgramStepState.Skipped => " –", _ => " ·" });
                text.ToolTip = state switch { ProgramStepState.Unknown => n == 1 ? L("Die Paketquelle hat den Downloadfortschritt nicht einzeln gemeldet.", "The package source did not report download progress separately.") : L("Keine eindeutige Einzelmeldung erhalten", "No unambiguous stage report received"), ProgramStepState.Skipped => L("Nicht ausgeführt", "Not run"), _ => null };
                if (state == ProgramStepState.Done) text.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 200, 130));
                else if (state == ProgramStepState.Failed) text.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 85, 110));
                else text.SetResourceReference(TextBlock.ForegroundProperty, state == ProgramStepState.Active ? "UnifiedAccent" : "UnifiedMuted");
            }
            Paint(_history[id][n]); if (id == _currentId) Paint(_steps[n]);
        }
        if (!_finished && _currentId == id)
            _note.Text = L("Auswahl gesperrt · Andere Seiten bleiben zum Ansehen erreichbar.", "Selection locked · other pages remain available for viewing.")
                + (_stepStates[id][1] == ProgramStepState.Unknown ? L("\nDownloadfortschritt von WinGet nicht einzeln gemeldet.", "\nWinGet did not report download progress separately.") : "");
    }
    internal void Location(string id, string? path, bool confirmed = false)
    {
        if (_confirmedLocations.Contains(id) && !confirmed) return;
        if (!_paths.TryGetValue(id, out var label)) return;
        var directory = ProgramLocation.DirectoryOf(path);
        if (directory is null && label.Visibility == Visibility.Visible) return;
        label.Text = directory is null ? L("Installationspfad nicht ermittelbar", "Installation path unavailable") : L("Installationspfad: ", "Installation path: ") + directory;
        label.ToolTip = directory; label.Visibility = Visibility.Visible;
        if (confirmed && directory is not null) _confirmedLocations.Add(id);
    }
    internal void State(string id, string status, bool terminal = true)
    {
        if (_states.TryGetValue(id, out var text)) text.Text = status;
        bool success = status is "Bestätigt" or "Verified" or "Portable eingerichtet" or "Portable configured" or "ZIP eingerichtet" or "ZIP configured" or "Bereits installiert" or "Already installed";
        if (terminal)
        {
            _terminal.Add(id);
            if (_markers.TryGetValue(id, out var marker)) { marker.Text = success ? "✓" : "!"; if (success) marker.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 200, 130)); }
            if (success) Step(id, new(ProgramStep.Finish, ProgramStepState.Done));
            if (_stepStates.TryGetValue(id, out var states))
                for (int n = 0; n < 4; n++) if (states[n] is ProgramStepState.Active or ProgramStepState.Waiting)
                    Step(id, new((ProgramStep)n, success ? ProgramStepState.Unknown : states[n] == ProgramStepState.Active ? ProgramStepState.Failed : ProgramStepState.Skipped));
        }
        else if (_markers.TryGetValue(id, out var current)) current.Text = "›";
    }
    internal void Measurement(double? percent) { bool measurable = percent is { } p && double.IsFinite(p); Progress.IsIndeterminate = !measurable; Progress.ShowPercentage = measurable; if (measurable) Progress.Value = Math.Clamp(percent!.Value, 0, 100); }
    internal void Download(ProgramDownload value)
    {
        if (_finished || _disposed) return;
        Measurement(value.Percent);
        static string Size(long bytes) => (bytes / (1024d * 1024)).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) + " MB";
        if (!_stopRequested) Caption("Download · " + Size(value.Received) + (value.Total is { } total ? " / " + Size(total) : L(" · Gesamtgröße unbekannt", " · total size unknown")));
    }
    internal void StopRequested() { _stopRequested = true; _stop.IsEnabled = false; _phase.Text = L("Stop angefordert · laufenden Vorgang zuerst beenden", "Stop requested · waiting for the current operation to finish"); }
    internal void Finish(bool stopped)
    {
        if (_disposed || _finished) return;
        _finished = true; Progress.IsRunning = false; Progress.ShowPercentage = false;
        foreach (var entry in _entries.Where(e => _view.Inventory.Installed.ContainsKey(e.Id))) Location(entry.Id, _view.Inventory.Evidence?.GetValueOrDefault(entry.Id)?.Path);
        foreach (var (id, state) in _states.Where(pair => !_terminal.Contains(pair.Key)))
            state.Text = state.Text is "Wartet" or "Waiting" or "Bereit" or "Ready" ? L("Nicht ausgeführt", "Not run") : L("Nicht bestätigt · siehe Protokoll", "Not verified · see log");
        bool verified = !stopped && _states.Values.All(state => state.Text is "Bestätigt" or "Verified" or "Portable eingerichtet" or "Portable configured" or "ZIP eingerichtet" or "ZIP configured" or "Bereits installiert" or "Already installed");
        if (verified) { Progress.IsIndeterminate = false; Progress.Value = 100; Progress.ShowPercentage = true; }
        else Progress.Visibility = Visibility.Collapsed;
        _title.Text = _reinstall ? stopped ? L("Neuinstallation gestoppt", "Reinstallation stopped") : verified ? L("Neuinstallation abgeschlossen", "Reinstallation completed") : L("Neuinstallation beendet", "Reinstallation finished") : L("Vorgang beendet", "Operation finished");
        foreach (var id in _stepStates.Keys)
        {
            for (int n = 0; n < 4; n++) if (_stepStates[id][n] is ProgramStepState.Active or ProgramStepState.Waiting)
                _stepStates[id][n] = _terminal.Contains(id) ? ProgramStepState.Unknown : ProgramStepState.Skipped;
            RenderSteps(id);
        }
        _phase.Text = stopped ? L("Warteschlange gestoppt", "Queue stopped") : L("Ausführung beendet · Ergebnisse siehe Liste", "Execution finished · see results below");
        _note.Text = L("Auswahl wieder freigegeben · Details stehen im Ereignisprotokoll.", "Selection unlocked · details are in the event log.")
            + (_stepStates.Values.Any(states => states.Contains(ProgramStepState.Unknown)) ? L("\nFragezeichen: Schritt nicht einzeln gemeldet; kein bestätigter Einzelabschluss.", "\nQuestion mark: stage not reported individually; no verified stage completion.") : "");
        _stop.Content = new TextBlock { Text = L("Schließen", "Close"), Foreground = Brushes.White }; _stop.IsEnabled = true;
        AutomationProperties.SetName(_stop, L("Schließen", "Close"));
        _minimize.Visibility = _collapse.Visibility = Visibility.Collapsed;
        _stop.Width = 116; _stop.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(_stop, 1);
        if (!_owner.IsVisible) _owner.Show();
        if (_owner.WindowState == WindowState.Minimized) _owner.WindowState = _restoreState;
        Expand();
        if (_owner.ShowActivated) _owner.Activate();
    }
    internal void Collapse() { if (_disposed || _finished) return; _frame.Visibility = Visibility.Collapsed; _reopen.Visibility = Visibility.Visible; _view.NotifyOverlayChanged(); }
    internal void Expand() { if (_disposed) return; _frame.Visibility = Visibility.Visible; _reopen.Visibility = Visibility.Collapsed; Resize(null, null); _view.NotifyOverlayChanged(); }
    private void Resize(object? sender, SizeChangedEventArgs? e)
    {
        if (_disposed) return;
        _frame.Width = Math.Min(_view.Compact ? 560 : 620, Math.Max(0, _host.ActualWidth - 24));
        _frame.MaxHeight = Math.Max(0, _host.ActualHeight - 108);
        var x = _placed ? Canvas.GetLeft(_frame) : (_host.ActualWidth - _frame.Width) / 2;
        var y = _placed ? Canvas.GetTop(_frame) : (_host.ActualHeight - _frame.ActualHeight) / 2;
        Place(x, y); _placed = _frame.ActualHeight > 0;
        Canvas.SetLeft(_reopen, Math.Max(8, _host.ActualWidth - 204)); Canvas.SetTop(_reopen, 100);
    }
    internal void Place(double x, double y)
    {
        double bottom = Math.Max(8, _host.ActualHeight - _frame.ActualHeight - 8);
        Canvas.SetLeft(_frame, Math.Clamp(x, 8, Math.Max(8, _host.ActualWidth - _frame.Width - 8)));
        Canvas.SetTop(_frame, Math.Clamp(y, Math.Min(96, bottom), bottom));
    }
    private void Down(object sender, MouseButtonEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null && element != _header; element = VisualTreeHelper.GetParent(element))
            if (element is Button) return;
        _drag = e.GetPosition(_host); _header.CaptureMouse(); e.Handled = true;
    }
    private void Move(object sender, System.Windows.Input.MouseEventArgs e) { if (_drag is not { } previous || e.LeftButton != MouseButtonState.Pressed) return; var current = e.GetPosition(_host); Place(Canvas.GetLeft(_frame) + current.X - previous.X, Canvas.GetTop(_frame) + current.Y - previous.Y); _drag = current; }
    private void Up(object sender, MouseButtonEventArgs e) { _drag = null; _header.ReleaseMouseCapture(); }
    private void OwnerStateChanged(object? sender, EventArgs e)
    { if (_owner.WindowState != WindowState.Minimized) _restoreState = _owner.WindowState; }
    private void Closed(object? sender, EventArgs e) => Dispose();
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; Progress.IsRunning = false; _header.ReleaseMouseCapture();
        _owner.Closed -= Closed; _owner.SizeChanged -= Resize; _owner.StateChanged -= OwnerStateChanged; _frame.SizeChanged -= Resize; _host.Children.Remove(_layer); _view.NotifyOverlayChanged();
    }
}
