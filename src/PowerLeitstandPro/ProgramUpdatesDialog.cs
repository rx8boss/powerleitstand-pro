using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Image = System.Windows.Controls.Image;
using Panel = System.Windows.Controls.Panel;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace PowerControlProWpf;

internal static class ProgramUpdatesDialog
{
    internal static Task<ProgramEntry[]?> ShowAsync(ProgramCatalogView view, ProgramEntry[] entries)
    {
        var owner = Window.GetWindow(view) ?? throw new InvalidOperationException("No app window");
        var host = owner.FindName("Root") as Grid ?? throw new InvalidOperationException("No app surface");
        var completion = new TaskCompletionSource<ProgramEntry[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        string L(string de, string en) => view.English ? en : de;
        UnifiedDialogAppearance.Resources(owner, view.Palette);
        TextBlock Text(string value, double size = 12, bool muted = false)
        {
            var text = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap };
            text.SetResourceReference(TextBlock.ForegroundProperty, muted ? "UnifiedMuted" : "UnifiedText"); return text;
        }
        Button Action(string caption, bool danger = false)
        {
            var button = new Button { Content = new TextBlock { Text = caption, Foreground = Brushes.White }, Height = 34, MinHeight = 34, Padding = new(10, 0, 10, 0) };
            UnifiedDialogAppearance.Button(owner, button, danger ? DialogButtonKind.Danger : DialogButtonKind.Primary);
            AutomationProperties.SetName(button, caption); return button;
        }
        var layer = new Grid { Name = "ProgramUpdatesOverlay", Background = new SolidColorBrush(Color.FromArgb(180, 0, 10, 20)), Focusable = true };
        KeyboardNavigation.SetTabNavigation(layer, KeyboardNavigationMode.Cycle);
        Grid.SetRowSpan(layer, Math.Max(1, host.RowDefinitions.Count)); Grid.SetColumnSpan(layer, Math.Max(1, host.ColumnDefinitions.Count)); Panel.SetZIndex(layer, 6000);
        var card = new Border { Padding = new(19), CornerRadius = new(10), BorderThickness = new(1.2), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        card.SetResourceReference(Border.BackgroundProperty, "DialogSurface"); card.SetResourceReference(Border.BorderBrushProperty, "UnifiedAccent");
        var layout = new Grid(); layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new()); layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new Grid { Margin = new(0, 0, 0, 13) }; header.ColumnDefinitions.Add(new() { Width = new GridLength(37) }); header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = new GridLength(28) });
        header.Children.Add(ProgramFallbackIcon.Create(27, "UnifiedAccent"));
        var labels = new StackPanel { Margin = new(0, 0, 48, 0) }; var title = Text(L("Programm-Updates auswählen", "Select program updates"), 20); title.FontWeight = FontWeights.SemiBold; labels.Children.Add(title);
        labels.Children.Add(Text(entries.Length + L(" Updates verfügbar · gewünschte Programme anhaken", " updates available · select programs to update"), 11, true)); Grid.SetColumn(labels, 1); header.Children.Add(labels);
        var stripes = new System.Windows.Shapes.Path { Data = Geometry.Parse("M48,0 L50,0 L8,40 L6,40 Z M67,0 L69,0 L27,40 L25,40 Z M73,0 L87,0 L45,40 L31,40 Z"), Width = 53, Height = 32, Stretch = Stretch.Fill, Opacity = .2, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 0, 9, 0) };
        stripes.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "UnifiedAccent"); Grid.SetColumn(stripes, 1); header.Children.Add(stripes);
        var close = new Button { Content = new System.Windows.Shapes.Path { Data = Geometry.Parse("M0,0 L12,12 M12,0 L0,12"), Width = 11, Height = 11, Stretch = Stretch.Uniform, StrokeThickness = 1.5 } };
        UnifiedDialogAppearance.Button(owner, close, DialogButtonKind.Close); AutomationProperties.SetName(close, L("Schließen", "Close")); Grid.SetColumn(close, 2); header.Children.Add(close); layout.Children.Add(header);
        var selected = entries.Where(e => view.Selected.Contains(e.Id)).Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = new StackPanel(); var start = Action(""); start.Name = "ProgramUpdatesStart";
        var hint = Text("", 11, true); hint.Margin = new(0, 5, 0, 14);
        void Update()
        {
            start.Content = new TextBlock { Text = L("↻ Aktualisieren", "↻ Update") + " (" + selected.Count + ")", Foreground = Brushes.White };
            AutomationProperties.SetName(start, L("Aktualisieren", "Update") + " (" + selected.Count + ")"); start.IsEnabled = selected.Count > 0;
            hint.Text = selected.Count + L(" ausgewählt · Versionen werden vor dem Start erneut geprüft.", " selected · versions will be checked again before starting.");
        }
        foreach (var entry in entries)
        {
            var row = new Grid(); row.ColumnDefinitions.Add(new() { Width = new GridLength(30) }); row.ColumnDefinitions.Add(new() { Width = new GridLength(40) }); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var check = new CheckBox { Style = (Style)view.FindResource("UpdateSelection"), IsChecked = selected.Contains(entry.Id), Tag = entry.Id, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(check, L("Update auswählen: ", "Select update: ") + entry.Name);
            check.Checked += (_, _) => { selected.Add(entry.Id); Update(); }; check.Unchecked += (_, _) => { selected.Remove(entry.Id); Update(); }; row.Children.Add(check);
            var icon = view.Assets.Icon(entry.Id); UIElement logo = icon is null ? ProgramFallbackIcon.Create(26, "UnifiedAccent") : new Image { Source = icon, Width = 28, Height = 28, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
            Grid.SetColumn(logo, 1); row.Children.Add(logo);
            var names = new StackPanel(); var name = Text(entry.Name, 14); name.FontWeight = FontWeights.SemiBold; names.Children.Add(name);
            names.Children.Add(Text((view.Inventory.PortableUpdates?.GetValueOrDefault(entry.Id)?.InstalledVersion ?? view.Inventory.Installed.GetValueOrDefault(entry.Id) ?? L("Unbekannt", "Unknown")) + " → " + view.UpdateTarget(entry.Id), 11, true)); if (view.Inventory.PortableUpdates?.ContainsKey(entry.Id) == true) names.Children.Add(Text(L("Hersteller · Portable · Sicherung vor Austausch", "Publisher · Portable · backup before replacement"), 10, true)); Grid.SetColumn(names, 2); row.Children.Add(names);
            var tag = Text("Update", 11); tag.Margin = new(8, 0, 0, 0); tag.VerticalAlignment = VerticalAlignment.Center; tag.SetResourceReference(TextBlock.ForegroundProperty, "UnifiedAccent"); Grid.SetColumn(tag, 3); row.Children.Add(tag);
            var frame = new Border { Child = row, Padding = new(11, 10, 11, 10), Margin = new(0, 0, 0, 8), CornerRadius = new(6), BorderThickness = new(1) };
            frame.SetResourceReference(Border.BackgroundProperty, "InputBrush"); frame.SetResourceReference(Border.BorderBrushProperty, "InputBorderBrush"); rows.Children.Add(frame);
        }
        var scroll = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); layout.Children.Add(scroll); DialogLayout.ScrollHint(scroll, view.English);
        var footer = new StackPanel(); footer.Children.Add(hint); var actions = new Grid(); actions.ColumnDefinitions.Add(new()); actions.ColumnDefinitions.Add(new() { Width = new GridLength(9) }); actions.ColumnDefinitions.Add(new());
        var cancel = Action(L("Abbrechen", "Cancel"), true); actions.Children.Add(cancel); Grid.SetColumn(start, 2); actions.Children.Add(start); footer.Children.Add(actions); Grid.SetRow(footer, 2); layout.Children.Add(footer);
        card.Child = layout; layer.Children.Add(card);
        void Resize(object? sender, SizeChangedEventArgs? e)
        { card.Width = Math.Min(view.Compact ? 580 : 650, Math.Max(0, host.ActualWidth - 32)); card.MaxHeight = Math.Max(0, host.ActualHeight - 48); }
        void Finish(ProgramEntry[]? chosen)
        {
            if (completion.Task.IsCompleted) return;
            owner.PreviewKeyDown -= Key; owner.SizeChanged -= Resize; view.Unloaded -= Unloaded;
            host.Children.Remove(layer); view.SelectionOpen = false; view.RefreshAvailability(); completion.TrySetResult(chosen);
        }
        void Key(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Finish(null); } }
        void Unloaded(object sender, RoutedEventArgs e) => Finish(null);
        close.Click += (_, _) => Finish(null); cancel.Click += (_, _) => Finish(null);
        start.Click += (_, _) => { if (start.IsEnabled) Finish(entries.Where(e => selected.Contains(e.Id)).ToArray()); };
        owner.PreviewKeyDown += Key; owner.SizeChanged += Resize; view.Unloaded += Unloaded;
        host.Children.Add(layer); view.SelectionOpen = true; view.RefreshAvailability(); Resize(null, null); Update(); cancel.Focus(); return completion.Task;
    }
}
