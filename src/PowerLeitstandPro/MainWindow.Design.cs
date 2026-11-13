using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Control = System.Windows.Controls.Control;
using Binding = System.Windows.Data.Binding;
using Pen = System.Windows.Media.Pen;

namespace PowerControlProWpf;

public partial class MainWindow
{
    private readonly Dictionary<string, object> _referencePaletteDefaults = new();
    private readonly Dictionary<System.Windows.Shapes.Path, (Brush? Fill, Brush? Stroke)> _referenceStripeDefaults = new();
    private readonly Dictionary<Border, (AdornerLayer Layer, NeonFrameAdorner Frame)> _neonFrames = new();
    private readonly List<DropShadowEffect> _animatedNeonEffects = new();
    private readonly Dictionary<Button, DropShadowEffect> _neonButtons = new();
    private readonly Dictionary<Button, Border> _neonHoverSurfaces = new();
    private AnimationClock? _neonClock;
    private Color? _neonColor;
    private Border? _neonOuterOutline;
    private System.Windows.Threading.DispatcherOperation? _neonRefresh;
    private bool _isWindowDragging;

    private void BeginWindowDragPresentation()
    {
        if (_isWindowDragging) return;
        _isWindowDragging = true;
        _placementSaveTimer.Stop();
        foreach (var bar in FindVisualChildren<AnimatedProgressBar>(Root)) bar.IsAnimationSuspended = true;
        _neonClock?.Controller?.Pause();
    }

    private void EndWindowDragPresentation()
    {
        if (!_isWindowDragging) return;
        _isWindowDragging = false;
        foreach (var bar in FindVisualChildren<AnimatedProgressBar>(Root)) bar.IsAnimationSuspended = false;
        _neonClock?.Controller?.Resume();
        if (!IsLoaded) return;
        UpdateLiveDiagnosticsLabels();
        if (_diagnosticsKind is not null) RefreshDiagnosticsHover();
        if (!SkipSavedPlacementForTests && WindowState == WindowState.Normal)
            _placementSaveTimer.Start();
    }

    private void ApplyPageNavigationTheme(string theme)
    {
        var classic = theme == "Classic";
        foreach (var button in new[] { PagePrevious, PageNext })
        {
            button.Style = (Style)Resources[classic ? "ClassicPageArrowButton" : "PageArrowButton"];
            button.Margin = classic ? button == PagePrevious ? new Thickness(0, 0, 2, 0) : new Thickness(2, 0, 0, 0) : new Thickness(0);
            if (classic) button.ClearValue(Control.BackgroundProperty);
            else button.SetResourceReference(Control.BackgroundProperty, button == PagePrevious ? "InputBrush" : "ButtonBrush");
            if (button.Content is System.Windows.Shapes.Path arrow) arrow.StrokeThickness = classic ? 2.8 : 2.2;
        }
    }

    private void ApplyClassicCancelTheme(string theme)
    {
        if (!IsLoaded) return;
        UnifiedDialogAppearance.Resources(this, ThemePalette.For(theme));
        UnifiedDialogAppearance.Button(this, RepairCancelButton, DialogButtonKind.Danger);
        ClassicCancelAppearance.Apply(AbortTimerButton, theme == "Classic", "AbortBorder");
    }

    private void UpdateOptionsViewport()
    {
        if (!IsLoaded || SettingsShell.Visibility != Visibility.Visible || SettingsShell.ActualHeight <= 0) return;
        if (SettingsShell.RenderTransform is not TranslateTransform translation)
            SettingsShell.RenderTransform = translation = new TranslateTransform();
        var bounds = SettingsShell.TransformToAncestor(this).TransformBounds(new Rect(SettingsShell.RenderSize));
        var top = bounds.Top - translation.Y;
        var bottom = bounds.Bottom - translation.Y;
        var offset = top < 8 ? 8 - top : bottom > ActualHeight - 8 ? ActualHeight - 8 - bottom : 0;
        if (Math.Abs(translation.Y - offset) > .1) translation.Y = offset;
    }

