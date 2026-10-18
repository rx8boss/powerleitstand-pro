using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MediaColor = System.Windows.Media.Color;
using MediaBrushes = System.Windows.Media.Brushes;

namespace PowerControlProWpf;

public partial class MainWindow
{
    private Border? _repairProgressIndicator;
    private Border? _repairProgressGlow;
    private TranslateTransform? _repairProgressTransform;
    private double _repairGlowTrackWidth;
    private double _repairGlowTrackHeight;
    private bool _repairGlowWaiting;
    private MediaColor _repairGlowAccent;

    private void UpdateRepairProgressAnimation()
    {
        // Activity is decorative; Value and the displayed percentage remain engine measurements.
        var waiting = _repairRunning && _repairPercent <= 0;
        if (RepairProgress.IsIndeterminate != waiting) RepairProgress.IsIndeterminate = waiting;
        if (!_repairRunning || !RepairProgress.IsVisible)
        {
            StopRepairProgressAnimation();
            return;
        }
        RepairProgress.ApplyTemplate();
        var indicator = RepairProgress.Template?.FindName("PART_Indicator", RepairProgress) as Border;
        if (indicator is null || indicator.ActualWidth <= 0 || indicator.ActualHeight <= 0)
        {
            StopRepairProgressAnimation();
            return;
        }
        var accent = ((SolidColorBrush)FindResource("AccentBrush")).Color;
        if (ReferenceEquals(indicator, _repairProgressIndicator) && _repairProgressGlow is not null &&
            Math.Abs(indicator.ActualWidth - _repairGlowTrackWidth) < .1 &&
            Math.Abs(indicator.ActualHeight - _repairGlowTrackHeight) < .1 &&
            waiting == _repairGlowWaiting && accent == _repairGlowAccent) return;

        StopRepairProgressAnimation();
        _repairProgressIndicator = indicator;
        _repairGlowTrackWidth = indicator.ActualWidth;
        _repairGlowTrackHeight = indicator.ActualHeight;
        _repairGlowWaiting = waiting;
        _repairGlowAccent = accent;
        if (waiting) indicator.Background = MediaBrushes.Transparent;
        else indicator.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        indicator.Clip = new RectangleGeometry(new Rect(0, 0, indicator.ActualWidth, indicator.ActualHeight), 4, 4);
        _repairProgressTransform = new TranslateTransform();
        _repairProgressGlow = new Border
        {
            Width = waiting ? 140 : 85,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            IsHitTestVisible = false,
            RenderTransform = _repairProgressTransform,
            Background = new LinearGradientBrush(new GradientStopCollection
            {
                new(Colors.Transparent, 0),
                new(waiting ? accent : MediaColor.FromArgb(150, 255, 255, 255), .5),
                new(Colors.Transparent, 1)
            }, 0)
        };
        indicator.Child = _repairProgressGlow;
        _repairProgressTransform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-_repairProgressGlow.Width, indicator.ActualWidth, TimeSpan.FromSeconds(1.8))
            { RepeatBehavior = RepeatBehavior.Forever });
    }

    private void StopRepairProgressAnimation()
    {
        _repairProgressTransform?.BeginAnimation(TranslateTransform.XProperty, null);
        if (_repairProgressIndicator is { } indicator)
        {
            if (ReferenceEquals(indicator.Child, _repairProgressGlow)) indicator.Child = null;
            indicator.ClearValue(UIElement.ClipProperty);
            indicator.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        }
        _repairProgressIndicator = null;
        _repairProgressGlow = null;
        _repairProgressTransform = null;
    }
}
