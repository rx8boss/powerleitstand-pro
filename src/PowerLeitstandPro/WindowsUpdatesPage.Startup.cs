using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace PowerControlProWpf;

public partial class WindowsUpdatesPage
{
    private Grid? _startupOverlay;
    internal bool HasStartupOverlay => _startupOverlay is not null;
    internal StartupApps StartupInventory { get; set; } = new();

    private void CloseStartup()
    {
        if (_startupOverlay is not { } overlay) return;
        _startupOverlay = null;
        PageRoot.Children.Remove(overlay);
        OverviewRoot.IsEnabled = true;
        ProgramOverlayChanged?.Invoke();
        if (IsVisible) OpenStartupButton.Focus();
    }

    internal async Task OpenStartupAsync()
    {
        if (_startupOverlay is not null || _working || HostBusy() || Window.GetWindow(this) is not { } owner) return;
        UnifiedDialogAppearance.Resources(owner, _manufacturerPalette);
        var overlay = _startupOverlay = new Grid { Name = "StartupOverlay", Background = new SolidColorBrush(Color.FromArgb(145, 0, 9, 19)) };
        KeyboardNavigation.SetTabNavigation(overlay, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(overlay, KeyboardNavigationMode.Cycle);
        overlay.PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; CloseStartup(); } };
        OverviewRoot.IsEnabled = false;
        PageRoot.Children.Add(overlay);
        ProgramOverlayChanged?.Invoke();