    private void ApplyReferencePalette(string name, ThemePalette palette)
    {
        var keys = new[] { "StandardReferenceAppBrush", "StandardReferenceCardBrush", "StandardReferenceStatusBrush",
            "StandardActionButtonBrush", "StandardActionDangerBrush", "StandardActionDangerBorderBrush", "StandardReferenceCornerBrush" };
        foreach (var key in keys)
            if (!_referencePaletteDefaults.ContainsKey(key)) _referencePaletteDefaults[key] = Resources[key];
        foreach (var decoration in _referenceCardDecorations.Values)
            foreach (var path in decoration.Stripes.Children.OfType<System.Windows.Shapes.Path>())
                if (!_referenceStripeDefaults.ContainsKey(path)) _referenceStripeDefaults[path] = (path.Fill, path.Stroke);

        if (name == "Standard")
        {
            foreach (var key in keys) Resources[key] = _referencePaletteDefaults[key];
            foreach (var (path, brushes) in _referenceStripeDefaults) { path.Fill = brushes.Fill; path.Stroke = brushes.Stroke; }
        }
        else
        {
            Color Read(string key) => ((SolidColorBrush)Resources[key]).Color;
            var app = Read("AppBrush"); var card = Read("CardBrush"); var input = Read("InputBrush");
            var button = Read("ButtonBrush"); var accent = Read("AccentBrush"); var danger = Read("DangerBrush");
            Resources["StandardReferenceAppBrush"] = new LinearGradientBrush(app, BlendColor(app, card, .25), 35);
            Resources["StandardReferenceCardBrush"] = new LinearGradientBrush(BlendColor(card, button, .36), BlendColor(card, input, .56), 0);
            Resources["StandardReferenceStatusBrush"] = new LinearGradientBrush(BlendColor(card, input, .4), input, 0);
            Resources["StandardActionButtonBrush"] = new LinearGradientBrush(button, BlendColor(button, input, .12), 90);
            Resources["StandardActionDangerBrush"] = new LinearGradientBrush(danger, BlendColor(danger, input, .12), 90);
            Resources["StandardActionDangerBorderBrush"] = Resources["DangerBorderBrush"];
            Resources["StandardReferenceCornerBrush"] = new LinearGradientBrush(accent, BlendColor(accent, card, .3), 90);
            foreach (var (path, original) in _referenceStripeDefaults)
            {
                if (original.Fill is not null) path.Fill = new SolidColorBrush(BlendColor(card, accent, .3));
                if (original.Stroke is not null) path.Stroke = new SolidColorBrush(BlendColor(card, accent, .58));
            }
        }
        foreach (var border in new[] { StandardStatusLogBar, CompactStandardTimePicker, StandardTimePicker })
            border.SetResourceReference(Border.BackgroundProperty, "StandardReferenceStatusBrush");
        ImmediateReferenceCorner.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "StandardReferenceCornerBrush");
        TimerReferenceCorner.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "StandardReferenceCornerBrush");
        foreach (var action in new[] { RestartButton, LogoutButton, LockButton, TimerLock, TimerRestart })
            action.SetResourceReference(Control.ForegroundProperty, "DefaultActionTextBrush");
        foreach (var step in new[] { CompactTimerMinusMinute, CompactTimerPlusMinute, CompactTimerMinusHour, CompactTimerPlusHour })
            step.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        var accentColor = ((SolidColorBrush)Resources["AccentBrush"]).Color;
        Resources["TimerStepHoverBrush"] = name == "Standard"
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3B1B709F"))
            : new SolidColorBrush(Color.FromArgb(59, accentColor.R, accentColor.G, accentColor.B));
        Resources["TimerStepPressedBrush"] = name == "Standard"
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#692A83AD"))
            : new SolidColorBrush(Color.FromArgb(105, accentColor.R, accentColor.G, accentColor.B));
        ImmediateReferenceTopGap.Fill = TimerReferenceTopGap.Fill = name == "Standard"
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#012242"))
            : (Brush)Resources["StandardReferenceCardBrush"];
    }

    internal static DoubleAnimationUsingKeyFrames NeonPulse(double litSeconds, double darkSeconds)
    {
        const double ignition = 1.2;
        var pulse = new DoubleAnimationUsingKeyFrames();
        void Add(double seconds, double strength) => pulse.KeyFrames.Add(new SplineDoubleKeyFrame(
            strength, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds)), new KeySpline(.42, 0, .58, 1)));
        Add(0, 0);
        Add(.14, .34);
        Add(.29, .08);
        Add(.64, 1);
        Add(ignition, .43);
        for (double second = 1.5; second < litSeconds; second += 1.5)
            Add(ignition + second, second % 3 == 0 ? .43 : .52);
        Add(ignition + litSeconds, .43);
        Add(ignition + litSeconds + .24, 0);
        Add(ignition + litSeconds + .24 + darkSeconds, 0);
        return pulse;
    }

    private void StartNeonCycle()
    {
        var clock = (AnimationClock)NeonPulse(Random.Shared.Next(5, 31),
            .55 + Random.Shared.NextDouble() * .65).CreateClock(true);
        clock.Completed += (_, _) => { if (ReferenceEquals(_neonClock, clock)) StartNeonCycle(); };
        var previous = _neonClock;
        _neonClock = clock;
        foreach (var (_, frame) in _neonFrames.Values)
            frame.ApplyAnimationClock(NeonFrameAdorner.StrengthProperty, clock);
        foreach (var effect in _animatedNeonEffects)
            effect.ApplyAnimationClock(DropShadowEffect.OpacityProperty, clock);
        foreach (var (button, effect) in _neonButtons)
            if (button.IsEnabled) effect.ApplyAnimationClock(DropShadowEffect.OpacityProperty, clock);
        previous?.Controller?.Remove();
        if (_isWindowDragging) clock.Controller?.Pause();
    }

    private void ConfigureNeonPresentation(ThemePalette palette)
    {
        if (!palette.IsNeon || !IsLoaded) { StopNeonPresentation(); return; }
        var color = (Color)ColorConverter.ConvertFromString(palette.Glow!);
        if (_neonColor != color)
        {
            StopNeonPresentation();
            _neonColor = color;
            var iconGlow = new DropShadowEffect { Color = color, ShadowDepth = 0, BlurRadius = 10, Opacity = .43 };
            var surfaceGlow = new DropShadowEffect { Color = color, ShadowDepth = 0, BlurRadius = 15, Opacity = .43 };
            _animatedNeonEffects.Add(iconGlow);
            _animatedNeonEffects.Add(surfaceGlow);
            if (SystemParameters.ClientAreaAnimation) StartNeonCycle();
            Resources["NeonIconGlow"] = iconGlow;
            Resources["NeonGlow"] = surfaceGlow;
        }
        QueueNeonRefresh();
    }

    private void QueueNeonRefresh()
    {
        if (_neonColor is not Color color || _neonRefresh is not null) return;
        _neonRefresh = Dispatcher.InvokeAsync(() =>
        {
            try
            {
                UpdateLayout();
                RefreshNeonSurfaces(color);
            }
            finally { _neonRefresh = null; }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void RefreshNeonSurfaces(Color color)
    {
        _neonOuterOutline = Template.FindName("OuterWindowOutline", this) as Border;
        var cards = new[] { _neonOuterOutline, ImmediateCardSurface, TimerCardSurface, UserCardSurface,
                RepairCardSurface, StandardStatusLogBar }.OfType<Border>().Concat(_updatesPage?.CardSurfaces ?? []).ToHashSet();
        foreach (var card in _neonFrames.Keys.Where(card => !cards.Contains(card)
                     || _neonFrames[card].Layer != AdornerLayer.GetAdornerLayer(card)).ToArray())
            RemoveNeonFrame(card);
        foreach (var card in cards)
        {
            if (_neonFrames.ContainsKey(card)) continue;
            var layer = AdornerLayer.GetAdornerLayer(card);
            if (layer is null) continue;
            var frame = new NeonFrameAdorner(card, color);
            frame.SetBinding(VisibilityProperty, new Binding(nameof(IsVisible)) { Source = card, Converter = new BooleanToVisibilityConverter() });
            layer.Add(frame);
            _neonFrames.Add(card, (layer, frame));
            if (_neonClock is not null) frame.ApplyAnimationClock(NeonFrameAdorner.StrengthProperty, _neonClock);
        }
        var actions = FindVisualChildren<Button>(Root).Concat(RepairNavigationButtons).Where(IsNeonButton).ToHashSet();
        foreach (var button in _neonButtons.Keys.Where(button => !actions.Contains(button)).ToArray())
            RemoveNeonButton(button);
        foreach (var action in actions)
        {
            if (_neonButtons.ContainsKey(action)) continue;
            var actionColor = action == ShutdownButton || action == TimerShutdown
                ? ((SolidColorBrush)Resources["DangerBorderBrush"]).Color : color;
            var effect = new DropShadowEffect { Color = actionColor, ShadowDepth = 0, BlurRadius = 10 };
            _neonButtons[action] = effect;
            action.Effect = effect;
            action.MouseEnter += NeonButtonHover;
            action.MouseLeave += NeonButtonHover;
            action.IsEnabledChanged += NeonButtonEnabledChanged;
            UpdateNeonButton(action, effect);
        }
        UpdateNeonOverlayVisibility();
    }

    private static bool IsNeonButton(Button button) =>
        !button.Name.Contains("Close", StringComparison.OrdinalIgnoreCase) &&
        !button.Name.Contains("Cancel", StringComparison.OrdinalIgnoreCase) &&
        !button.Name.Contains("Abort", StringComparison.OrdinalIgnoreCase) &&
        button.Content is not ("×" or "✕" or "✖");

    private void UpdateNeonOverlayVisibility()
    {
        var show = SettingsPanel.Visibility != Visibility.Visible && _updatesPage?.ProgramView?.HasOverlay != true
            && _updatesPage?.HasStartupOverlay != true && _updatesPage?.HasInstallOverlay != true;
        foreach (var (card, (_, frame)) in _neonFrames)
            frame.Opacity = show || ReferenceEquals(card, _neonOuterOutline) ? 1 : 0;
    }

    private void NeonButtonHover(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is Button button && _neonButtons.TryGetValue(button, out var effect))
            UpdateNeonButton(button, effect, e.RoutedEvent == Mouse.MouseEnterEvent);
    }

    private void NeonButtonEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is Button button && _neonButtons.TryGetValue(button, out var effect)) UpdateNeonButton(button, effect);
    }

    private void UpdateNeonButton(Button button, DropShadowEffect effect, bool? hovered = null)
    {
        var activeHover = button.IsEnabled && (hovered ?? button.IsMouseOver);
        effect.ApplyAnimationClock(DropShadowEffect.OpacityProperty, button.IsEnabled ? _neonClock : null);
        effect.Opacity = !button.IsEnabled ? 0 : activeHover ? .74 : .43;
        effect.BlurRadius = activeHover ? 16 : 7;
        ClearNeonHoverSurface(button);
        if (activeHover)
        {
            button.ApplyTemplate();
            var surface = new[] { "ActionBorder", "StepSurface", "ButtonBorder", "NoticeSurface", "ArrowSurface", "Surface" }
                .Select(name => button.Template?.FindName(name, button)).OfType<Border>().FirstOrDefault();
            if (surface is not null)
            {
                var danger = button == ShutdownButton || button == TimerShutdown;
                var accent = ((SolidColorBrush)Resources[danger ? "DangerBorderBrush" : "AccentBrush"]).Color;
                surface.Background = danger
                    ? new SolidColorBrush(BlendColor(((SolidColorBrush)Resources["DangerBrush"]).Color, accent, .3))
                    : (Brush)Resources["HoverBrush"];
                surface.BorderBrush = new SolidColorBrush(BlendColor(accent, Colors.White, .65));
                surface.BorderThickness = new Thickness(2);
                surface.Opacity = 1;
                _neonHoverSurfaces[button] = surface;
            }
        }
    }

    private void ClearNeonHoverSurface(Button button)
    {
        if (!_neonHoverSurfaces.Remove(button, out var surface)) return;
        // Restore template bindings/triggers, including palette changes and disabled states.
        surface.ClearValue(Border.BackgroundProperty);
        surface.ClearValue(Border.BorderBrushProperty);
        surface.ClearValue(Border.BorderThicknessProperty);
        surface.ClearValue(OpacityProperty);
    }

    private void StopNeonPresentation()
    {
        _neonRefresh?.Abort();
        _neonRefresh = null;
        _neonColor = null;
        _neonOuterOutline = null;
        var clock = _neonClock;
        _neonClock = null;
        clock?.Controller?.Remove();
        foreach (var card in _neonFrames.Keys.ToArray()) RemoveNeonFrame(card);
        foreach (var effect in _animatedNeonEffects)
            if (!effect.IsFrozen) effect.BeginAnimation(DropShadowEffect.OpacityProperty, null);
        _animatedNeonEffects.Clear();
        foreach (var button in _neonButtons.Keys.ToArray()) RemoveNeonButton(button);
    }

    private void RemoveNeonFrame(Border card)
    {
        var (layer, frame) = _neonFrames[card];
        _neonFrames.Remove(card);
        frame.BeginAnimation(NeonFrameAdorner.StrengthProperty, null);
        BindingOperations.ClearBinding(frame, VisibilityProperty);
        layer.Remove(frame);
    }

    private void RemoveNeonButton(Button button)
    {
        var effect = _neonButtons[button];
        _neonButtons.Remove(button);
        ClearNeonHoverSurface(button);
        effect.BeginAnimation(DropShadowEffect.OpacityProperty, null);
        if (ReferenceEquals(button.Effect, effect)) button.ClearValue(EffectProperty);
        button.MouseEnter -= NeonButtonHover;
        button.MouseLeave -= NeonButtonHover;
        button.IsEnabledChanged -= NeonButtonEnabledChanged;
    }
}

