using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using ToolTip = System.Windows.Controls.ToolTip;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace PowerControlProWpf;

internal static class AppToolTips
{
    private static bool _initialized;
    private sealed class HelpTip(FrameworkElement target, Window? owner, string text) : ToolTip
    {
        internal string PlainText { get; } = text;
        private bool _listening, _closeQueued;
        private long _opening;
        private bool Connected => target.IsLoaded && target.IsVisible && PresentationSource.FromVisual(target) is not null
            && (owner is null || owner.IsLoaded && owner.IsVisible && PresentationSource.FromVisual(owner) is not null
                && ReferenceEquals(Window.GetWindow(target), owner));

        protected override void OnOpened(RoutedEventArgs e)
        {
            _opening++; _closeQueued = false;
            Listen(); base.OnOpened(e);
            if (!Connected) Dismiss();
        }
        protected override void OnClosed(RoutedEventArgs e)
        {
            _opening++; _closeQueued = false; StopListening(); base.OnClosed(e);
        }
        private void Listen()
        {
            if (_listening) return; _listening = true;
            target.Unloaded += LostTarget;
            target.IsVisibleChanged += VisibilityChanged;
            PresentationSource.AddSourceChangedHandler(target, SourceChanged);
            if (owner is not null) { owner.Closed += OwnerClosed; owner.IsVisibleChanged += VisibilityChanged; }
        }
        private void StopListening()
        {
            if (!_listening) return; _listening = false;
            target.Unloaded -= LostTarget;
            target.IsVisibleChanged -= VisibilityChanged;
            PresentationSource.RemoveSourceChangedHandler(target, SourceChanged);
            if (owner is not null) { owner.Closed -= OwnerClosed; owner.IsVisibleChanged -= VisibilityChanged; }
        }
        private void LostTarget(object sender, RoutedEventArgs e) => Dismiss();
        private void OwnerClosed(object? sender, EventArgs e) => Dismiss();
        private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) { if (!Connected) Dismiss(); }
        private void SourceChanged(object sender, SourceChangedEventArgs e) { if (!Connected) Dismiss(); }
        private void Dismiss()
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) { StopListening(); return; }
            if (!IsOpen || _closeQueued) return;
            _closeQueued = true; var opening = _opening;
            // Popup may call us from its own resize/layout callback. Close after that callback unwinds.
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                if (opening != _opening) return;
                _closeQueued = false;
                if (IsOpen) SetCurrentValue(IsOpenProperty, false);
            }));
        }
        internal CustomPopupPlacement[] Position(Size popup, Size size)
        {
            CustomPopupPlacement[] Invalid()
            { Dismiss(); return [new(new Point(0, 0), PopupPrimaryAxis.Vertical)]; }
            if (!Connected || !double.IsFinite(popup.Width) || !double.IsFinite(popup.Height)
                || !double.IsFinite(size.Width) || !double.IsFinite(size.Height)) return Invalid();
            Point anchor;
            try { anchor = owner is null ? new Point(0, popup.Height + 16) : owner.PointFromScreen(target.PointToScreen(new Point())); }
            catch (InvalidOperationException) { return Invalid(); }
            var x = size.Width - popup.Width; var y = -popup.Height - 8;
            if (owner is not null)
            {
                var maxX = Math.Max(8 - anchor.X, owner.ActualWidth - anchor.X - popup.Width - 8);
                x = Math.Clamp(x, 8 - anchor.X, maxX);
                y = Math.Clamp(y, 8 - anchor.Y, Math.Max(8 - anchor.Y, owner.ActualHeight - anchor.Y - popup.Height - 8));
            }
            return [new(new Point(x, y), PopupPrimaryAxis.Vertical)];
        }
    }
    internal static void Initialize()
    {
        if (_initialized) return; _initialized = true;
        EventManager.RegisterClassHandler(typeof(FrameworkElement), ToolTipService.ToolTipOpeningEvent, new ToolTipEventHandler(Opening), true);
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Button), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => ToolTipService.SetShowOnDisabled((DependencyObject)sender, true)), true);
    }
    private static void Opening(object sender, ToolTipEventArgs e)
    {
        if (e.Handled || sender is not FrameworkElement element) return;
        var text = element.ToolTip switch { string value => value, HelpTip tip => tip.PlainText, _ => null };
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!element.IsLoaded || !element.IsVisible || PresentationSource.FromVisual(element) is null) { e.Handled = true; return; }
        // Keep ToolTipService responsible for hover timing, auto-close and focus loss.
        // An existing non-null tooltip can be replaced during its opening event.
        element.SetCurrentValue(FrameworkElement.ToolTipProperty, Create(element, text));
    }
    internal static ToolTip Create(FrameworkElement target, string text)
    {
        var owner = Window.GetWindow(target);
        var main = owner as MainWindow ?? owner?.Owner as MainWindow ?? System.Windows.Application.Current?.MainWindow as MainWindow;
        var palette = owner is UpdateManagerWindow manager ? manager.CurrentPalette : ThemePalette.For(main?.CurrentThemeName ?? "Standard");
        Brush Read(string key, int index) => owner?.TryFindResource(key) as Brush ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Colors[index]));
        var body = new TextBlock { Text = text, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = Read("TextBrush", 5), LineHeight = 16 };
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = Math.Min(220, Math.Max(48, (owner?.ActualHeight ?? 310) - 64)) };
        var frame = new Border { Child = scroll, Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            Background = owner?.TryFindResource("StandardReferenceStatusBrush") as Brush ?? owner?.TryFindResource("DialogSurface") as Brush ?? Read("InputBrush", 4), BorderBrush = Read("AccentBrush", 7) };
        var tip = new HelpTip(target, owner, text) { Content = frame, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            MaxWidth = Math.Min(340, Math.Max(160, (owner?.ActualWidth ?? 500) - 24)), PlacementTarget = target, Placement = PlacementMode.Custom };
        tip.CustomPopupPlacementCallback = (popup, size, _) => tip.Position(popup, size);
        return tip;
    }
}
