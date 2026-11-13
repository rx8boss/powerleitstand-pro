using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Path = System.Windows.Shapes.Path;
using Color = System.Windows.Media.Color;

namespace PowerControlProWpf;

internal static class ProgramInstalledVisuals
{
    internal static FrameworkElement ButtonContent(out TextBlock caption)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var icon = new Grid { Name = "InstalledProgramsIcon", Width = 19, Height = 18, Margin = new(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        var windows = new Path { Data = Geometry.Parse("M4,1 L16,1 L16,4 M1,5 L14,5 L14,16 L1,16 Z M1,8 L14,8"), StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round };
        windows.SetResourceReference(Shape.StrokeProperty, "TextBrush"); icon.Children.Add(windows);
        var check = new Path { Data = Geometry.Parse("M10,13 L13,16 L18,10"), StrokeThickness = 2.1,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        }; check.SetResourceReference(Shape.StrokeProperty, "ReadyBrush"); icon.Children.Add(check);
        content.Children.Add(icon);
        caption = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(caption); return content;
    }
}

internal sealed class ProgramInstalledLoading : Grid, IDisposable
{
    private readonly RotateTransform _rotation = new(0, 24, 24);
    private bool _disposed;
    internal ProgramInstalledLoading(bool english)
    {
        Name = "InstalledProgramsLoading";
        HorizontalAlignment = HorizontalAlignment.Stretch; VerticalAlignment = VerticalAlignment.Stretch;
        IsHitTestVisible = false; ClipToBounds = true;
        var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new(12) };
        var circle = new Grid { Width = 48, Height = 48, Margin = new(0, 0, 0, 17) };
        var ring = new Ellipse { StrokeThickness = 4, Opacity = .18 }; ring.SetResourceReference(Shape.StrokeProperty, "UnifiedAccent"); circle.Children.Add(ring);
        var arc = new Path { Name = "InstalledProgramsLoadingArc", Data = Geometry.Parse("M24,4 A20,20 0 1 1 4,24"), StrokeThickness = 4,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, RenderTransform = _rotation };
        arc.SetResourceReference(Shape.StrokeProperty, "UnifiedAccent"); circle.Children.Add(arc); center.Children.Add(circle);
        string label = english ? "Loading installed programs …" : "Installierte Programme werden geladen …";
        var title = new TextBlock { Text = label, FontSize = 15, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap, MaxWidth = 430 };
        title.SetResourceReference(TextBlock.ForegroundProperty, "UnifiedText"); center.Children.Add(title);
        var hint = new TextBlock { Text = english ? "Windows programs, portable apps and Store apps" : "Windows-Programme, portable Apps und Store-Apps",
            FontSize = 11.5, Margin = new(0, 8, 0, 0), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "UnifiedMuted"); center.Children.Add(hint); Children.Add(center);
        AutomationProperties.SetName(this, label);
        Loaded += OnLoaded; Unloaded += OnUnloaded; IsVisibleChanged += OnVisibility;
    }
    private void OnLoaded(object sender, RoutedEventArgs e) => Animate();
    private void OnUnloaded(object sender, RoutedEventArgs e) => Stop();
    private void OnVisibility(object sender, DependencyPropertyChangedEventArgs e) { if (IsVisible) Animate(); else Stop(); }
    private void Animate()
    {
        if (_disposed || !IsLoaded || !IsVisible || !SystemParameters.ClientAreaAnimation || _rotation.HasAnimatedProperties) return;
        _rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
    }
    private void Stop() => _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        Loaded -= OnLoaded; Unloaded -= OnUnloaded; IsVisibleChanged -= OnVisibility;
        Stop(); Visibility = Visibility.Collapsed;
    }
}