// Only the outline is drawn here: text and content never pass through a blur filter.
internal sealed class NeonFrameAdorner : Adorner
{
    internal static readonly DependencyProperty StrengthProperty = DependencyProperty.Register(
        nameof(Strength), typeof(double), typeof(NeonFrameAdorner),
        new FrameworkPropertyMetadata(.45, (sender, args) =>
            ((NeonFrameAdorner)sender)._outlineDrawing.Opacity = (double)args.NewValue));
    private readonly DrawingGroup _outlineDrawing = new() { Opacity = .45 };
    private readonly Pen[] _haloPens;
    private readonly Pen _corePen;
    public double Strength { get => (double)GetValue(StrengthProperty); set => SetValue(StrengthProperty, value); }

    internal NeonFrameAdorner(Border card, Color color) : base(card)
    {
        _haloPens = new[] { (21.0, .025), (16.0, .045), (12.0, .075), (8.0, .12), (5.0, .21), (2.8, .46) }
            .Select(layer => new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * layer.Item2), color.R, color.G, color.B)), layer.Item1)).ToArray();
        var core = Color.FromRgb((byte)((color.R + 510) / 3), (byte)((color.G + 510) / 3), (byte)((color.B + 510) / 3));
        _corePen = new Pen(new SolidColorBrush(Color.FromArgb(235, core.R, core.G, core.B)), 1.25);
        foreach (var pen in _haloPens.Append(_corePen)) pen.Freeze();
        IsHitTestVisible = false;
        IsClipEnabled = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = AdornedElement.RenderSize;
        if (size.Width <= 1 || size.Height <= 1) return;
        var rect = new Rect(.5, .5, size.Width - 1, size.Height - 1);
        var radius = ((Border)AdornedElement).CornerRadius.TopLeft;
        // Retain the outline commands; pulse changes only the group's opacity.
        // Rebuild geometry only when layout invalidates this adorner's render size.
        using (var outline = _outlineDrawing.Open())
        {
            foreach (var pen in _haloPens) outline.DrawRoundedRectangle(null, pen, rect, radius, radius);
            outline.DrawRoundedRectangle(null, _corePen, rect, radius, radius);
        }
        dc.DrawDrawing(_outlineDrawing);
    }
}
