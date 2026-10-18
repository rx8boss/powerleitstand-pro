using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;
using Button = System.Windows.Controls.Button;
using Binding = System.Windows.Data.Binding;
using RelativeSource = System.Windows.Data.RelativeSource;

namespace PowerControlProWpf;

internal sealed class RepairLogWindow : Window
{
    private static Brush Solid(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

    internal RepairLogWindow(IReadOnlyList<RepairLogEntry> entries, ThemePalette palette, bool english, Style? scrollBarStyle)
    {
        Title = english ? "Repair log - PowerLeitstand PRO" : "Reparaturprotokoll - PowerLeitstand PRO";
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/app-icon.ico"));
        Width = 720;
        Height = 520;
        MinWidth = 560;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        ShowInTaskbar = false;
        Background = Solid(palette.Colors[0]);
        Foreground = Solid(palette.Colors[5]);
        FontFamily = new FontFamily(palette.FontFamily);
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);

        var borderColor = Solid(palette.IsNeon ? palette.Colors[7] : palette.Colors[8]);
        var subtle = Solid(palette.Colors[6]);
        var accent = Solid(palette.Colors[7]);
        var card = Solid(palette.Colors[3]);
        Resources["InputBrush"] = Solid(palette.Colors[4]);
        Resources["InputBorderBrush"] = borderColor;
        Resources["AccentBrush"] = accent;
        Resources["TextBrush"] = Foreground;
        Resources["ButtonBrush"] = Solid(palette.Colors[9]);
        if (scrollBarStyle is not null) Resources[typeof(ScrollBar)] = scrollBarStyle;
        var outer = new Border
        {
            BorderBrush = borderColor, BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(11), Background = card, Padding = new Thickness(14)
        };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        outer.Child = layout;
        Content = outer;
        layout.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 0 0 L 38 0 M 0 0 L 38 38"),
            Stroke = borderColor, StrokeThickness = 1, Opacity = 0.55,
            Width = 38, Height = 38,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false
        });

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); };
        var title = new TextBlock
        {
            Text = english ? "Repair log" : "Reparaturprotokoll", FontSize = 16,
            FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center,
            Foreground = Foreground
        };
        var heading = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(new System.Windows.Controls.Image
        {
            Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(
                $"pack://application:,,,/Assets/icons/{palette.IconSet}/log.png")),
            Width = 29, Height = 29, Margin = new Thickness(0, 0, 9, 0)
        });
        heading.Children.Add(title);
        header.Children.Add(heading);
        var close = CreateButton("×", Solid("#A51D36"), Solid("#FF6388"));
        close.Width = 30;
        close.Height = 29;
        close.Padding = new Thickness(0);
        close.Content = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 3,3 L 16,16 M 16,3 L 3,16"),
            Stroke = Solid("#FF4B4B"),
            StrokeThickness = 2, Width = 13, Height = 13, Stretch = Stretch.Uniform
        };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        layout.Children.Add(header);

        var errorCount = entries.Count(entry => entry.Level == "FEHLER");
        var successCount = entries.Count(entry => entry.Level == "ERFOLG");
        var note = new TextBlock
        {
            Text = english
                ? $"{entries.Count} saved entries · {successCount} successful · {errorCount} errors · newest first"
                : $"{entries.Count} gespeicherte Einträge · {successCount} Erfolge · {errorCount} Fehler · neueste zuerst",
            FontSize = 13, Foreground = subtle, Margin = new Thickness(0, 4, 0, 11)
        };
        Grid.SetRow(note, 1);
        layout.Children.Add(note);

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = Solid(palette.Colors[4])
        };
        var logFrame = new Border
        {
            BorderBrush = borderColor, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Background = scroller.Background,
            Child = scroller
        };
        Grid.SetRow(logFrame, 2);
        layout.Children.Add(logFrame);
        var list = new StackPanel { Margin = new Thickness(10, 9, 10, 3) };
        scroller.Content = list;
        if (entries.Count == 0)
            list.Children.Add(new TextBlock
            {
                Text = english ? "No repair entries are saved." : "Es sind keine Reparatureinträge gespeichert.",
                FontSize = 14, Foreground = subtle
            });
        foreach (var entry in entries.Select(e => e.WithLanguage(english)))
        {
            var levelColor = entry.Level switch
            {
                "FEHLER" => Solid(palette.Actions[1]),
                "ABBRUCH" => Solid(palette.Actions[5]),
                "ERFOLG" => Solid(palette.Ready),
                _ => accent
            };
            var entryCard = new Border
            {
                Background = card, BorderBrush = levelColor,
                BorderThickness = new Thickness(3, 1, 1, 1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(11, 9, 11, 9),
                Margin = new Thickness(0, 0, 0, 8)
            };
            var entryContent = new StackPanel();
            entryCard.Child = entryContent;
            var head = new TextBlock
            {
                Text = $"{entry.Timestamp}   ·   {entry.Phase}   ·   {entry.DisplayLevel}",
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = levelColor
            };
            var message = new TextBlock
            {
                Text = entry.DisplayMessage, FontSize = 15, Foreground = Foreground,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0)
            };
            entryContent.Children.Add(head);
            entryContent.Children.Add(message);
            list.Children.Add(entryCard);
        }

        var done = CreateButton(english ? "Close" : "Schließen", Solid(palette.Colors[9]), borderColor);
        var doneContent = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        doneContent.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 3,3 L 16,16 M 16,3 L 3,16"),
            Stroke = Solid("#FF4B4B"), StrokeThickness = 1.8, Width = 14, Height = 14,
            Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 7, 0)
        });
        doneContent.Children.Add(new TextBlock { Text = english ? "Close" : "Schließen", VerticalAlignment = VerticalAlignment.Center });
        done.Content = doneContent;
        done.MinWidth = 140;
        done.Height = 36;
        done.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        done.VerticalAlignment = VerticalAlignment.Bottom;
        done.Click += (_, _) => Close();
        Grid.SetRow(done, 3);
        layout.Children.Add(done);
    }

    private Button CreateButton(string label, Brush background, Brush border) => new()
    {
        Content = label, Background = background, BorderBrush = border,
        Foreground = Foreground, BorderThickness = new Thickness(1),
        Padding = new Thickness(12, 4, 12, 4), FontSize = 14,
        FontWeight = FontWeights.SemiBold, Cursor = System.Windows.Input.Cursors.Hand,
        Template = RoundedButtonTemplate()
    };

    private static ControlTemplate RoundedButtonTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var frame = new FrameworkElementFactory(typeof(Border));
        frame.Name = "Frame";
        frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        frame.SetBinding(Border.BackgroundProperty, new Binding(nameof(Button.Background))
            { RelativeSource = RelativeSource.TemplatedParent });
        frame.SetBinding(Border.BorderBrushProperty, new Binding(nameof(Button.BorderBrush))
            { RelativeSource = RelativeSource.TemplatedParent });
        frame.SetBinding(Border.BorderThicknessProperty, new Binding(nameof(Button.BorderThickness))
            { RelativeSource = RelativeSource.TemplatedParent });
        frame.SetBinding(Border.PaddingProperty, new Binding(nameof(Button.Padding))
            { RelativeSource = RelativeSource.TemplatedParent });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        frame.AppendChild(content);
        template.VisualTree = frame;
        var hover = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.86) { TargetName = "Frame" });
        template.Triggers.Add(hover);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.68) { TargetName = "Frame" });
        template.Triggers.Add(pressed);
        return template;
    }
}
