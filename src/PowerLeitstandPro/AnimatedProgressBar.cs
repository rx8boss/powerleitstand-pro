using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace PowerControlProWpf;

// The measured Value stays untouched. Only the presentation interpolates between measurements.
public sealed class AnimatedProgressBar : ProgressBar
{
    public static readonly DependencyProperty IsRunningProperty = DependencyProperty.Register(nameof(IsRunning), typeof(bool), typeof(AnimatedProgressBar), new FrameworkPropertyMetadata(false, StateChanged));
    public static readonly DependencyProperty IsAnimationSuspendedProperty = DependencyProperty.Register(nameof(IsAnimationSuspended), typeof(bool), typeof(AnimatedProgressBar), new FrameworkPropertyMetadata(false, StateChanged));
    public static readonly DependencyProperty ShowPercentageProperty = DependencyProperty.Register(nameof(ShowPercentage), typeof(bool), typeof(AnimatedProgressBar), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty PhaseProperty = DependencyProperty.Register("Phase", typeof(double), typeof(AnimatedProgressBar), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register("DisplayValue", typeof(double), typeof(AnimatedProgressBar), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    private AnimationClock? _motionClock;
    private bool _useReferenceBlue;

    public bool IsRunning { get => (bool)GetValue(IsRunningProperty); set => SetValue(IsRunningProperty, value); }
    public bool IsAnimationSuspended { get => (bool)GetValue(IsAnimationSuspendedProperty); set => SetValue(IsAnimationSuspendedProperty, value); }
    public bool ShowPercentage { get => (bool)GetValue(ShowPercentageProperty); set => SetValue(ShowPercentageProperty, value); }
    internal bool HasMotion => _motionClock is not null;
    internal double DisplayedValue => (double)GetValue(DisplayValueProperty);
    internal double MotionPhase => (double)GetValue(PhaseProperty);
    internal string PercentageText => ShowPercentage && !IsIndeterminate ? $"{Math.Floor(Ratio(DisplayedValue) * 100):0} %" : "";
    internal void ApplyPalette(ThemePalette palette)
    {
        _useReferenceBlue = palette.Colors[0] == "#04162B";
        InvalidateVisual();
    }

    static AnimatedProgressBar()
    {
        IsIndeterminateProperty.OverrideMetadata(typeof(AnimatedProgressBar), new FrameworkPropertyMetadata(false, StateChanged));
        BackgroundProperty.OverrideMetadata(typeof(AnimatedProgressBar), new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));
        ForegroundProperty.OverrideMetadata(typeof(AnimatedProgressBar), new FrameworkPropertyMetadata(Brushes.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));
    }

    public AnimatedProgressBar()
    {
        // Draw one shared vector presentation instead of the platform's progress template.
        Template = new ControlTemplate(typeof(AnimatedProgressBar));
        Height = 34;
        Loaded += (_, _) => { SnapToMeasurement(); UpdateMotion(); };
        Unloaded += (_, _) => { StopMotion(); SnapToMeasurement(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) SnapToMeasurement(); UpdateMotion(); };
    }

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        var previous = DisplayedValue;
        BeginAnimation(DisplayValueProperty, null);
        SetValue(DisplayValueProperty, newValue);
        if (IsLoaded && IsVisible && IsRunning && !IsIndeterminate && !IsAnimationSuspended && SystemParameters.ClientAreaAnimation)
            BeginAnimation(DisplayValueProperty, new DoubleAnimation(previous, newValue, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        InvalidateVisual();
    }

    private double Ratio(double value) => Maximum <= Minimum ? 0 : Math.Clamp((value - Minimum) / (Maximum - Minimum), 0, 1);
    private static void StateChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        var progress = (AnimatedProgressBar)value;
        if (!progress.IsRunning || progress.IsAnimationSuspended || progress.IsIndeterminate) progress.SnapToMeasurement();
        progress.UpdateMotion(); progress.InvalidateVisual();
    }
    private void SnapToMeasurement() { BeginAnimation(DisplayValueProperty, null); SetValue(DisplayValueProperty, Value); }
    private void UpdateMotion()
    {
        if (!IsLoaded || !IsVisible || !IsRunning || IsAnimationSuspended || !SystemParameters.ClientAreaAnimation) { StopMotion(); return; }
        if (_motionClock is not null) return;
        var animation = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1.45)) { RepeatBehavior = RepeatBehavior.Forever };
        _motionClock = animation.CreateClock(); ApplyAnimationClock(PhaseProperty, _motionClock);
    }
    private void StopMotion()
    {
        BeginAnimation(PhaseProperty, null); _motionClock = null;
    }

