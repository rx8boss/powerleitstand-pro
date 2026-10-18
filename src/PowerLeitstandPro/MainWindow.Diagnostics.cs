using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Border = System.Windows.Controls.Border;
using Brush = System.Windows.Media.Brush;
using Orientation = System.Windows.Controls.Orientation;
using Path = System.Windows.Shapes.Path;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using AutomationProperties = System.Windows.Automation.AutomationProperties;
using Button = System.Windows.Controls.Button;
using Control = System.Windows.Controls.Control;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Cursors = System.Windows.Input.Cursors;

namespace PowerControlProWpf;

public partial class MainWindow
{
    private readonly SystemDiagnosticsSampler _diagnosticsSampler = new();
    private readonly DispatcherTimer _diagnosticsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _diagnosticsHoverTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _diagnosticsCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private DiagnosticsSnapshot _diagnosticsSnapshot = DiagnosticsSnapshot.Empty;
    private Task<DiagnosticsSnapshot>? _diagnosticsRead;
    private bool _diagnosticsClosed, _driveOpenBusy;
    private Canvas _diagnosticsLayer = null!;
    private Border _diagnosticsPanel = null!;
    private FrameworkElement? _diagnosticsAnchor;
    private string? _diagnosticsKind, _diagnosticsPendingKind;
    private ScrollViewer? _diagnosticsScroll;
    private CultureInfo DiagnosticsCulture => CultureInfo.GetCultureInfo(_english ? "en-US" : "de-DE");

