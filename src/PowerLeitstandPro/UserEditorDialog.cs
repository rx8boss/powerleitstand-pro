using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Control = System.Windows.Controls.Control;
using FontFamily = System.Windows.Media.FontFamily;
using Orientation = System.Windows.Controls.Orientation;
using Path = System.Windows.Shapes.Path;
using TextBox = System.Windows.Controls.TextBox;

namespace PowerControlProWpf;

internal sealed record UserEditValues(string Name, string FullName, string Description, string Password, bool WithoutPassword, bool Enabled, bool Administrator);

internal sealed class UserEditorDialog : Window
{
    private const string Person = "M 10,1 A 4,4 0 1 1 10,9 A 4,4 0 1 1 10,1 M 2,19 C 2,13 5,11 10,11 C 15,11 18,13 18,19 Z";
    private const string PersonPlus = "M 9,1 A 4,4 0 1 1 9,9 A 4,4 0 1 1 9,1 M 1,19 C 1,13 4,11 9,11 C 12,11 14,12 15,14 L 15,19 Z M 19,10 L 19,18 M 15,14 L 23,14";
    private const string Document = "M 3,1 L 13,1 L 18,6 L 18,20 L 3,20 Z M 12,1 L 12,7 L 18,7 M 6,11 L 15,11 M 6,15 L 15,15";
    private const string Lock = "M 4,9 L 17,9 L 17,20 L 4,20 Z M 7,9 L 7,6 A 3.5,3.5 0 0 1 14,6 L 14,9 M 10.5,13 L 10.5,17";
    private const string Gear = "M 10,1 L 12,1 L 13,4 L 16,5 L 19,3 L 21,5 L 19,8 L 20,11 L 23,12 L 23,15 L 20,16 L 19,19 L 21,21 L 19,23 L 16,21 L 13,22 L 12,25 L 9,25 L 8,22 L 5,21 L 3,23 L 1,21 L 3,18 L 2,16 L 0,15 L 0,12 L 3,11 L 4,8 L 2,5 L 4,3 L 7,5 L 9,4 Z M 11.5,9 A 4,4 0 1 0 11.5,17 A 4,4 0 1 0 11.5,9";
    private const string Shield = "M 11,1 L 20,5 L 19,13 C 18,18 14,21 11,23 C 7,21 4,18 3,13 L 2,5 Z M 11,2 L 11,21";
    private const string Eye = "M 1,10 C 5,3 15,3 19,10 C 15,17 5,17 1,10 Z M 10,7 A 3,3 0 1 0 10,13 A 3,3 0 1 0 10,7";
    private const string Key = "M 7,12 A 5,5 0 1 1 12,7 A 5,5 0 0 1 7,12 M 10,10 L 20,20 M 15,15 L 18,12";
    private const string SaveIcon = "M 2,2 L 17,2 L 20,5 L 20,20 L 2,20 Z M 6,2 L 6,9 L 16,9 L 16,2 M 6,20 L 6,13 L 16,13 L 16,20";
    private const string Cross = "M 2,2 L 18,18 M 18,2 L 2,18";
    private const string Trash = "M 4,5 L 18,5 M 7,5 L 8,20 L 15,20 L 16,5 M 9,5 L 10,2 L 13,2 L 14,5 M 10,8 L 10,17 M 13,8 L 13,17";

    private readonly TextBox _name = new();
    private readonly TextBox _fullName = new();
    private readonly TextBox _description = new();
    private readonly PasswordBox _password = new();
    private readonly PasswordBox _repeat = new();
    private readonly CheckBox _withoutPassword = new();
    private readonly List<Border> _passwordRows = new();
    private readonly CheckBox _enabled = new();
    private readonly CheckBox _administrator = new();
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly bool _creating;
    private readonly bool _english;
    private readonly Brush _text;
    private readonly Brush _subtle;
    private readonly Brush _accent;
    private readonly Brush _input;
    private readonly Brush _border;
    private readonly Brush _button;
    private readonly Brush _danger;
    private readonly Brush _dangerBorder;

    internal UserEditValues? Values { get; private set; }
    internal bool ResetPasswordRequested { get; private set; }
    internal bool DeleteRequested { get; private set; }

