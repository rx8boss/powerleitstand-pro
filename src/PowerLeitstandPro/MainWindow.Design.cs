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
    private readonly List<(AdornerLayer Layer, NeonFrameAdorner Frame)> _neonFrames = new();
    private readonly List<DropShadowEffect> _animatedNeonEffects = new();
    private readonly Dictionary<Button, DropShadowEffect> _neonButtons = new();
    private readonly Dictionary<Button, Border> _neonHoverSurfaces = new();
    private readonly Dictionary<(DependencyObject Target, DependencyProperty Property), AnimationClock> _neonPulseClocks = new();
    private bool _isWindowDragging;

    private void StartNeonPulse(DependencyObject target, DependencyProperty property, double low, double high)
    {
        StopNeonPulse(target, property);
        var clock = (AnimationClock)NeonPulse(low, high).CreateClock(true);
        if (target is Animatable animation) animation.ApplyAnimationClock(property, clock);
        else if (target is UIElement element) element.ApplyAnimationClock(property, clock);
        _neonPulseClocks[(target, property)] = clock;
        if (_isWindowDragging) clock.Controller?.Pause();
    }

    private void StopNeonPulse(DependencyObject target, DependencyProperty property)
    {
        if (_neonPulseClocks.Remove((target, property), out var clock)) clock.Controller?.Remove();
        if (target is Animatable animation) animation.BeginAnimation(property, null);
        else if (target is UIElement element) element.BeginAnimation(property, null);
    }

    private void BeginWindowDragPresentation()
    {
        if (_isWindowDragging) return;
        _isWindowDragging = true;
        _placementSaveTimer.Stop();
        foreach (var clock in _neonPulseClocks.Values) clock.Controller?.Pause();
    }

    private void EndWindowDragPresentation()
    {
        if (!_isWindowDragging) return;
        _isWindowDragging = false;
        foreach (var clock in _neonPulseClocks.Values) clock.Controller?.Resume();
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
        CrossButtonAppearance.Prepare(RepairCancelButton);
        ClassicCancelAppearance.Apply(RepairCancelButton, theme == "Classic", "CrossFrame");
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

    private static DoubleAnimationUsingKeyFrames NeonPulse(double low, double high)
    {
        var pulse = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(2), RepeatBehavior = RepeatBehavior.Forever };
        foreach (var (seconds, value) in new[] { (0.0, .15), (.1, .4), (.2, .78), (.3, 1.0), (.4, .64), (.5, .92),
                     (.6, .68), (.7, .5), (.8, .32), (.9, .2), (1.0, .12), (3.0, .12) })
            pulse.KeyFrames.Add(new LinearDoubleKeyFrame(low + (high - low) * value, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds / 1.5))));
        return pulse;
    }

    private void ConfigureNeonPresentation(ThemePalette palette)
    {
        StopNeonPresentation();
        if (!palette.IsNeon || !IsLoaded) return;
        var color = (Color)ColorConverter.ConvertFromString(palette.Glow!);
        foreach (var card in new[] { ImmediateCardSurface, TimerCardSurface, UserCardSurface, RepairCardSurface, StandardStatusLogBar })
        {
            var layer = AdornerLayer.GetAdornerLayer(card);
            if (layer is null) continue;
            var frame = new NeonFrameAdorner(card, color);
            frame.SetBinding(VisibilityProperty, new Binding(nameof(IsVisible)) { Source = card, Converter = new BooleanToVisibilityConverter() });
            layer.Add(frame);
            _neonFrames.Add((layer, frame));
            if (SystemParameters.ClientAreaAnimation)
                StartNeonPulse(frame, NeonFrameAdorner.StrengthProperty, 0, 1);
            else frame.Strength = .45;
        }
        var iconGlow = new DropShadowEffect { Color = color, ShadowDepth = 0, BlurRadius = 12, Opacity = .8 };
        _animatedNeonEffects.Add(iconGlow);
        if (SystemParameters.ClientAreaAnimation) StartNeonPulse(iconGlow, DropShadowEffect.OpacityProperty, .3, .9);
        Resources["NeonIconGlow"] = iconGlow;
        var surfaceGlow = ((DropShadowEffect)Resources["NeonGlow"]).Clone();
        _animatedNeonEffects.Add(surfaceGlow);
        if (SystemParameters.ClientAreaAnimation) StartNeonPulse(surfaceGlow, DropShadowEffect.OpacityProperty, .3, .9);
        Resources["NeonGlow"] = surfaceGlow;
        foreach (var action in FindVisualChildren<Button>(Root).Where(IsNeonButton).ToArray())
        {
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
        var show = SettingsPanel.Visibility != Visibility.Visible;
        foreach (var (_, frame) in _neonFrames) frame.Opacity = show ? 1 : 0;
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
        StopNeonPulse(effect, DropShadowEffect.OpacityProperty);
        effect.Opacity = !button.IsEnabled ? 0 : activeHover ? 1 : .45;
        effect.BlurRadius = activeHover ? 18 : 10;
        ClearNeonHoverSurface(button);
        if (activeHover)
        {
            button.ApplyTemplate();
            var surface = new[] { "ActionBorder", "StepSurface", "ButtonBorder", "NoticeSurface", "ArrowSurface" }
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
        if (button.IsEnabled && !activeHover && SystemParameters.ClientAreaAnimation)
            StartNeonPulse(effect, DropShadowEffect.OpacityProperty, .16, .8);
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
        foreach (var clock in _neonPulseClocks.Values) clock.Controller?.Remove();
        _neonPulseClocks.Clear();
        foreach (var (layer, frame) in _neonFrames)
        {
            frame.BeginAnimation(NeonFrameAdorner.StrengthProperty, null);
            BindingOperations.ClearBinding(frame, VisibilityProperty);
            layer.Remove(frame);
        }
        _neonFrames.Clear();
        foreach (var effect in _animatedNeonEffects)
            if (!effect.IsFrozen) effect.BeginAnimation(DropShadowEffect.OpacityProperty, null);
        _animatedNeonEffects.Clear();
        foreach (var (button, effect) in _neonButtons)
        {
            ClearNeonHoverSurface(button);
            effect.BeginAnimation(DropShadowEffect.OpacityProperty, null);
            if (ReferenceEquals(button.Effect, effect)) button.ClearValue(EffectProperty);
            button.MouseEnter -= NeonButtonHover;
            button.MouseLeave -= NeonButtonHover;
            button.IsEnabledChanged -= NeonButtonEnabledChanged;
        }
        _neonButtons.Clear();
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
        _haloPens = new[] { (24.0, .025), (19.0, .04), (14.0, .065), (10.0, .11), (6.0, .2), (3.0, .35) }
            .Select(layer => new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * layer.Item2), color.R, color.G, color.B)), layer.Item1)).ToArray();
        var core = Color.FromRgb(Math.Max(color.R, (byte)185), Math.Max(color.G, (byte)185), Math.Max(color.B, (byte)185));
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