    private void InitializeLiveDiagnostics()
    {
        _diagnosticsLayer = new Canvas();
        Grid.SetRowSpan(_diagnosticsLayer, 4);
        Panel.SetZIndex(_diagnosticsLayer, 700);
        Root.Children.Add(_diagnosticsLayer);
        SizeChanged += (_, _) => UpdateLiveDiagnosticsLabels();
        _diagnosticsPanel = new Border { Width = 360, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Visibility = Visibility.Collapsed };
        _diagnosticsPanel.SetResourceReference(Border.BackgroundProperty, "StandardReferenceStatusBrush");
        _diagnosticsPanel.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        _diagnosticsLayer.Children.Add(_diagnosticsPanel);
        _diagnosticsPanel.MouseEnter += (_, _) => _diagnosticsCloseTimer.Stop();
        _diagnosticsPanel.MouseLeave += (_, _) => _diagnosticsCloseTimer.Start();
        _diagnosticsHoverTimer.Tick += (_, _) => { _diagnosticsHoverTimer.Stop(); if (_diagnosticsPendingKind is { } kind) OpenDiagnosticsHover(kind); };
        _diagnosticsCloseTimer.Tick += (_, _) =>
        {
            _diagnosticsCloseTimer.Stop();
            if (!_diagnosticsPanel.IsMouseOver && !_diagnosticsPanel.IsKeyboardFocusWithin && _diagnosticsAnchor?.IsMouseOver != true && _diagnosticsAnchor?.IsKeyboardFocusWithin != true) CloseDiagnosticsHover();
        };
        foreach (var (label, kind) in new[] { (StandardWindowsLabel, "pc"), (StandardCpuLabel, "cpu"), (StandardRamLabel, "ram"), (StandardGpuLabel, "gpu"), (StandardDiskLabel, "disk"), (StandardNetworkLabel, "network") })
        {
            var target = (FrameworkElement)label.Parent;
            target.Focusable = true;
            target.SetValue(AutomationProperties.NameProperty, kind switch { "pc" => "PC-Informationen", "cpu" => "CPU-Auslastung", "gpu" => "Grafikkarte", "disk" => "Laufwerksdetails", _ => "Netzwerkdetails" });
            target.MouseEnter += (_, _) =>
            {
                _diagnosticsCloseTimer.Stop(); _diagnosticsPendingKind = kind;
                _diagnosticsHoverTimer.Stop(); _diagnosticsHoverTimer.Start();
            };
            target.MouseLeave += (_, _) => { _diagnosticsHoverTimer.Stop(); _diagnosticsCloseTimer.Start(); };
            target.GotKeyboardFocus += (_, _) => OpenDiagnosticsHover(kind);
            target.LostKeyboardFocus += (_, _) => _diagnosticsCloseTimer.Start();
            label.ToolTip = null;
        }
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && _diagnosticsPanel.IsVisible) { CloseDiagnosticsHover(); e.Handled = true; } };
        PreviewMouseDown += (_, e) =>
        {
            if (_diagnosticsPanel.IsVisible && e.OriginalSource is DependencyObject source &&
                !IsInside(source, _diagnosticsPanel) && (_diagnosticsAnchor is null || !IsInside(source, _diagnosticsAnchor))) CloseDiagnosticsHover();
        };
        Deactivated += (_, _) => CloseDiagnosticsHover();
        LayoutUpdated += (_, _) => { if (_diagnosticsKind is not null) PositionDiagnosticsHover(); };
        _diagnosticsTimer.Tick += async (_, _) => await ReadLiveDiagnosticsAsync();
        Loaded += async (_, _) =>
        {
            if (SkipSavedPlacementForTests) return;
            _diagnosticsTimer.Start();
            await ReadLiveDiagnosticsAsync();
        };
        Closed += (_, _) => StopLiveDiagnostics();
    }

    private async Task ReadLiveDiagnosticsAsync()
    {
        if (_diagnosticsClosed || _diagnosticsRead is not null) return;
        _diagnosticsTimer.Interval = TimeSpan.FromSeconds(WindowState == WindowState.Minimized ? 3 : 1);
        var read = _diagnosticsRead = Task.Run(_diagnosticsSampler.Sample);
        try
        {
            var snapshot = await read;
            if (!_diagnosticsClosed) ApplyDiagnosticsSnapshot(snapshot);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Unavailable Windows counters must not interrupt timer/repair controls.
            if (!_diagnosticsClosed) ApplyDiagnosticsSnapshot(DiagnosticsSnapshot.Empty);
        }
        finally { _diagnosticsRead = null; }
    }

    private void StopLiveDiagnostics()
    {
        _diagnosticsClosed = true;
        _diagnosticsTimer.Stop(); CloseDiagnosticsHover();
        if (_diagnosticsRead is { } read) _ = read.ContinueWith(_ => _diagnosticsSampler.Dispose(), TaskScheduler.Default);
        else _diagnosticsSampler.Dispose();
    }

    private string DiagnosticNumber(double? value, string format = "0") => value is { } number && double.IsFinite(number) ? number.ToString(format, DiagnosticsCulture) : "—";
    private string DiagnosticBytes(ulong value) => (value / 1073741824d).ToString("0.#", DiagnosticsCulture) + " GB";

    private void ApplyDiagnosticsSnapshot(DiagnosticsSnapshot snapshot)
    {
        _diagnosticsSnapshot = snapshot;
        // Collection continues in the background; defer only the visual refresh.
        if (_isWindowDragging) return;
        UpdateLiveDiagnosticsLabels();
        if (_diagnosticsKind is not null) RefreshDiagnosticsHover();
    }

    private void UpdateLiveDiagnosticsLabels()
    {
        var data = _diagnosticsSnapshot;
        ((FrameworkElement)StandardDiskLabel.Parent).SetValue(AutomationProperties.NameProperty, _english ? "Drive details" : "Laufwerksdetails");
        ((FrameworkElement)StandardNetworkLabel.Parent).SetValue(AutomationProperties.NameProperty, _english ? "Network details" : "Netzwerkdetails");
        UpdateGpuMetricLayout();
        var gpu = data.Gpus.FirstOrDefault(g => g.IsPrimary) ?? data.Gpus.FirstOrDefault();
        StandardGpuLabel.Text = "GPU: " + DiagnosticNumber(gpu?.Usage) + (gpu?.Usage is not null ? "%" : "") + " · " + DiagnosticNumber(gpu?.Temperature) + " °C";
        ((FrameworkElement)StandardGpuLabel.Parent).SetValue(AutomationProperties.NameProperty, _english ? "Graphics card details" : "Grafikkartendetails");
        StandardWindowsLabel.Text = data.Pc?.Windows ?? "Windows";
        StandardCpuLabel.Text = "CPU: " + DiagnosticNumber(data.Cpu) + (data.Cpu.HasValue ? "%" : "");
        StandardRamLabel.Text = "RAM: " + DiagnosticNumber(data.Ram) + (data.Ram.HasValue ? "%" : "");
        StandardDiskLabel.Text = (_english ? "Disk: " : ActualWidth >= 1100 ? "Datentr.: " : "Datenträger: ") + DiagnosticNumber(data.Disk) + (data.Disk.HasValue ? "%" : "");
        StandardNetworkLabel.Text = (_english ? "Net: " : "Netz: ") + DiagnosticNumber(data.NetworkTotal, "0.#") + (data.NetworkTotal.HasValue ? " MBit/s" : "");
        StandardCpuLabel.ToolTip = null;
        StandardRamLabel.ToolTip = null;
        foreach (var label in new[] { StandardWindowsLabel, StandardCpuLabel, StandardRamLabel, StandardDiskLabel, StandardGpuLabel, StandardNetworkLabel })
        {
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            if (label.Parent is FrameworkElement parent && parent.ActualWidth > 50) label.MaxWidth = Math.Max(20, parent.ActualWidth - 29);
        }
    }

    private void UpdateGpuMetricLayout()
    {
        const bool wide = false;
        const int columns = 3;
        if (StandardMetricGrid.ColumnDefinitions.Count == columns) return;
        StandardMetricGrid.ColumnDefinitions.Clear(); StandardMetricGrid.RowDefinitions.Clear();
        for (var i = 0; i < columns; i++)
            StandardMetricGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(wide && i == 4 ? 1.45 : wide && i == 5 ? 1.15 : 1, GridUnitType.Star) });
        for (var i = 0; i < (wide ? 1 : 2); i++) StandardMetricGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
        for (var i = 0; i < StandardMetricGrid.Children.Count; i++)
        {
            var item = (FrameworkElement)StandardMetricGrid.Children[i];
            Grid.SetColumn(item, i % columns); Grid.SetRow(item, i / columns);
            item.Margin = new Thickness(0, 0, 5, 0);
        }
    }

    private void OpenDiagnosticsHover(string kind)
    {
        if (_diagnosticsClosed || SettingsShell.Visibility == Visibility.Visible || kind is not ("pc" or "cpu" or "ram" or "gpu" or "disk" or "network")) return;
        _diagnosticsHoverTimer.Stop(); _diagnosticsCloseTimer.Stop();
        _diagnosticsKind = kind;
        _diagnosticsAnchor = (FrameworkElement)(kind switch { "pc" => StandardWindowsLabel.Parent, "cpu" => StandardCpuLabel.Parent, "ram" => StandardRamLabel.Parent, "gpu" => StandardGpuLabel.Parent, "disk" => StandardDiskLabel.Parent, _ => StandardNetworkLabel.Parent });
        RefreshDiagnosticsHover();
        _diagnosticsPanel.Visibility = Visibility.Visible;
        PositionDiagnosticsHover();
    }

    private void CloseDiagnosticsHover()
    {
        _diagnosticsHoverTimer.Stop(); _diagnosticsCloseTimer.Stop();
        _diagnosticsKind = _diagnosticsPendingKind = null;
        if (_diagnosticsPanel is not null) _diagnosticsPanel.Visibility = Visibility.Collapsed;
    }

    private TextBlock DiagnosticText(string text, double size, bool strong = false, string brush = "TextBrush")
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return block;
    }

    private static Path DiagnosticIcon(string data, double size = 19)
    {
        var path = new Path { Data = Geometry.Parse(data), Width = size, Height = size, Stretch = Stretch.Uniform, StrokeThickness = 1.6, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, VerticalAlignment = System.Windows.VerticalAlignment.Center };
        path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentBrush");
        return path;
    }

    private Border DiagnosticRow(UIElement content, Thickness padding, double bottom = 3)
    {
        var border = new Border { Padding = padding, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 0, bottom), Child = content };
        border.SetResourceReference(Border.BackgroundProperty, "StandardActionButtonBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "InputBorderBrush");
        return border;
    }

    private UIElement DriveDiagnosticRow(DriveDiagnostic drive)
    {
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(27) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(DiagnosticIcon("M 5,3 H 19 L 22,19 H 2 Z M 3,15 H 21 M 17,17 H 19"));
        var content = new StackPanel(); Grid.SetColumn(content, 1); grid.Children.Add(content);
        var heading = new Grid(); heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = DiagnosticText(drive.Name + (string.IsNullOrWhiteSpace(drive.Label) ? "" : " " + drive.Label), 11.5, true);
        name.ToolTip = name.Text; name.Margin = new Thickness(0, 0, 6, 0); heading.Children.Add(name);
        var percent = DiagnosticText(DiagnosticNumber(drive.Percent) + (drive.Percent.HasValue ? " %" : ""), 11.5, true, "AccentBrush"); Grid.SetColumn(percent, 1); heading.Children.Add(percent); content.Children.Add(heading);
        var detail = new Grid { Margin = new Thickness(0, 2, 0, 0) }; detail.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(129) }); detail.ColumnDefinitions.Add(new ColumnDefinition());
        var bytes = drive.Used is { } used && drive.Total is { } total ? $"{(used / 1073741824d).ToString("0.#", DiagnosticsCulture)} / {(total / 1073741824d).ToString("0.#", DiagnosticsCulture)} GB " + (_english ? "used" : "belegt") : _english ? "Unavailable" : "Nicht verfügbar";
        detail.Children.Add(DiagnosticText(bytes, 10, brush: "SubtleTextBrush"));
        var bar = new Border { Height = 4, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center };
        bar.SetResourceReference(Border.BackgroundProperty, "InputBrush"); Grid.SetColumn(bar, 1); detail.Children.Add(bar);
        if (drive.Percent is { } fill)
        {
            var track = new Grid(); track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fill, GridUnitType.Star) }); track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - fill, GridUnitType.Star) });
            var filled = new Border { CornerRadius = new CornerRadius(2) }; filled.SetResourceReference(Border.BackgroundProperty, "AccentBrush"); track.Children.Add(filled); bar.Child = track;
        }
        content.Children.Add(detail);
        percent.Text += "  ›";
        var row = DiagnosticRow(grid, new Thickness(6, 2, 6, 2), 0);
        var button = new Button { Content = row, Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 3),
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch, Cursor = Cursors.Hand,
            IsEnabled = drive.Used.HasValue, ToolTip = _english ? "Open in Explorer" : "Im Explorer öffnen" };
        button.Template = (ControlTemplate)XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'><ContentPresenter HorizontalAlignment='Stretch'/></ControlTemplate>");
        void Highlight(bool active)
        {
            row.SetResourceReference(Border.BackgroundProperty, active ? "HoverBrush" : "StandardActionButtonBrush");
            row.SetResourceReference(Border.BorderBrushProperty, active ? "AccentBrush" : "InputBorderBrush");
        }
        button.MouseEnter += (_, _) => Highlight(true);
        button.MouseLeave += (_, _) => Highlight(button.IsKeyboardFocusWithin);
        button.GotKeyboardFocus += (_, _) => Highlight(true);
        button.LostKeyboardFocus += (_, _) => Highlight(button.IsMouseOver);
        button.Click += async (_, _) => await OpenDiagnosticDriveAsync(drive);
        if (!button.IsEnabled) row.Opacity = .6;
        return button;
    }

    private async Task OpenDiagnosticDriveAsync(DriveDiagnostic drive)
    {
        if (_driveOpenBusy || drive.Used is null) return;
        _driveOpenBusy = true;
        try
        {
            var start = DrivePathLauncher.CreateStartInfo(drive.Name);
            if (!await Task.Run(() => System.IO.Directory.Exists(start.ArgumentList[0])).WaitAsync(TimeSpan.FromSeconds(3)))
                throw new System.IO.IOException(_english ? "Drive unavailable." : "Laufwerk nicht verfügbar.");
            if (!_diagnosticsClosed) System.Diagnostics.Process.Start(start)?.Dispose();
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException or TimeoutException)
        {
            if (!_diagnosticsClosed) ThemedMessageBox.Show((_english ? "The drive could not be opened: " : "Das Laufwerk konnte nicht geöffnet werden: ") + e.Message,
                "PowerLeitstand PRO", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _driveOpenBusy = false; }
    }

    private string CpuTopologyText()
    {
        var pc = _diagnosticsSnapshot.Pc;
        string Count(int? value) => value is > 0 ? value.Value.ToString(DiagnosticsCulture) : "—";
        return $"{Count(pc?.Packages)} CPU · {Count(pc?.Cores)} " + (_english ? "cores" : "Kerne") +
            $" · {Count(pc?.LogicalProcessors)} " + (_english ? "logical processors" : "logische Prozessoren");
    }

    private void BuildPcInformation(StackPanel rows)
    {
        var data = _diagnosticsSnapshot;
        var pc = data.Pc;
        void Add(string label, string? value)
        {
            var content = new StackPanel();
            content.Children.Add(DiagnosticText(label, 10, brush: "SubtleTextBrush"));
            var text = DiagnosticText(string.IsNullOrWhiteSpace(value) ? (_english ? "Unavailable" : "Nicht verfügbar") : value, 11, true);
            text.TextWrapping = TextWrapping.Wrap; text.Margin = new Thickness(0, 2, 0, 0); content.Children.Add(text);
            rows.Children.Add(DiagnosticRow(content, new Thickness(7, 4, 7, 4), 4));
        }
        Add("Windows", pc?.WindowsDetails);
        Add(_english ? "Processor" : "Prozessor", pc?.CpuModel);
        Add(_english ? "CPU topology" : "CPU-Aufbau", CpuTopologyText());
        Add(_english ? "Memory" : "Arbeitsspeicher", data.TotalMemory is { } total ? DiagnosticBytes(total) : null);
        Add(_english ? "Graphics" : "Grafikkarte", pc?.Graphics);
        Add("Mainboard", pc?.Mainboard);
        Add(_english ? "Drives" : "Laufwerke", data.Drives.Count == 0 ? null : string.Join(" · ", data.Drives.Select(d => d.Name)));
        Add(_english ? "Network" : "Netzwerk", data.NetworkAvailable ? data.Networks.Count == 0 ? (_english ? "No active connection" : "Keine aktive Verbindung") : string.Join(" · ", data.Networks.Select(n => n.Kind + ": " + n.Name)) : null);
    }

    private void BuildProcessorInformation(StackPanel rows)
    {
        var model = DiagnosticText(_diagnosticsSnapshot.Pc?.CpuModel ?? "—", 10, brush: "SubtleTextBrush");
        model.TextWrapping = TextWrapping.Wrap; model.Margin = new Thickness(0, 0, 0, 8); rows.Children.Add(model);
        rows.Children.Add(ProcessorUsageRow(_english ? "Total" : "Gesamt", _diagnosticsSnapshot.Cpu));
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
        var processors = _diagnosticsSnapshot.Processors;
        for (var i = 0; i < processors.Count; i++)
        {
            if (i % 2 == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var card = ProcessorUsageRow("Thread " + (i + 1), processors[i].Usage);
            card.Margin = new Thickness(i % 2 == 0 ? 0 : 3, 0, i % 2 == 0 ? 3 : 0, 4);
            Grid.SetRow(card, i / 2); Grid.SetColumn(card, i % 2); grid.Children.Add(card);
        }
        rows.Children.Add(grid);
    }

    private Border ProcessorUsageRow(string label, double? usage)
    {
        var content = new StackPanel();
        var line = new Grid(); line.ColumnDefinitions.Add(new ColumnDefinition()); line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.Children.Add(DiagnosticText(label, 11, true));
        var percent = DiagnosticText(DiagnosticNumber(usage) + (usage.HasValue ? " %" : ""), 11, true, "AccentBrush"); Grid.SetColumn(percent, 1); line.Children.Add(percent); content.Children.Add(line);
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Value = usage ?? 0, Height = 4, Margin = new Thickness(0, 4, 0, 0) };
        progress.SetResourceReference(Control.ForegroundProperty, "AccentBrush"); progress.SetResourceReference(Control.BackgroundProperty, "InputBrush"); progress.BorderThickness = new Thickness(0); content.Children.Add(progress);
        return DiagnosticRow(content, new Thickness(6), 4);
    }

    private void BuildRamInformation(StackPanel rows)
    {
        var data = _diagnosticsSnapshot;
        var memory = new StackPanel(); memory.Children.Add(DiagnosticText(_english ? "Used / usable" : "Belegt / nutzbar", 11, true));
        var usage = data.TotalMemory is { } total && data.AvailableMemory is { } free ? DiagnosticBytes(total - free) + " / " + DiagnosticBytes(total) + " · " + DiagnosticNumber(data.Ram) + " %" : (_english ? "Unavailable" : "Nicht verfügbar");
        var value = DiagnosticText(usage, 12, true, "AccentBrush"); value.Margin = new Thickness(0, 4, 0, 5); memory.Children.Add(value);
        if (data.Ram is { } percent)
        {
            var track = new Grid { Height = 5 }; track.SetResourceReference(Panel.BackgroundProperty, "InputBrush");
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(percent, GridUnitType.Star) }); track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - percent, GridUnitType.Star) });
            var fill = new Border { CornerRadius = new CornerRadius(2) }; fill.SetResourceReference(Border.BackgroundProperty, "AccentBrush"); track.Children.Add(fill); memory.Children.Add(track);
        }
        var available = DiagnosticText((_english ? "Available: " : "Verfügbar: ") + (data.AvailableMemory is { } remaining ? DiagnosticBytes(remaining) : "—"), 10, brush: "SubtleTextBrush"); available.Margin = new Thickness(0, 5, 0, 0); memory.Children.Add(available);
        rows.Children.Add(DiagnosticRow(memory, new Thickness(8), 5));
        var hardware = data.RamHardware;
        var summary = DiagnosticText(hardware.Slots > 0 ? $"{hardware.Modules.Count} / {hardware.Slots} " + (_english ? "slots populated" : "Steckplätze belegt") : (_english ? "Module details unavailable" : "Moduldetails nicht verfügbar"), 11);
        summary.Margin = new Thickness(0, 3, 0, 6); rows.Children.Add(summary);
        foreach (var module in hardware.Modules)
        {
            var stack = new StackPanel();
            void Line(string text, bool bold = false, string brush = "TextBrush") { var block = DiagnosticText(text, 11, bold, brush); block.TextWrapping = TextWrapping.Wrap; block.Margin = new Thickness(0, 2, 0, 0); stack.Children.Add(block); }
            Line((string.IsNullOrWhiteSpace(module.Slot) ? (_english ? "Module" : "Modul") : module.Slot) + " · " + (module.Bytes is { } bytes ? DiagnosticBytes(bytes) : "—"), true, "AccentBrush");
            Line(string.Join(" · ", new[] { module.Manufacturer, module.PartNumber }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } name ? name : (_english ? "Manufacturer / model unavailable" : "Hersteller / Modell nicht verfügbar"));
            Line((_english ? "Type: " : "Typ: ") + (string.IsNullOrEmpty(module.Type) ? "—" : module.Type));
            Line(module.ConfiguredSpeed is { } speed ? (_english ? "Configured (BIOS): " : "Eingestellt (BIOS): ") + speed + " " + module.SpeedUnit : module.RatedSpeed is { } rated ? (_english ? "Rated (BIOS): " : "Nennwert (BIOS): ") + rated + " " + module.SpeedUnit : (_english ? "Speed unavailable" : "Geschwindigkeit nicht verfügbar"));
            rows.Children.Add(DiagnosticRow(stack, new Thickness(8), 5));
        }
    }

    private void BuildGpuInformation(StackPanel rows)
    {
        if (_diagnosticsSnapshot.Gpus.Count == 0)
        {
            var missing = DiagnosticText(_english ? "Detecting graphics card or no driver data available…" : "Grafikkarte wird ermittelt oder Treiberdaten sind nicht verfügbar…", 11);
            missing.TextWrapping = TextWrapping.Wrap; rows.Children.Add(missing); return;
        }
        foreach (var gpu in _diagnosticsSnapshot.Gpus)
        {
            if (_diagnosticsSnapshot.Gpus.Count > 1)
            {
                var name = DiagnosticText(gpu.Name + (gpu.IsPrimary ? (_english ? " · main display" : " · Hauptanzeige") : ""), 12, true, "AccentBrush");
                name.TextWrapping = TextWrapping.Wrap; name.Margin = new Thickness(0, 5, 0, 8); rows.Children.Add(name);
            }
            var readings = new Grid(); readings.ColumnDefinitions.Add(new ColumnDefinition()); readings.ColumnDefinitions.Add(new ColumnDefinition());
            void AddReading(string key, string value, int column)
            {
                var stack = new StackPanel(); stack.Children.Add(DiagnosticText(key, 10, brush: "SubtleTextBrush"));
                stack.Children.Add(DiagnosticText(value, 18, true, "AccentBrush")); Grid.SetColumn(stack, column); readings.Children.Add(stack);
            }
            AddReading(_english ? "Utilization" : "Auslastung", DiagnosticNumber(gpu.Usage) + (gpu.Usage.HasValue ? " %" : ""), 0);
            AddReading(_english ? "Temperature" : "Temperatur", DiagnosticNumber(gpu.Temperature) + " °C", 1);
            rows.Children.Add(DiagnosticRow(readings, new Thickness(8), 5));
            var memory = new StackPanel(); memory.Children.Add(DiagnosticText(_english ? "Graphics memory (VRAM)" : "Grafikspeicher (VRAM)", 11, true));
            string Mib(ulong bytes) => (bytes / 1048576d).ToString("0", DiagnosticsCulture) + " MiB";
            var usage = gpu.MemoryPercent is not null ? $"{Mib(gpu.UsedMemory!.Value)} " + (_english ? "of " : "von ") + Mib(gpu.TotalMemory!.Value) + (_english ? " used" : " belegt") : (_english ? "Usage unavailable" : "Belegung nicht verfügbar");
            var usageText = DiagnosticText(usage, 11); usageText.Margin = new Thickness(0, 4, 0, 5); memory.Children.Add(usageText);
            var bar = new Border { Height = 5, CornerRadius = new CornerRadius(2) }; bar.SetResourceReference(Border.BackgroundProperty, "InputBrush");
            if (gpu.MemoryPercent is { } percent)
            {
                var track = new Grid(); track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(percent, GridUnitType.Star) }); track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - percent, GridUnitType.Star) });
                var fill = new Border { CornerRadius = new CornerRadius(2) }; fill.SetResourceReference(Border.BackgroundProperty, "AccentBrush"); track.Children.Add(fill); bar.Child = track;
            }
            memory.Children.Add(bar);
            var total = DiagnosticText(gpu.TotalMemory is > 0 ? (_english ? "Total: " : "Gesamt: ") + Mib(gpu.TotalMemory.Value) + (_english ? " dedicated graphics memory" : " eigener Grafikspeicher") : (_english ? "Total memory unavailable" : "Gesamtspeicher nicht verfügbar"), 10, brush: "SubtleTextBrush");
            total.TextWrapping = TextWrapping.Wrap; total.Margin = new Thickness(0, 5, 0, 0); memory.Children.Add(total); rows.Children.Add(DiagnosticRow(memory, new Thickness(8), 5));
            var type = new StackPanel(); type.Children.Add(DiagnosticText(_english ? "Memory type" : "Speichertyp", 10, brush: "SubtleTextBrush"));
            var value = DiagnosticText(gpu.MemoryType ?? (_english ? "Not reported by driver" : "Vom Treiber nicht gemeldet"), 11); value.Margin = new Thickness(0, 2, 0, 0); type.Children.Add(value); rows.Children.Add(DiagnosticRow(type, new Thickness(8), 7));
        }
    }

    private UIElement NetworkDiagnosticRow(NetworkDiagnostic network)
    {
        var stack = new StackPanel(); var title = new Grid(); title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(25) }); title.ColumnDefinitions.Add(new ColumnDefinition());
        title.Children.Add(DiagnosticIcon(network.Kind == "WLAN" ? "M 1,6 Q 11,-3 21,6 M 5,10 Q 11,4 17,10 M 9,14 Q 11,12 13,14 M 11,17 L 11,18" : "M 4,3 H 18 V 14 H 4 Z M 11,14 V 19 M 5,19 H 17"));
        var kind = network.Kind switch { "WLAN" => "WLAN / WiFi", "LAN" => "LAN / Ethernet", _ => _english ? "Other / virtual" : "Andere / virtuell" };
        var heading = DiagnosticText(kind + (_english ? " · active" : " · aktiv"), 12, true, "AccentBrush"); Grid.SetColumn(heading, 1); title.Children.Add(heading); stack.Children.Add(title);
        if (network.Kind == "WLAN") { var ssid = DiagnosticText((_english ? "Network: " : "Netzwerk: ") + (network.Ssid ?? (_english ? "Name unavailable" : "Name nicht verfügbar")), 11); ssid.Margin = new Thickness(0, 6, 0, 0); ssid.ToolTip = ssid.Text; stack.Children.Add(ssid); }
        var adapter = DiagnosticText("Adapter: " + network.Name, 11); adapter.Margin = new Thickness(0, 5, 0, 0); adapter.ToolTip = adapter.Text; stack.Children.Add(adapter);
        var rate = DiagnosticText((_english ? "Receive: " : "Empfang: ") + DiagnosticNumber(network.Receive, "0.#") + " MBit/s · " + (_english ? "Send: " : "Versand: ") + DiagnosticNumber(network.Send, "0.#") + " MBit/s", 10.5, brush: "SubtleTextBrush");
        rate.TextWrapping = TextWrapping.Wrap; rate.Margin = new Thickness(0, 5, 0, 0); stack.Children.Add(rate);
        return DiagnosticRow(stack, new Thickness(8), 7);
    }

    private void RefreshDiagnosticsHover()
    {
        if (_diagnosticsKind is null) return;
        if (_diagnosticsPanel.IsVisible && (Mouse.LeftButton == MouseButtonState.Pressed || (_diagnosticsKind == "disk" && _diagnosticsPanel.IsKeyboardFocusWithin))) return;
        var offset = _diagnosticsScroll?.VerticalOffset ?? 0;
        var frame = new Grid { ClipToBounds = true };
        var stripes = (Grid)XamlReader.Parse("""
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Width="86" Height="54" HorizontalAlignment="Right" VerticalAlignment="Top" IsHitTestVisible="False" Margin="0,1,1,0">
              <Path Data="M 115,0 L 120,0 L 120,44 L 82,75 L 28,75 Z" Fill="{DynamicResource AccentBrush}" Opacity="0.12" Stretch="Fill"/>
              <Path Data="M 90,0 L 15,75 M 113,0 L 38,75" Stroke="{DynamicResource AccentBrush}" StrokeThickness="0.7" Opacity="0.55" Stretch="Fill"/>
            </Grid>
            """);
        frame.Children.Add(stripes);
        var layout = new Grid { Margin = new Thickness(12) }; layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); frame.Children.Add(layout);
        var disk = _diagnosticsKind == "disk";
        var title = _diagnosticsKind switch { "pc" => _english ? "PC information" : "PC-Informationen", "cpu" => _english ? "CPU utilization" : "CPU-Auslastung", "ram" => _english ? "Memory" : "Arbeitsspeicher", "gpu" => _english ? "Graphics card" : "Grafikkarte", "disk" => _english ? "Drives" : "Laufwerke", _ => _english ? "Network" : "Netzwerk" };
        var heading = new StackPanel(); heading.Children.Add(DiagnosticText(title, 16, true));
        var description = _diagnosticsKind switch { "pc" => _english ? "Automatically detected hardware" : "Automatisch erkannte Geräteinformationen", "cpu" => CpuTopologyText(), "ram" => _english ? "Memory usage and detected modules" : "RAM-Auslastung und erkannte Speichermodule", "gpu" => _diagnosticsSnapshot.Gpus.Count == 1 ? _diagnosticsSnapshot.Gpus[0].Name : _english ? "Automatically detected graphics cards" : "Automatisch erkannte Grafikkarten", "disk" => _english ? "Storage usage per drive" : "Speicherbelegung je Laufwerk", _ => _english ? "Active connections" : "Aktive Verbindungen" };
        _diagnosticsPanel.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        var gpuHeading = _diagnosticsKind == "gpu";
        var subtitle = DiagnosticText(description, gpuHeading ? 12 : 11, gpuHeading, gpuHeading ? "AccentBrush" : "SubtleTextBrush");
        subtitle.TextWrapping = TextWrapping.Wrap;
        subtitle.Margin = new Thickness(0, gpuHeading ? 5 : 3, 0, 10); heading.Children.Add(subtitle); layout.Children.Add(heading);
        var rows = new StackPanel();
        if (_diagnosticsKind == "pc") BuildPcInformation(rows);
        else if (_diagnosticsKind == "cpu") BuildProcessorInformation(rows);
        else if (_diagnosticsKind == "ram") BuildRamInformation(rows);
        else if (_diagnosticsKind == "gpu") BuildGpuInformation(rows);
        else if (disk)
        {
            foreach (var drive in _diagnosticsSnapshot.Drives) rows.Children.Add(DriveDiagnosticRow(drive));
            if (rows.Children.Count == 0) rows.Children.Add(DiagnosticText(_english ? "Drives unavailable or loading…" : "Laufwerke werden ermittelt oder sind nicht verfügbar…", 11));
        }
        else
        {
            foreach (var network in _diagnosticsSnapshot.Networks) rows.Children.Add(NetworkDiagnosticRow(network));
            if (rows.Children.Count == 0) rows.Children.Add(DiagnosticText(_diagnosticsSnapshot.NetworkAvailable ? _english ? "No active connection" : "Keine aktive Verbindung" : _english ? "Network measurement unavailable" : "Netzwerkmessung nicht verfügbar", 11));
        }
        _diagnosticsScroll = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = Math.Max(70, Math.Min(300, ActualHeight - 200)) };
        Grid.SetRow(_diagnosticsScroll, 1); layout.Children.Add(_diagnosticsScroll);
        var note = DiagnosticText(_diagnosticsKind == "ram" ? "Windows / SMBIOS" : _diagnosticsKind == "gpu" ? (_diagnosticsSnapshot.Gpus.Any(g => g.Source == "NVIDIA") ? (_english ? "NVIDIA driver · updated periodically" : "NVIDIA-Treiber · regelmäßige Aktualisierung") : (_english ? "Windows · driver telemetry unavailable" : "Windows · Treibermesswerte nicht verfügbar")) : _diagnosticsKind is "pc" or "cpu" ? (_english ? "Detected locally by Windows" : "Lokal von Windows erkannt") : disk ? (_english ? "Activity in status bar: " : "Aktivität in der Statusleiste: ") + _diagnosticsSnapshot.SystemDrive : "MBit/s", 10, brush: "SubtleTextBrush"); note.Margin = new Thickness(0, 7, 0, 0); Grid.SetRow(note, 2); layout.Children.Add(note);
        if (ThemePalette.For(SelectedTheme()).IsNeon)
        {
            var glow = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), IsHitTestVisible = false };
            glow.SetResourceReference(Border.BorderBrushProperty, "AccentBrush"); glow.SetResourceReference(UIElement.EffectProperty, "NeonGlow"); frame.Children.Insert(0, glow);
        }
        _diagnosticsPanel.Child = frame;
        _diagnosticsPanel.UpdateLayout(); _diagnosticsScroll.ScrollToVerticalOffset(offset);
        PositionDiagnosticsHover();
    }

    private void PositionDiagnosticsHover()
    {
        if (_diagnosticsKind is null || _diagnosticsAnchor is null) return;
        if (SettingsShell.Visibility == Visibility.Visible || WindowState == WindowState.Minimized) { CloseDiagnosticsHover(); return; }
        _diagnosticsPanel.Measure(new Size(360, Math.Max(120, ActualHeight - 16)));
        var size = _diagnosticsPanel.DesiredSize;
        var rootOffset = Root.TranslatePoint(new Point(0, 0), this);
        var anchor = _diagnosticsAnchor.TranslatePoint(new Point(0, 0), this);
        var status = StandardStatusLogBar.TranslatePoint(new Point(0, 0), this);
        var left = Math.Clamp(anchor.X, 8, Math.Max(8, ActualWidth - size.Width - 8)) - rootOffset.X;
        var top = Math.Clamp(status.Y - size.Height - 10, 8, Math.Max(8, ActualHeight - size.Height - 8)) - rootOffset.Y;
        if (Math.Abs(Canvas.GetLeft(_diagnosticsPanel) - left) > .1 || double.IsNaN(Canvas.GetLeft(_diagnosticsPanel))) Canvas.SetLeft(_diagnosticsPanel, left);
        if (Math.Abs(Canvas.GetTop(_diagnosticsPanel) - top) > .1 || double.IsNaN(Canvas.GetTop(_diagnosticsPanel))) Canvas.SetTop(_diagnosticsPanel, top);
    }
}
