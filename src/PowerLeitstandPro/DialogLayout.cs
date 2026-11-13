using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using Panel = System.Windows.Controls.Panel;
using Control = System.Windows.Controls.Control;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace PowerControlProWpf;

// Reparent existing controls, retaining their names, bindings and click handlers.
internal static class DialogLayout
{
    internal static T Take<T>(T element) where T : FrameworkElement
    {
        switch (element.Parent)
        {
            case Panel panel: panel.Children.Remove(element); break;
            case Border border: border.Child = null; break;
            case ContentControl content: content.Content = null; break;
            case Decorator decorator: decorator.Child = null; break;
        }
        Grid.SetRow(element, 0); Grid.SetColumn(element, 0);
        Grid.SetRowSpan(element, 1); Grid.SetColumnSpan(element, 1);
        return element;
    }

    internal static StackPanel Stack(params FrameworkElement[] elements)
    {
        var result = new StackPanel();
        foreach (var element in elements) result.Children.Add(Take(element));
        return result;
    }

    internal static Grid Actions(params Button[] buttons)
    {
        var grid = new Grid();
        for (int i = 0; i < buttons.Length; i++)
        {
            if (i > 0) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var button = Take(buttons[i]);
            button.Width = double.NaN; button.Height = 40; button.MinHeight = 40;
            button.Margin = new Thickness(0); button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(button, i * 2); grid.Children.Add(button);
        }
        return grid;
    }

    internal static void Compose(Window window, Border frame, ThemePalette palette, string title,
        bool english, FrameworkElement body, FrameworkElement footer, Button? close = null, double preferredWidth = 548,
        FrameworkElement? toolbar = null, bool fitHeightToOwner = true)
    {
        Take(body); Take(footer); if (close is not null) Take(close);
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, MinHeight = 66 });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var placeholder = new Grid();
        if (close is not null) placeholder.Children.Add(close);
        root.Children.Add(placeholder);
        var scroll = new ScrollViewer
        {
            Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 0, 0, 18), MaxHeight = 410,
            CanContentScroll = false, Focusable = true
        };
        var bodyHost = new Border { Child = scroll };
        Grid.SetRow(bodyHost, 1); root.Children.Add(bodyHost);
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        frame.Padding = new Thickness(20); frame.CornerRadius = new CornerRadius(11); frame.Child = root;
        window.MinWidth = 0; window.MaxWidth = double.PositiveInfinity; window.Width = preferredWidth;
        window.MinHeight = 0; window.MaxHeight = Math.Max(260, Math.Min(720, SystemParameters.WorkArea.Height - 32));
        window.SizeToContent = SizeToContent.Height; window.Height = double.NaN;
        ScrollHint(scroll, english);
        if (toolbar is not null)
        {
            var scrollBody = bodyHost.Child!;
            bodyHost.Child = null;
            DockPanel.SetDock(toolbar, Dock.Top);
            bodyHost.Child = new DockPanel { Children = { toolbar, scrollBody } };
        }
        UnifiedDialogAppearance.Apply(window, frame, root, palette, title, english);
        // The footer never scrolls; only the body shrinks on a small display/owner.
        void Fit()
        {
            var area = WindowPlacementService.GetCursorWorkAreaInDips(window);
            var availableWidth = Math.Min(area.Width - 24, window.Owner is { ActualWidth: > 0 } owner ? owner.ActualWidth - 32 : preferredWidth);
            window.Width = Math.Max(300, Math.Min(preferredWidth, availableWidth));
            var availableHeight = Math.Min(area.Height - 24,
                fitHeightToOwner && window.Owner is { ActualHeight: > 0 } parent ? parent.ActualHeight - 32 : 720);
            window.MaxHeight = Math.Max(240, availableHeight);
            scroll.MaxHeight = Math.Max(80, window.MaxHeight - root.RowDefinitions[0].ActualHeight - footer.ActualHeight - (toolbar?.ActualHeight ?? 0) - 62);
        }
        window.Loaded += (_, _) => Fit();
        window.SizeChanged += (_, _) => { if (window.IsLoaded) Fit(); };
    }

    internal static TextBlock Note(string text, ThemePalette palette, double size = 13) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size,
        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Colors[6]))
    };

    internal static StackPanel Field(TextBlock caption, TextBlock value, ThemePalette palette)
    {
        Take(caption); Take(value); caption.FontSize = 12; caption.Margin = new Thickness(0, 0, 0, 5);
        value.FontSize = 14; value.TextWrapping = TextWrapping.Wrap;
        value.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Colors[5]));
        return new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 12), Children = { caption, new Border
            {
                Child = value, Padding = new Thickness(9), CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Colors[4])),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Colors[8]))
            } }
        };
    }

    internal static void ScrollHint(ScrollViewer scroll, bool english)
    {
        if (scroll.Parent is not Border parent || scroll.Tag as string == "DialogScrollHint") return;
        scroll.Tag = "DialogScrollHint";
        parent.Child = null;
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(scroll);
        var hint = new TextBlock
        {
            FontSize = 11, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "UnifiedMuted");
        Grid.SetRow(hint, 1); grid.Children.Add(hint); parent.Child = grid;
        void Update()
        {
            hint.Visibility = scroll.ScrollableHeight > .5 ? Visibility.Visible : Visibility.Collapsed;
            hint.Text = scroll.VerticalOffset + scroll.ViewportHeight >= scroll.ExtentHeight - 1
                ? english ? "↑ Scroll up for previous details" : "↑ Für vorherige Details nach oben scrollen"
                : english ? "↓ Scroll for more details" : "↓ Für weitere Details scrollen";
        }
        scroll.ScrollChanged += (_, _) => Update(); scroll.Loaded += (_, _) => Update();
    }
}
