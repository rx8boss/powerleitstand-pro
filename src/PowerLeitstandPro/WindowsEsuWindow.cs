using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace PowerControlProWpf;

internal sealed class WindowsEsuWindow : Window
{
    private readonly WindowsUpdatesPage _page;
    private readonly bool _english;
    private readonly ThemePalette _palette;
    private readonly StackPanel _checks = new();
    private readonly TextBlock _status;
    private readonly TextBlock _error;
    private TextBlock? _accountResult;
    private Border? _accountIcon;
    private int _accountCheckVersion;
    private bool _closed;
    internal Func<Task<WindowsAccountInfo>> AccountLookup { get; set; } = WindowsAccountInfo.ReadAsync;
    internal Button OpenSettingsButton { get; }
    internal Button OpenAccountButton { get; }
    internal Button CheckButton { get; }
    internal CheckBox ConfirmedCheckBox { get; }
    internal Button SaveAndHideButton { get; }
    private string L(string de, string en) => _english ? en : de;

    internal WindowsEsuWindow(Window owner, WindowsUpdatesPage page, ThemePalette palette, bool english)
    {
        Owner = owner; _page = page; _english = english; _palette = palette;
        Title = "Windows 10 ESU"; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        Background = Brushes.Transparent; AllowsTransparency = true;
        foreach (var (key, index) in new[] { ("InputBrush", 4), ("InputBorderBrush", 8) })
            Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Colors[index]));

        Border Box(FrameworkElement content)
        {
            var border = new Border { Child = content, Padding = new Thickness(12, 10, 12, 10),
                BorderThickness = new Thickness(1.2), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 10) };
            border.SetResourceReference(Border.BackgroundProperty, "InputBrush");
            border.SetResourceReference(Border.BorderBrushProperty, "InputBorderBrush");
            return border;
        }
        void OpenSettings(string address)
        {
            try { _page.OpenExternalLink(new Uri(address)); }
            catch (Exception error)
            {
                _error.Text = L("Windows-Einstellungen konnten nicht geöffnet werden: ", "Could not open Windows Settings: ") + UiText.Error(error, _english);
                _error.Visibility = Visibility.Visible; _page.WriteLog?.Invoke(_error.Text);
            }
        }

        var body = new StackPanel();
        var statusLine = new Grid { MinHeight = 38 };
        statusLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        statusLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        statusLine.ColumnDefinitions.Add(new ColumnDefinition());
        var statusLabel = DialogLayout.Note(L("Status auf diesem PC", "Status on this PC"), palette, 16);
        statusLabel.FontWeight = FontWeights.SemiBold; statusLabel.VerticalAlignment = VerticalAlignment.Center;
        statusLine.Children.Add(statusLabel);
        var statusDivider = new Border { Width = 1, Margin = new Thickness(0, 3, 0, 3) };
        statusDivider.SetResourceReference(Border.BackgroundProperty, "UnifiedAccent");
        Grid.SetColumn(statusDivider, 1); statusLine.Children.Add(statusDivider);
        var statusValue = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0) };
        statusValue.Children.Add(new System.Windows.Shapes.Ellipse { Width = 14, Height = 14,
            Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Actions[5])), Margin = new Thickness(0, 0, 12, 0) });
        _status = DialogLayout.Note("", palette, 14); _status.FontWeight = FontWeights.SemiBold;
        statusValue.Children.Add(_status); Grid.SetColumn(statusValue, 2); statusLine.Children.Add(statusValue);
        body.Children.Add(Box(statusLine));
        body.Children.Add(Box(_checks));

        var note = new Grid();
        note.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        note.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        note.ColumnDefinitions.Add(new ColumnDefinition());
        var infoIcon = DialogLayout.Note("ⓘ", palette, 25); infoIcon.VerticalAlignment = VerticalAlignment.Center;
        infoIcon.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Colors[7])); note.Children.Add(infoIcon);
        var noteDivider = new Border { Width = 1, Margin = new Thickness(0, 2, 0, 2) };
        noteDivider.SetResourceReference(Border.BackgroundProperty, "UnifiedAccent");
        Grid.SetColumn(noteDivider, 1); note.Children.Add(noteDivider);
        var noteText = DialogLayout.Note(L(
            "Windows Update zeigt die ESU-Registrierung. Für die Anmeldung zu ESU brauchst du ein Microsoft-Konto mit Administratorrechten. Bei lokaler Windows-Anmeldung kannst du es im ESU-Assistenten angeben.",
            "Windows Update shows ESU enrollment. To enroll, use a Microsoft account with administrator rights. If you sign in locally, you can provide it in the ESU wizard."), palette, 12);
        noteText.Margin = new Thickness(14, 0, 0, 0);
        Grid.SetColumn(noteText, 2); note.Children.Add(noteText); body.Children.Add(Box(note));
        _error = DialogLayout.Note("", palette, 11); _error.Visibility = Visibility.Collapsed; body.Children.Add(_error);

        OpenAccountButton = new Button { Content = L("Konto öffnen ↗", "Open account ↗"), Padding = new Thickness(4, 0, 4, 0) };
        OpenAccountButton.Click += (_, _) => OpenSettings("ms-settings:yourinfo");
        AutomationProperties.SetName(OpenAccountButton, L("Windows-Anmeldung in den Einstellungen prüfen", "Check Windows sign-in in Settings"));
        CheckButton = new Button { Content = L("⟳  Erneut prüfen", "⟳  Check again") };
        CheckButton.Click += (_, _) => { Refresh(); _ = RefreshAccountAsync(); };
        OpenSettingsButton = new Button { Content = L("⚙  Windows-Update öffnen", "⚙  Open Windows Update") };
        OpenSettingsButton.Click += (_, _) => OpenSettings("ms-settings:windowsupdate");
        ConfirmedCheckBox = new CheckBox
        {
            Content = L("Mein PC ist bereits registriert", "My PC is already enrolled"),
            Foreground = noteText.Foreground, FontSize = 13, Margin = new Thickness(0, 6, 0, 4)
        };
        SaveAndHideButton = new Button
        {
            Content = L("Speichern und ausblenden", "Save and hide"),
            IsEnabled = false, FontSize = 12
        };
        ConfirmedCheckBox.Checked += (_, _) => SaveAndHideButton.IsEnabled = _page.ConfirmEsuRegistration is not null;
        ConfirmedCheckBox.Unchecked += (_, _) => SaveAndHideButton.IsEnabled = false;
        SaveAndHideButton.Click += (_, _) =>
        {
            if (ConfirmedCheckBox.IsChecked != true || _page.ConfirmEsuRegistration is not { } save) return;
            try { save(); }
            catch (Exception error)
            {
                _error.Text = L("Bestätigung konnte nicht gespeichert werden: ", "Could not save confirmation: ") + UiText.Error(error, _english);
                _error.Visibility = Visibility.Visible;
                _error.BringIntoView();
                return;
            }
            Close();
        };
        var close = new Button { Content = L("Schließen", "Close") }; close.Click += (_, _) => Close();
        var actions = new Grid();
        foreach (var width in new[] { new GridLength(1, GridUnitType.Star), new GridLength(10), new GridLength(1.42, GridUnitType.Star),
            new GridLength(10), new GridLength(1.48, GridUnitType.Star), new GridLength(10), new GridLength(.68, GridUnitType.Star) })
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        foreach (var (button, column) in new[] { (CheckButton, 0), (OpenSettingsButton, 2), (SaveAndHideButton, 4), (close, 6) })
        {
            button.Height = 42; button.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumn(button, column); actions.Children.Add(button);
        }
        var frame = new Border(); Content = frame;
        DialogLayout.Compose(this, frame, palette, "Windows 10 ESU", english, body, actions, preferredWidth: 720);
        var header = UnifiedDialogAppearance.Children<Grid>(frame).First(grid => grid.Name == "UnifiedDialogHeader");
        var heading = UnifiedDialogAppearance.Children<TextBlock>(header).First(text => text.Name == "UnifiedDialogHeading");
        heading.FontSize = 25;
        var subtitle = UnifiedDialogAppearance.Children<TextBlock>(header).First(text => text != heading);
        subtitle.Text = L("Sicherheitsupdates bis 12. Oktober 2027", "Security updates through October 12, 2027");
        subtitle.FontSize = 14;
        ((Grid)frame.Child).RowDefinitions[0].MinHeight = 86;
        var headerDivider = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(-20, 0, -20, -8) };
        headerDivider.SetResourceReference(Border.BackgroundProperty, "UnifiedAccent");
        Grid.SetColumnSpan(headerDivider, 3); header.Children.Add(headerDivider);
        close.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
        close.Style = (Style)FindResource("ManagerDialogTextButton");
        OpenAccountButton.Style = (Style)FindResource("ManagerDialogTextButton");
        var accent = (Color)ColorConverter.ConvertFromString(palette.Colors[7]);
        OpenSettingsButton.Background = new SolidColorBrush(accent);
        OpenSettingsButton.Resources["UnifiedHover"] = new SolidColorBrush(accent);
        OpenSettingsButton.Foreground = new SolidColorBrush(accent.R * .299 + accent.G * .587 + accent.B * .114 < 150
            ? System.Windows.Media.Colors.White : System.Windows.Media.Colors.Black);
        Loaded += (_, _) => _ = RefreshAccountAsync();
        Closed += (_, _) => { _closed = true; _accountCheckVersion++; };
        Refresh();
    }

    private async Task RefreshAccountAsync()
    {
        var version = ++_accountCheckVersion;
        WindowsAccountInfo account;
        try { account = await AccountLookup(); }
        catch (Exception) { account = new(WindowsAccountKind.Unknown); }
        if (_closed || version != _accountCheckVersion || _accountResult is null || _accountIcon is null) return;
        _accountResult.Text = account.Kind switch
        {
            WindowsAccountKind.Microsoft => account.Name is { } name
                ? L("Microsoft-Konto · ", "Microsoft account · ") + name
                : L("Microsoft-Konto erkannt", "Microsoft account detected"),
            WindowsAccountKind.Local => L("Lokales Konto", "Local account"),
            WindowsAccountKind.Work => L("Geschäfts- oder Schulkonto", "Work or school account"),
            _ => L("Nicht sicher ermittelbar", "Cannot determine reliably")
        };
        _accountResult.ToolTip = _accountResult.Text;
        if (account.Kind != WindowsAccountKind.Unknown) SetRowIcon(_accountIcon, true);
    }

    private void SetRowIcon(Border icon, bool? passed)
    {
        var color = new SolidColorBrush((Color)ColorConverter.ConvertFromString(passed == true ? _palette.Ready : _palette.Actions[5]));
        icon.BorderBrush = color;
        var symbol = (TextBlock)icon.Child;
        symbol.Foreground = color;
        symbol.Text = passed switch { true => "✓", false => "!", _ => "?" };
    }

    internal void Refresh()
    {
        var info = WindowsEsuInfo.Read();
        _checks.Children.Clear();
        (TextBlock Result, Border Icon) Row(string label, string result, bool? passed, Button? action = null, CheckBox? confirmation = null)
        {
            var row = new Grid { MinHeight = 39 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(224) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var icon = new Border { Width = 25, Height = 25, CornerRadius = new CornerRadius(13),
                BorderThickness = new Thickness(2), VerticalAlignment = VerticalAlignment.Center };
            icon.Child = new TextBlock { FontSize = 17, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            SetRowIcon(icon, passed);
            row.Children.Add(icon);
            var name = DialogLayout.Note(label, _palette, 14); name.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(name, 1); row.Children.Add(name);
            var divider = new Border { Width = 1, Margin = new Thickness(0, 6, 0, 6) };
            divider.SetResourceReference(Border.BackgroundProperty, "UnifiedAccent"); Grid.SetColumn(divider, 2); row.Children.Add(divider);
            var value = new DockPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            var resultText = DialogLayout.Note(result, _palette, 13);
            resultText.TextWrapping = TextWrapping.NoWrap;
            resultText.TextTrimming = TextTrimming.CharacterEllipsis;
            if (action is not null)
            {
                action.Margin = new Thickness(12, 0, 0, 0);
                DockPanel.SetDock(action, Dock.Right);
                value.Children.Add(DialogLayout.Take(action));
            }
            value.Children.Add(confirmation is null ? resultText : DialogLayout.Stack(resultText, confirmation));
            Grid.SetColumn(value, 3); row.Children.Add(value); _checks.Children.Add(row);
            if (label != L("ESU-Registrierung", "ESU enrollment"))
            {
                var line = new Border { Height = 1 };
                line.SetResourceReference(Border.BackgroundProperty, "InputBorderBrush"); _checks.Children.Add(line);
            }
            return (resultText, icon);
        }
        Row("Windows 10 22H2", info.Build == 19045 ? L("Erkannt", "Detected") : L("Nicht erkannt", "Not detected"), info.Build == 19045);
        Row(L("Private ESU-Edition", "Consumer ESU edition"), info.EligibleEdition ? L("Erkannt", "Detected") : L("Nicht erkannt", "Not detected"), info.EligibleEdition);
        Row(L("App mit Adminrechten", "App running as administrator"), info.Administrator switch { true => L("Ja", "Yes"), false => L("Nein", "No"), _ => L("Nicht prüfbar", "Cannot check") }, info.Administrator);
        // shortcut: consumer ESU enrollment has no supported local status API; replace this link if Microsoft documents one.
        (_accountResult, _accountIcon) = Row(L("Windows-Anmeldung", "Windows sign-in"), L("Wird geprüft …", "Checking …"), null, OpenAccountButton);
        Row(L("ESU-Registrierung", "ESU enrollment"), L("Registrierungsstatus in Windows Update anzeigen", "View enrollment status in Windows Update"),
            null, confirmation: ConfirmedCheckBox);
        _status.Text = info.ShowOn(DateOnly.FromDateTime(DateTime.Now))
            ? L("Registrierung in Windows prüfen", "Check enrollment in Windows")
            : L("ESU auf diesem System nicht angeboten", "ESU is not offered on this system");
        _error.Visibility = Visibility.Collapsed;
    }
}