        TextBlock Text(string value, double size, bool muted = false)
        {
            var text = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap };
            text.SetResourceReference(TextBlock.ForegroundProperty, muted ? "UnifiedMuted" : "UnifiedText");
            return text;
        }
        Button Action(string caption, RoutedEventHandler click)
        {
            var button = new Button { Content = caption, MinHeight = 34, Padding = new Thickness(12, 4, 12, 4) };
            UnifiedDialogAppearance.Button(owner, button, DialogButtonKind.Primary);
            AutomationProperties.SetName(button, caption);
            button.Click += click;
            return button;
        }

        var layout = new Grid();
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var title = L("Autostart verwalten", "Manage startup apps");
        UnifiedDialogAppearance.ReplaceHeader(owner, layout, title, _english, closeAction: CloseStartup);
        var list = new StackPanel();
        var scroll = new ScrollViewer { Content = list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        var body = new Border { Child = scroll };
        DialogLayout.ScrollHint(scroll, _english);
        Grid.SetRow(body, 1); layout.Children.Add(body);

        var footer = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        footer.ColumnDefinitions.Add(new()); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        void OpenWindowsStartup(object sender, RoutedEventArgs args)
        {
            try { OpenExternalLink(new Uri("ms-settings:startupapps")); }
            catch (Exception error) { WriteLog?.Invoke(UiText.Error(error, _english)); }
        }
        var settings = Action(L("Windows-Autostart öffnen", "Open Windows Startup settings"), OpenWindowsStartup);
        settings.HorizontalAlignment = HorizontalAlignment.Left;
        var close = Action(L("Schließen", "Close"), (_, _) => CloseStartup());
        Grid.SetColumn(close, 1); footer.Children.Add(settings); footer.Children.Add(close);
        Grid.SetRow(footer, 2); layout.Children.Add(footer);

        var frame = new Border { Child = layout, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(11), Padding = new Thickness(16),
            BorderThickness = new Thickness(1.3), ClipToBounds = true };
        frame.SetResourceReference(Border.BackgroundProperty, "DialogSurface");
        frame.SetResourceReference(Border.BorderBrushProperty, "UnifiedAccent");
        AutomationProperties.SetName(frame, title);
        void Fit()
        {
            frame.Width = Math.Min(586, Math.Max(0, overlay.ActualWidth - 24));
            frame.Height = Math.Min(365, Math.Max(0, overlay.ActualHeight - 16));
        }
        overlay.SizeChanged += (_, _) => Fit();
        overlay.Children.Add(frame);
        Fit();
        if (SystemParameters.ClientAreaAnimation)
        {
            var slide = new TranslateTransform(); frame.RenderTransform = slide;
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(160)));
        }
        close.Focus();

        var caption = Text(L("Die Schalter gelten für die nächste Windows-Anmeldung.", "Switches apply at the next Windows sign-in."), 11, true);
        list.Children.Add(Text(L("Autostarteinträge werden gelesen …", "Reading startup entries …"), 13));
        using var inventoryDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (apps, partial) = await Task.Run(() => StartupInventory.ReadWithPackagesAsync(inventoryDeadline.Token));
        if (!ReferenceEquals(_startupOverlay, overlay)) return;
        list.Children.Clear();
        caption.Margin = new Thickness(0, 0, 0, 8); list.Children.Add(caption);
        if (partial) list.Children.Add(Text(L("Einige Autostartquellen waren nicht lesbar.", "Some startup sources could not be read."), 12));
        if (apps.Count == 0) list.Children.Add(Text(L("Keine direkt änderbaren Autostarteinträge gefunden.", "No startup entries can be changed here."), 13));
        var icons = new List<(StartupApp App, Image Image, System.Windows.Shapes.Path Fallback)>();
        bool packageHeading = false;
        foreach (var app in apps)
        {
            if (app.Source == StartupSource.WindowsPackage && !packageHeading)
            {
                packageHeading = true;
                var section = Text(L("Windows- und Store-Apps", "Windows and Store apps"), 14);
                section.FontWeight = FontWeights.SemiBold;
                section.Margin = new Thickness(0, 8, 0, 6);
                list.Children.Add(section);
            }
            var row = new Grid(); row.ColumnDefinitions.Add(new() { Width = new GridLength(34) });
            row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = new GridLength(120) });
            var source = app.Source switch
            {
                StartupSource.UserRun => L("Registrierung · Aktueller Benutzer", "Registry · Current user"),
                StartupSource.WindowsPackage => L("Windows-App · Aktueller Benutzer", "Windows app · Current user"),
                _ => L("Autostartordner · Aktueller Benutzer", "Startup folder · Current user")
            };
            var name = app.Source == StartupSource.UserRun && app.Name.Equals(RegistryAutoStartRegistration.ValueName, StringComparison.OrdinalIgnoreCase)
                ? "PowerLeitstand PRO" : app.Name;
            var heading = Text(name, 13); heading.FontWeight = FontWeights.SemiBold;
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { heading, Text(source, 11, true) } };
            Grid.SetColumn(label, 1); row.Children.Add(label);
            var icon = new Image { Width = 24, Height = 24, Stretch = Stretch.Uniform, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            var fallback = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 2,2 H 20 V 18 H 2 Z M 2,7 H 20 M 6,12 H 16"),
                Width = 21, Height = 21, Stretch = Stretch.Uniform, StrokeThickness = 1.5, IsHitTestVisible = false
            };
            fallback.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "UnifiedAccent");
            var iconBox = new Grid { Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center, Children = { fallback, icon } };
            row.Children.Add(iconBox); icons.Add((app, icon, fallback));
            if (app.CanToggle && app.Enabled is { } enabled)
            {
                var toggle = new CheckBox { Style = (Style)owner.FindResource("DialogStatusSwitch"), Width = 42, Height = 23, IsChecked = enabled };
                AutomationProperties.SetName(toggle, name + " · " + L("Beim Anmelden starten", "Start at sign-in"));
                var state = Text(enabled ? L("An", "On") : L("Aus", "Off"), 12);
                state.Margin = new Thickness(8, 0, 0, 0); state.VerticalAlignment = VerticalAlignment.Center;
                var controls = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { toggle, state } };
                Grid.SetColumn(controls, 2); row.Children.Add(controls);
                var current = app;
                toggle.Click += async (_, _) =>
                {
                    bool requested = toggle.IsChecked == true;
                    if (_working || HostBusy()) { toggle.IsChecked = current.Enabled; return; }
                    toggle.IsEnabled = false;
                    try
                    {
                        current = await Task.Run(() => StartupInventory.SetAndReadAsync(current, requested));
                        if (current.Enabled != requested) throw new System.IO.IOException("Startup change could not be verified.");
                        state.Text = current.Enabled == true ? L("An", "On") : L("Aus", "Off");
                        caption.Text = L("Änderung gespeichert. Sie gilt für die nächste Anmeldung.", "Change saved. It applies at the next sign-in.");
                    }
                    catch (Exception error) when (StartupPackagedApps.ReadFailure(error) || error is OperationCanceledException)
                    {
                        caption.Text = L("Änderung nicht bestätigt. Bitte öffne die Liste erneut.", "Change was not confirmed. Reopen the list to check.");
                        WriteLog?.Invoke(UiText.Error(error, _english));
                        return;
                    }
                    finally { toggle.IsChecked = current.Enabled; }
                    if (current.CanToggle) toggle.IsEnabled = true;
                    else if (row.Parent is Border existingItem) list.Children.Remove(existingItem);
                };
            }
            var item = new Border { Child = row, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 0, 5),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
            item.SetResourceReference(Border.BackgroundProperty, "InputBrush");
            item.SetResourceReference(Border.BorderBrushProperty, "InputBorderBrush");
            list.Children.Add(item);
        }
        _ = LoadIconsAsync();

        async Task LoadIconsAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            foreach (var item in icons)
            {
                if (!ReferenceEquals(_startupOverlay, overlay) || deadline.IsCancellationRequested) return;
                try
                {
                    var bitmap = await Task.Run(() => item.App.Package is { } package
                        ? StartupPackagedApps.ReadIconAsync(package, deadline.Token)
                        : Task.FromResult(StartupApps.ReadIcon(item.App))).WaitAsync(deadline.Token);
                    if (!ReferenceEquals(_startupOverlay, overlay)) return;
                    if (bitmap is null) continue;
                    item.Image.Source = bitmap;
                    item.Image.Visibility = Visibility.Visible;
                    item.Fallback.Visibility = Visibility.Collapsed;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception error) when (StartupPackagedApps.ReadFailure(error)) { }
            }
        }
    }
}