    internal UserEditorDialog(MainWindow owner, UserEntry? user)
    {
        Owner = owner;
        ClassicCancelAppearance.RegisterDialog(this, owner.CurrentThemeName == "Classic");
        Resources.MergedDictionaries.Add(owner.Resources);
        Icon = owner.Icon;
        _creating = user is null;
        _english = owner.IsEnglish;
        Title = _creating ? Tr("Benutzer erstellen", "Create user") : Tr("Benutzer bearbeiten", "Edit user");
        Width = _creating ? 416 : 390;
        Height = _creating ? 466 : 368;
        MinWidth = MaxWidth = Width;
        MinHeight = MaxHeight = Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        FontFamily = new FontFamily(owner.CurrentFontFamily);
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        _text = GetBrush(owner, "TextBrush", Brushes.White);
        _subtle = GetBrush(owner, "SubtleTextBrush", Brushes.LightGray);
        _accent = GetBrush(owner, "AccentBrush", Brushes.DeepSkyBlue);
        _input = GetBrush(owner, "InputBrush", Brushes.Black);
        _border = GetBrush(owner, "InputBorderBrush", _accent);
        _button = GetBrush(owner, "ButtonBrush", _input);
        _danger = GetBrush(owner, "DangerBrush", Brushes.DarkRed);
        _dangerBorder = GetBrush(owner, "DangerBorderBrush", Brushes.IndianRed);
        Background = GetBrush(owner, "AppBrush", _input);

        var root = new Border
        {
            Background = GetBrush(owner, "CardSurfaceBrush", _input),
            BorderBrush = _accent,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(11, 9, 11, 12)
        };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(_creating ? 48 : 59) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = _creating ? new GridLength(19) : GridLength.Auto, MinHeight = _creating ? 0 : 7 });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(53) });

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(_creating
            ? IconPath(PersonPlus, 44, _accent, true, new Thickness(8, 0, 12, 0))
            : UserGearIcon());
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = Title, FontSize = 19, FontWeight = FontWeights.Bold, Foreground = _text });
        titles.Children.Add(new TextBlock
        {
            Text = _creating ? Tr("Neuen lokalen Windows-Benutzer anlegen", "Create a new local Windows user")
                             : Tr("Lokales Windows-Konto anpassen", "Edit a local Windows account"),
            FontSize = 11, Foreground = _subtle
        });
        titleRow.Children.Add(titles);
        header.Children.Add(titleRow);
        var close = new Button { Content = "×", Style = owner.FindResource("OptionsCloseButton") as Style, Width = 37, Height = 35, FontSize = 21 };
        close.Click += (_, _) => DialogResult = false;
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        layout.Children.Add(new Border
        {
            Child = header,
            Background = Brushes.Transparent,
            BorderBrush = _border,
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(6)
        });

        var formBorder = new Border
        {
            BorderBrush = _border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7),
            Background = _input, Margin = new Thickness(0, 2, 0, 0), ClipToBounds = true
        };
        var form = new StackPanel();
        var rowHeight = _creating ? 39 : 37;
        _name.Text = user?.Name ?? string.Empty;
        AddTextRow(form, Tr("Benutzername", "User name"), _creating ? Person : Lock, _name,
            Tr("z. B. max.mustermann", "e.g. max.smith"), rowHeight);
        _fullName.Text = user?.FullName ?? string.Empty;
        AddTextRow(form, Tr("Vollständiger Name", "Full name"), Person, _fullName,
            Tr("z. B. Max Mustermann", "e.g. Max Smith"), rowHeight);
        _description.Text = user?.Description ?? string.Empty;
        AddTextRow(form, Tr("Beschreibung", "Description"), Document, _description,
            Tr("z. B. Wartungskonto", "e.g. Maintenance account"), rowHeight);
        if (_creating)
        {
            AddPasswordRow(form, Tr("Kennwort", "Password"), _password,
                Tr("Kennwort eingeben", "Enter password"), rowHeight, owner);
            AddPasswordRow(form, Tr("Kennwort wiederholen", "Repeat password"), _repeat,
                Tr("Kennwort erneut eingeben", "Repeat password"), rowHeight, owner);
            AddSwitchRow(form, Tr("Ohne Kennwort", "No password"), Lock, _withoutPassword, rowHeight, owner);
            _withoutPassword.ToolTip = Tr("Unsicher: Windows kann ein kennwortloses Konto durch eine Sicherheitsrichtlinie ablehnen.",
                "Insecure: Windows security policy may reject an account without a password.");
            _withoutPassword.Checked += (_, _) =>
            {
                _password.Clear();
                _repeat.Clear();
                foreach (var row in _passwordRows) row.IsEnabled = false;
            };
            _withoutPassword.Unchecked += (_, _) =>
            {
                foreach (var row in _passwordRows) row.IsEnabled = true;
            };
        }
        else
        {
            var row = NewRow(rowHeight);
            var accountActions = new Grid { Margin = new Thickness(7, 0, 7, 0) };
            accountActions.ColumnDefinitions.Add(new ColumnDefinition());
            accountActions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(7) });
            accountActions.ColumnDefinitions.Add(new ColumnDefinition());
            var reset = new Button
            {
                Height = 29, HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                Background = _button, BorderBrush = _accent,
                BorderThickness = new Thickness(1.5), Style = owner.FindResource("UserToolbarButton") as Style,
                Content = ButtonContent(Key, Tr("Kennwort zurücksetzen", "Reset password"), _accent, _text, 15, 11)
            };
            reset.Click += (_, _) => { ResetPasswordRequested = true; DialogResult = true; };
            accountActions.Children.Add(reset);
            var delete = new Button
            {
                Height = 29, HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                Background = _danger, BorderBrush = _dangerBorder,
                BorderThickness = new Thickness(1.5), Style = owner.FindResource("UserToolbarButton") as Style,
                Content = ButtonContent(Trash, Tr("Löschen", "Delete"), Brushes.White, Brushes.White, 16, 11)
            };
            delete.Click += (_, _) => { DeleteRequested = true; DialogResult = true; };
            Grid.SetColumn(delete, 2);
            accountActions.Children.Add(delete);
            row.Child = accountActions;
            form.Children.Add(row);
        }
        _enabled.IsChecked = user?.Enabled ?? true;
        _administrator.IsChecked = user?.Role == "Administrator";
        AddSwitchRow(form, Tr("Konto aktiv", "Account enabled"), Gear, _enabled, rowHeight, owner);
        AddSwitchRow(form, Tr("Administrator", "Administrator"), Shield, _administrator, rowHeight, owner);
        formBorder.Child = form;
        Grid.SetRow(formBorder, 1);
        layout.Children.Add(formBorder);

        _error.Foreground = _dangerBorder;
        _error.FontSize = 11;
        _error.VerticalAlignment = VerticalAlignment.Center;
        if (!_creating) _error.Visibility = Visibility.Collapsed;
        Grid.SetRow(_error, 2);
        layout.Children.Add(_error);
        var actions = new Grid { Margin = new Thickness(12, 4, 12, 4) };
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        var cancel = DialogButton(Cross, Tr("Abbrechen", "Cancel"), _danger, _dangerBorder, owner);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => DialogResult = false;
        actions.Children.Add(cancel);
        var save = DialogButton(_creating ? PersonPlus : SaveIcon,
            _creating ? Tr("Erstellen", "Create") : Tr("Speichern", "Save"), _button, _accent, owner);
        save.IsDefault = true;
        save.Click += Save;
        Grid.SetColumn(save, 2);
        actions.Children.Add(save);
        var footer = new Border
        {
            Child = actions,
            Background = Brushes.Transparent,
            BorderBrush = _border,
            BorderThickness = new Thickness(0, 1, 0, 0),
            CornerRadius = new CornerRadius(6)
        };
        Grid.SetRow(footer, 3);
        layout.Children.Add(footer);
        root.Child = layout;
        Content = root;
        Loaded += (_, _) => (_creating ? _name : _fullName).Focus();
    }

    private string Tr(string german, string english) => _english ? english : german;
    private static Brush GetBrush(MainWindow owner, string key, Brush fallback) => owner.TryFindResource(key) as Brush ?? fallback;

    private static Path IconPath(string data, double size, Brush color, bool filled, Thickness margin) =>
        new()
        {
            Data = Geometry.Parse(data), Width = size, Height = size, Stretch = Stretch.Uniform,
            Fill = filled ? color : Brushes.Transparent, Stroke = color, StrokeThickness = filled ? 0.5 : 1.8,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            VerticalAlignment = VerticalAlignment.Center, Margin = margin
        };

    private Grid UserGearIcon()
    {
        var icon = new Grid { Width = 38, Height = 38, Margin = new Thickness(8, 0, 12, 0) };
        icon.Children.Add(new Path
        {
            Data = Geometry.Parse(Person), Width = 30, Height = 30, Stretch = Stretch.Uniform,
            Fill = _accent, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
        });
        icon.Children.Add(new Path
        {
            Data = Geometry.Parse(Gear), Width = 19, Height = 19, Stretch = Stretch.Uniform,
            Fill = _accent, Stroke = _input, StrokeThickness = 1,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom
        });
        return icon;
    }

    private static StackPanel ButtonContent(string icon, string label, Brush iconBrush, Brush textBrush, double iconSize, double fontSize)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(IconPath(icon, iconSize, icon == Cross
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 75, 75)) : iconBrush, false, new Thickness(0, 0, 7, 0)));
        content.Children.Add(new TextBlock { Text = label, Foreground = textBrush, FontSize = fontSize, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        return content;
    }

    private Button DialogButton(string icon, string label, Brush background, Brush outline, MainWindow owner)
    {
        var destructive = ReferenceEquals(background, _danger);
        return new Button
        {
            Style = owner.FindResource("UserToolbarButton") as Style,
            Background = background, BorderBrush = outline, BorderThickness = new Thickness(1.8),
            Height = 42, Padding = new Thickness(5, 3, 5, 3),
            Content = ButtonContent(icon, label, destructive ? Brushes.White : _accent, destructive ? Brushes.White : _text, 20, 14)
        };
    }

    private Border NewRow(int height) =>
        new() { Height = height, BorderBrush = _border, BorderThickness = new Thickness(0, 0, 0, 0.7) };

    private Grid RowGrid(int height, string label, string icon)
    {
        var grid = new Grid { Height = height };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_creating ? 166 : 142) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var caption = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        caption.Children.Add(IconPath(icon, 20, _accent, icon == Person || icon == Gear, new Thickness(0, 0, 9, 0)));
        caption.Children.Add(new TextBlock { Text = label, Foreground = _text, FontSize = _creating ? 11.5 : 10.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        grid.Children.Add(caption);
        return grid;
    }

    private void AddTextRow(StackPanel form, string label, string icon, TextBox field, string hint, int height)
    {
        var border = NewRow(height);
        var row = RowGrid(height, label, icon);
        var input = new Border
        {
            Margin = new Thickness(3, 4, 7, 4), Background = _input, BorderBrush = _accent,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5)
        };
        var inner = new Grid();
        field.Background = Brushes.Transparent;
        field.Foreground = _text;
        field.BorderThickness = new Thickness(0);
        field.FontSize = 12;
        field.Padding = new Thickness(8, 3, 6, 3);
        field.VerticalContentAlignment = VerticalAlignment.Center;
        inner.Children.Add(field);
        var placeholder = new TextBlock
        {
            Text = hint, Foreground = _subtle, FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
            IsHitTestVisible = false
        };
        inner.Children.Add(placeholder);
        void SyncHint() => placeholder.Visibility = field.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        field.TextChanged += (_, _) => SyncHint();
        SyncHint();
        input.Child = inner;
        Grid.SetColumn(input, 1);
        row.Children.Add(input);
        border.Child = row;
        form.Children.Add(border);
    }

    private void AddPasswordRow(StackPanel form, string label, PasswordBox hidden, string hint, int height, MainWindow owner)
    {
        var border = NewRow(height);
        var row = RowGrid(height, label, Lock);
        var inputShell = new Border
        {
            Margin = new Thickness(3, 4, 7, 4), Background = _input, BorderBrush = _accent,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5)
        };
        var input = new Grid();
        input.ColumnDefinitions.Add(new ColumnDefinition());
        input.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        var revealed = new TextBox { Visibility = Visibility.Collapsed };
        foreach (Control field in new Control[] { hidden, revealed })
        {
            field.Background = Brushes.Transparent;
            field.Foreground = _text;
            field.BorderThickness = new Thickness(0);
            field.FontSize = 12;
            field.Padding = new Thickness(8, 3, 4, 3);
        }
        input.Children.Add(hidden);
        input.Children.Add(revealed);
        var placeholder = new TextBlock
        {
            Text = hint, Foreground = _subtle, FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
            IsHitTestVisible = false
        };
        input.Children.Add(placeholder);
        bool visible = false;
        bool clearingRevealed = false;
        void SyncHint() => placeholder.Visibility = (visible ? revealed.Text.Length : hidden.Password.Length) == 0 ? Visibility.Visible : Visibility.Collapsed;
        hidden.PasswordChanged += (_, _) =>
        {
            if (hidden.Password.Length == 0 && revealed.Text.Length > 0)
            {
                clearingRevealed = true;
                revealed.Clear();
                clearingRevealed = false;
            }
            SyncHint();
        };
        revealed.TextChanged += (_, _) => { if (!clearingRevealed) hidden.Password = revealed.Text; SyncHint(); };
        var eye = new Button
        {
            Background = _button, BorderBrush = _accent, BorderThickness = new Thickness(1),
            Style = owner.FindResource("SmallButton") as Style, Padding = new Thickness(3),
            Content = IconPath(Eye, 18, _text, false, new Thickness(0)), ToolTip = Tr("Kennwort anzeigen", "Show password")
        };
        eye.Click += (_, _) =>
        {
            visible = !visible;
            if (visible)
            {
                revealed.Text = hidden.Password;
                revealed.Visibility = Visibility.Visible;
                hidden.Visibility = Visibility.Collapsed;
                revealed.Focus();
                revealed.CaretIndex = revealed.Text.Length;
            }
            else
            {
                hidden.Password = revealed.Text;
                clearingRevealed = true;
                revealed.Clear();
                clearingRevealed = false;
                hidden.Visibility = Visibility.Visible;
                revealed.Visibility = Visibility.Collapsed;
                hidden.Focus();
            }
            eye.ToolTip = visible ? Tr("Kennwort verbergen", "Hide password") : Tr("Kennwort anzeigen", "Show password");
            SyncHint();
        };
        Grid.SetColumn(eye, 1);
        input.Children.Add(eye);
        inputShell.Child = input;
        Grid.SetColumn(inputShell, 1);
        row.Children.Add(inputShell);
        border.Child = row;
        form.Children.Add(border);
        _passwordRows.Add(border);
    }

    private void AddSwitchRow(StackPanel form, string label, string icon, CheckBox check, int height, MainWindow owner)
    {
        var border = NewRow(height);
        var row = RowGrid(height, label, icon);
        check.Style = owner.FindResource("SettingsSwitch") as Style;
        check.Width = 57;
        check.Height = 27;
        check.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        check.VerticalAlignment = VerticalAlignment.Center;
        check.Margin = new Thickness(0, 0, 7, 0);
        check.Content = string.Empty;
        Grid.SetColumn(check, 1);
        row.Children.Add(check);
        border.Child = row;
        form.Children.Add(border);
    }

    private void Save(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_name.Text)) { ShowError(Tr("Benutzernamen eingeben.", "Enter a user name.")); return; }
        if (!LocalUserManager.IsValidName(_name.Text.Trim())) { ShowError(Tr("Ungültiger Benutzername (maximal 20 Zeichen).", "Invalid user name (maximum 20 characters).")); return; }
        var withoutPassword = _creating && _withoutPassword.IsChecked == true;
        if (_creating && !withoutPassword && string.IsNullOrEmpty(_password.Password)) { ShowError(Tr("Kennwort eingeben.", "Enter a password.")); return; }
        if (_creating && !withoutPassword && _password.Password != _repeat.Password) { ShowError(Tr("Kennwörter stimmen nicht überein.", "Passwords do not match.")); return; }
        Values = new UserEditValues(_name.Text.Trim(), _fullName.Text.Trim(), _description.Text.Trim(), withoutPassword ? string.Empty : _password.Password, withoutPassword, _enabled.IsChecked == true, _administrator.IsChecked == true);
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        _error.Text = message;
        if (_error.Visibility != Visibility.Visible)
        {
            _error.Visibility = Visibility.Visible;
            if (!_creating) Height = MaxHeight = MinHeight = 369;
        }
    }
}