    private static Color Mix(Color background, Color accent, double amount) => Color.FromRgb(
        (byte)(background.R + (accent.R - background.R) * amount),
        (byte)(background.G + (accent.G - background.G) * amount),
        (byte)(background.B + (accent.B - background.B) * amount));

    protected override void OnRender(DrawingContext drawing)
    {
        if (ActualWidth <= 1 || ActualHeight <= 1) return;
        var accent = (Foreground as SolidColorBrush)?.Color ?? Colors.Cyan;
        var background = (Background as SolidColorBrush)?.Color ?? Color.FromRgb(4, 23, 41);
        var outer = new Rect(.5, .5, Math.Max(0, ActualWidth - 1), Math.Max(0, ActualHeight - 1));
        drawing.DrawRoundedRectangle(_useReferenceBlue ? new SolidColorBrush(Color.FromRgb(6, 28, 48)) : Background, new Pen(BorderBrush ?? Foreground, 1), outer, 7, 7);
        var inner = new Rect(4, 4, Math.Max(0, ActualWidth - 8), Math.Max(0, ActualHeight - 8));
        if (inner.Width <= 0 || inner.Height <= 0) return;
        var phase = MotionPhase;
        double fillWidth = inner.Width * Ratio(DisplayedValue);
        double fillLeft = inner.Left;
        if (IsIndeterminate)
        {
            fillWidth = Math.Min(140, inner.Width * .32);
            fillLeft = inner.Left + (HasMotion ? phase : .5) * (inner.Width + fillWidth) - fillWidth;
        }
        if (fillWidth > 0)
        {
            drawing.PushClip(new RectangleGeometry(inner, 4, 4));
            var fill = new Rect(fillLeft, inner.Top, fillWidth, inner.Height);
            var gradient = new LinearGradientBrush(
                _useReferenceBlue ? Color.FromRgb(7, 85, 138) : Mix(background, accent, .36),
                _useReferenceBlue ? Color.FromRgb(11, 143, 192) : Mix(background, accent, .72), 0);
            drawing.DrawRoundedRectangle(gradient, null, fill, 4, 4);
            drawing.PushClip(new RectangleGeometry(fill, 4, 4));
            var stripePen = new Pen(new SolidColorBrush(Color.FromArgb(48, accent.R, accent.G, accent.B)), 8);
            var offset = phase * 24;
            for (double x = fill.Left - inner.Height - 24; x < fill.Right + 24; x += 24)
                drawing.DrawLine(stripePen, new Point(x + offset, inner.Bottom), new Point(x + inner.Height + offset, inner.Top));
            if (HasMotion)
            {
                var shineWidth = Math.Min(140, Math.Max(60, fillWidth * .35));
                var shineLeft = fill.Left + phase * (fillWidth + shineWidth) - shineWidth;
                var shine = new LinearGradientBrush(new GradientStopCollection {
                    new(Colors.Transparent, 0), new(Color.FromArgb(145, 215, 253, 255), .5), new(Colors.Transparent, 1)
                }, 0);
                drawing.DrawRectangle(shine, null, new Rect(shineLeft, inner.Top, shineWidth, inner.Height));
            }
            drawing.Pop(); drawing.Pop();
        }
        var label = PercentageText;
        if (label.Length == 0) return;
        var font = new Typeface(FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var size = Math.Clamp(ActualHeight * .42, 11, 14);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var white = new FormattedText(label, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight, font, size, Brushes.White, dpi);
        var origin = new Point((ActualWidth - white.Width) / 2, (ActualHeight - white.Height) / 2);
        // A fine outline keeps the white percentage readable on light palettes, without a box.
        var geometry = white.BuildGeometry(origin);
        drawing.DrawGeometry(new SolidColorBrush(Color.FromArgb(180, 0, 12, 25)), new Pen(new SolidColorBrush(Color.FromArgb(180, 0, 12, 25)), 1.1), geometry);
        drawing.DrawText(white, origin);
    }
}
