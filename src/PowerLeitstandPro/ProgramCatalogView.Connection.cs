using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Button = System.Windows.Controls.Button;
using Grid = System.Windows.Controls.Grid;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Panel = System.Windows.Controls.Panel;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Brush = System.Windows.Media.Brush;
using Orientation = System.Windows.Controls.Orientation;

namespace PowerControlProWpf;

internal sealed partial class ProgramCatalogView
{
    private readonly Grid _connectionLayer = new() { Visibility = Visibility.Collapsed };
    private Border? _checkProgressFrame;
    private Ellipse? _checkProgressDot;
    private TextBlock? _checkProgressTitle, _checkProgressDescription;
    private CancellationTokenSource? _inspectionCancellation;
    private bool _connectionEvents, _retrySearch;
    private CancellationTokenSource? _addressProbe;
    private Exception? _connectionFailure;
    private readonly Dictionary<string, (bool Complete, string? Version)> _versionChecks = [];
    internal bool HasOverlay => OperationPanel?.Expanded == true || _selectionOpen || _info?.IsOpen == true || _connectionLayer.Visibility == Visibility.Visible;
    internal bool HasLoadingOverlay => _connectionLayer.Visibility == Visibility.Visible && _connectionFailure is null;
    internal void NotifyOverlayChanged() => _page.ProgramOverlayChanged?.Invoke();
    internal void VersionChecking(string id) => _versionChecks[id] = (false, null);
    internal void VersionChecked(string id, string? version) { _versionChecks[id] = (true, version); RenderTiles(); }
    internal bool ShowManualMark(string id) => !Inventory.Installed.ContainsKey(id)
        && (!ProgramVersions.StoreFamilies.ContainsKey(id) || Inventory.StoreCheckComplete)
        && _versionChecks.TryGetValue(id, out var check) && check.Complete && string.IsNullOrWhiteSpace(check.Version);
    private void InitializeConnectionOverlay()
    {
        Grid.SetRow(_connectionLayer, 3); Grid.SetRowSpan(_connectionLayer, 2); Panel.SetZIndex(_connectionLayer, 1000); _root.Children.Add(_connectionLayer);
        _connectionLayer.SizeChanged += (_, _) => FitConnectionCard();
        Loaded += (_, _) => { if (!_connectionEvents) { NetworkChange.NetworkAvailabilityChanged += NetworkChanged; NetworkChange.NetworkAddressChanged += AddressChanged; _connectionEvents = true; } };
        Unloaded += (_, _) =>
        {
            if (_connectionEvents) { NetworkChange.NetworkAvailabilityChanged -= NetworkChanged; NetworkChange.NetworkAddressChanged -= AddressChanged; _connectionEvents = false; }
            _addressProbe?.Cancel();
            var current = _inspectionCancellation; _inspectionCancellation = null; current?.Cancel(); _refreshing = false; HideConnectionOverlay();
        };
    }
    private void NetworkChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (Dispatcher.HasShutdownStarted) return;
        try { Dispatcher.BeginInvoke(() => ConnectionChanged(e.IsAvailable)); } catch (InvalidOperationException) { }
    }
    private void AddressChanged(object? sender, EventArgs args)
    {
        if (Dispatcher.HasShutdownStarted) return;
        try { Dispatcher.BeginInvoke(() => { _ = CheckAddressAsync(); }); } catch (InvalidOperationException) { }
    }
    private async Task CheckAddressAsync()
    {
        if (!IsVisible || IsWorking) return;
        OnlineConnection.Invalidate(); _addressProbe?.Cancel();
        using var current = new CancellationTokenSource(TimeSpan.FromSeconds(10)); _addressProbe = current;
        try { await OnlineConnection.CheckAsync(current.Token); }
        catch (OperationCanceledException) { }
        catch (OnlineConnectionException error)
        {
            if (IsVisible && _addressProbe == current && !IsWorking)
            { _inspectionCancellation?.Cancel(); CancelSearch(); ShowConnectionOverlay(error); }
        }
        finally { if (_addressProbe == current) _addressProbe = null; }
    }
    internal void ConnectionChanged(bool available)
    {
        if (!IsVisible) return;
        if (available) { _ = Assets.RetryMissingAsync(); return; }
        _inspectionCancellation?.Cancel(); CancelSearch();
        if (IsWorking) { _phase = L("Internetverbindung verloren · laufenden Installer zuerst beenden", "Connection lost · finish the running installer first"); RefreshAvailability(); return; }
        ShowConnectionOverlay(new OnlineConnectionException(true, lost: true), false);
    }
    private void HideConnectionOverlay()
    {
        _checkProgressFrame = null; _checkProgressDot = null; _checkProgressTitle = _checkProgressDescription = null;
        _connectionLayer.Visibility = Visibility.Collapsed; _connectionLayer.Children.Clear(); UpdateCategoryVisibility(); NotifyOverlayChanged();
    }
    private void UpdateCheckProgressOverlay()
    {
        if (_checkProgressFrame is null || _checkProgressDot is null || _checkProgressTitle is null || _checkProgressDescription is null) return;
        bool partial = _checkStage == CheckStage.Partial || _checkHasFreshResults && (_checkStage is CheckStage.Stopped or CheckStage.Failed);
        bool complete = _checkStage == CheckStage.Complete;
        string title = complete ? L("Prüfung abgeschlossen", "Check complete")
            : partial ? L("Teilergebnisse gefunden", "Partial results found")
            : (_checkStage is CheckStage.Stopped or CheckStage.Failed) ? L("Prüfung nicht abgeschlossen", "Check not completed")
            : L("Suche läuft", "Scan in progress");
        string description = complete ? L("Die Ergebnisse stehen unten in der Programmliste.", "Results are shown below in the program list.")
            : partial ? L("Weitere Programme und Updatequellen werden noch geprüft.", "More programs and update sources are still being checked.")
            : (_checkStage is CheckStage.Stopped or CheckStage.Failed) ? L("Es liegt kein vollständiges Prüfergebnis vor.", "A complete check result is not available.")
            : L("Installierte Programme und Updatequellen werden im Hintergrund abgefragt.", "Installed programs and update sources are being checked in the background.");
        var brush = _status.Foreground;
        _checkProgressFrame.BorderBrush = brush; _checkProgressDot.Fill = brush;
        _checkProgressTitle.Foreground = brush; _checkProgressTitle.Text = title;
        _checkProgressDescription.Text = description;
        System.Windows.Automation.AutomationProperties.SetName(_checkProgressFrame, title + ". " + description);
    }
    private void FitConnectionCard()
    {
        if (_connectionLayer.Children.Count == 0 || _connectionLayer.Children[0] is not Border card) return;
        bool loading = _connectionFailure is null;
        card.Width = Math.Min(loading ? (_compact ? 560 : 600) : (_compact ? 470 : 530), Math.Max(0, _connectionLayer.ActualWidth - 32));
        card.MaxHeight = Math.Max(0, _connectionLayer.ActualHeight - 16);
        card.MinHeight = loading ? Math.Min(210, card.MaxHeight) : 0;
    }
    private void ShowConnectionOverlay(Exception? problem = null, bool search = false)
    {
        CloseAdditionalCategories();
        CloseInfo();
        _tabs.Visibility = problem is null || _search.Text.Trim().Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        _connectionFailure = problem; _retrySearch = search; _connectionLayer.Children.Clear();
        _checkProgressFrame = null; _checkProgressDot = null; _checkProgressTitle = _checkProgressDescription = null;
        if (problem is null && !search) ApplyCheckStatusAppearance();
        if (problem is not null && !IsWorking) { InterruptCheckStatus(problem is not TimeoutException); ApplyCheckStatusAppearance(); }
        _connectionLayer.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Palette.Colors[4]));
        var card = new Border { Margin = new(8), Padding = new(problem is null ? (_compact ? 12 : 20) : (_compact ? 14 : 20)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new(10), BorderThickness = new(1.2), MaxHeight = Math.Max(150, ActualHeight - 145) };
        card.SetResourceReference(Border.BackgroundProperty, "InputBrush"); card.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        var layout = new Grid(); layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new()); layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var titleRow = new Grid { Margin = new(0, 0, 0, _compact ? 5 : 8) }; titleRow.ColumnDefinitions.Add(new() { Width = new GridLength(44) }); titleRow.ColumnDefinitions.Add(new());
        if (problem is null)
        {
            var circle = new Grid { Width = 28, Height = 28 };
            var ring = new Ellipse { StrokeThickness = 3, Opacity = .2 }; ring.SetResourceReference(Shape.StrokeProperty, "AccentBrush"); circle.Children.Add(ring);
            var arc = new Path { Data = Geometry.Parse("M 14,2 A 12,12 0 0 1 26,14"), StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            arc.SetResourceReference(Shape.StrokeProperty, "AccentBrush"); var rotation = new RotateTransform(0, 14, 14); arc.RenderTransform = rotation;
            if (SystemParameters.ClientAreaAnimation) rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
            circle.Children.Add(arc); titleRow.Children.Add(circle);
        }
        else
        {
            var icon = new Path { Data = Geometry.Parse("M 2,10 C 9,3 23,3 30,10 M 7,16 C 12,11 20,11 25,16 M 12,22 C 15,19 18,19 21,22 M 16,27 L 16.1,27 M 3,3 L 30,30"),
                Width = 31, Height = 31, Stretch = Stretch.Uniform, StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(Shape.StrokeProperty, "WarningBrush"); titleRow.Children.Add(icon);
        }
        var title = Text(problem is null ? (search ? L("Programme online suchen …", "Searching programs online …") : L("Programme und Updates werden geprüft …", "Checking programs and updates …"))
            : problem is OnlineConnectionException { Lost: true } ? L("Internetverbindung verloren", "Internet connection lost")
            : problem is OnlineConnectionException { Offline: true } ? L("Keine Internetverbindung", "No internet connection")
            : L("Onlineprüfung unterbrochen", "Online check interrupted"), _compact ? 18 : 20);
        title.FontWeight = FontWeights.SemiBold;
        var heading = new StackPanel { Margin = new(0, 0, problem is null ? 0 : 38, 0) }; heading.Children.Add(title);
        if (problem is not null) { var subtitle = Text(L("Onlinefunktionen pausiert", "Online functions paused"), 11, "WarningBrush"); subtitle.Margin = new(0, 4, 0, 0); heading.Children.Add(subtitle); }
        Grid.SetColumn(heading, 1); titleRow.Children.Add(heading); layout.Children.Add(titleRow);
        var stripes = new Path { Data = Geometry.Parse("M 65,0 L 67,0 L 8,54 L 6,54 Z M 90,0 L 92,0 L 33,54 L 31,54 Z M 94,0 L 110,0 L 51,54 L 35,54 Z"),
            Width = 55, Height = 35, Stretch = Stretch.Fill, Opacity = .14, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        stripes.SetResourceReference(Shape.FillProperty, "AccentBrush"); layout.Children.Insert(0, stripes);
        var body = new StackPanel();
        body.Children.Add(Text(problem is null ? (search ? L("Suche nach „" + _search.Text.Trim() + "“ in der Onlinequelle.", "Searching the online source for “" + _search.Text.Trim() + "”.") : L("Wir scannen installierte Programme und prüfen verfügbare Updates.", "We scan installed programs and check for available updates.")) : UiText.Error(problem, _english), problem is null && !search && _compact ? 11 : 12));
        body.Children.Add(new TextBlock { Text = problem is null ? (search ? L("Ergebnisse und passende Symbole werden anschließend geladen.", "Results and matching icons will then be loaded.") : L("Die Ergebnisse erscheinen anschließend unten in der Programmliste. Während der Prüfung bleibt die Auswahl gesperrt.", "Results will appear below in the program list. Selection stays locked while the check runs."))
            : L("Auswahl und vorhandene Ergebnisse bleiben erhalten. Lokal kannst du weiterarbeiten.", "Your selection and existing results are preserved. You can continue offline."),
            TextWrapping = TextWrapping.Wrap, FontSize = problem is null && !search && _compact ? 10 : 11,
            Margin = new(0, 6, 0, 0), Foreground = (Brush)FindResource("SubtleTextBrush") });
        if (problem is null && !search)
        {
            _checkProgressFrame = new Border { Name = "ProgramCheckProgressFrame", Padding = new(8, 6, 8, 6), Margin = new(0, _compact ? 7 : 12, 0, 0), CornerRadius = new(7), BorderThickness = new(1) };
            _checkProgressFrame.SetResourceReference(Border.BackgroundProperty, "StandardActionButtonBrush");
            var progressRow = new Grid(); progressRow.ColumnDefinitions.Add(new() { Width = new GridLength(19) }); progressRow.ColumnDefinitions.Add(new());
            _checkProgressDot = new Ellipse { Name = "ProgramCheckProgressDot", Width = 10, Height = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(_checkProgressDot, 0); progressRow.Children.Add(_checkProgressDot);
            var progressLabels = new StackPanel();
            _checkProgressTitle = Text("", 12); _checkProgressTitle.Name = "ProgramCheckProgressTitle"; _checkProgressTitle.FontWeight = FontWeights.SemiBold;
            _checkProgressDescription = Text("", 10, "SubtleTextBrush"); _checkProgressDescription.Name = "ProgramCheckProgressDescription";
            progressLabels.Children.Add(_checkProgressTitle); progressLabels.Children.Add(_checkProgressDescription);
            Grid.SetColumn(progressLabels, 1); progressRow.Children.Add(progressLabels); _checkProgressFrame.Child = progressRow; body.Children.Add(_checkProgressFrame);

            var legend = new StackPanel { Name = "ProgramCheckProgressLegend", Orientation = Orientation.Horizontal, Margin = new(0, _compact ? 4 : 9, 0, 0) };
            void AddLegend(string color, string label)
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 0, _compact ? 8 : 12, 0), VerticalAlignment = VerticalAlignment.Center };
                item.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)), VerticalAlignment = VerticalAlignment.Center });
                var caption = Text(label, 9, "SubtleTextBrush"); caption.Margin = new(4, 0, 0, 0);
                item.Children.Add(caption); legend.Children.Add(item);
            }
            AddLegend("#FF5A6B", L("Läuft", "Running")); AddLegend("#FFB347", L("Teilergebnisse", "Partial results")); AddLegend("#00D7A0", L("Abgeschlossen", "Complete"));
            body.Children.Add(legend);
        }
        if (problem is not null)
        {
            var notice = new Border { Padding = new(8, 5, 8, 5), Margin = new(0, 8, 0, 0), CornerRadius = new(6), BorderThickness = new(1) };
            notice.SetResourceReference(Border.BackgroundProperty, "StandardActionButtonBrush"); notice.SetResourceReference(Border.BorderBrushProperty, "InputBorderBrush");
            notice.Child = Text(L("Kein neuer Download und keine Onlineinstallation werden gestartet.", "No new download or online installation will start."), 11); body.Children.Add(notice);
        }
        var bodyFrame = new Border { Child = body, Padding = new(problem is null ? (_compact ? 6 : 10) : 0), CornerRadius = new(6), BorderThickness = new(problem is null ? 1 : 0) };
        bodyFrame.SetResourceReference(Border.BackgroundProperty, "StandardActionButtonBrush"); bodyFrame.SetResourceReference(Border.BorderBrushProperty, "InputBorderBrush");
        var scroll = new ScrollViewer { Name = "ProgramCheckProgressScroll", Content = bodyFrame, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new(0, 3, 0, _compact ? 4 : 8) }; Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
        var actions = new Grid(); actions.ColumnDefinitions.Add(new()); actions.ColumnDefinitions.Add(new() { Width = new GridLength(8) }); actions.ColumnDefinitions.Add(new());
        var local = Action(L("Offline weiter", "Continue offline"), 34);
        local.Click += (_, _) => { _inspectionCancellation?.Cancel(); CancelSearch(); _searchStatus = L("Onlineprüfung nicht vollständig · lokale Daten", "Online check incomplete · local data"); HideConnectionOverlay(); RefreshAvailability(); };
        if (problem is null)
        {
            var caption = search ? L("Suche beenden", "Stop search") : L("Prüfung beenden", "Stop checking");
            local.Content = new TextBlock { Text = caption, Foreground = System.Windows.Media.Brushes.White };
            if (Window.GetWindow(this) is { } owner) UnifiedDialogAppearance.Button(owner, local, DialogButtonKind.Danger);
            local.Width = 220; local.HorizontalAlignment = HorizontalAlignment.Center;
            System.Windows.Automation.AutomationProperties.SetName(local, caption);
        }
        if (problem is not null)
        {
            var retry = Action(L("↻ Erneut prüfen", "↻ Check again"), 34);
            retry.Click += async (_, _) => { HideConnectionOverlay(); if (_retrySearch) await SearchOnlineAsync(); else await RefreshAsync(); };
            actions.Children.Add(retry); Grid.SetColumn(local, 2);
        }
        else Grid.SetColumnSpan(local, 3);
        actions.Children.Add(local); Grid.SetRow(actions, 2); layout.Children.Add(actions); card.Child = layout; _connectionLayer.Children.Add(card);
        _connectionLayer.Visibility = Visibility.Visible; FitConnectionCard(); UpdateCheckProgressOverlay(); NotifyOverlayChanged();
    }
}
