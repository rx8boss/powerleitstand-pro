using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Brush = System.Windows.Media.Brush;

namespace PowerControlProWpf;

internal sealed class NetworkDnsWindow : Window
{
    internal RepairNetworkRequest? Request { get; private set; }

    internal NetworkDnsWindow(MainWindow owner, ThemePalette palette, bool english, bool administrator,
        RepairNetworkAdapter adapter, NetworkDnsBackup current, NetworkDnsBackup? restore = null)
    {
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        string T(string de, string en) => english ? en : de;
        var title = restore is null ? T("DNS-Server ändern", "Change DNS servers")
            : T("DNS-Einstellung wiederherstellen", "Restore DNS settings");
        Title = title;
        var frame = new Border();
        Content = frame;

        foreach (var key in new[] { "InputBrush", "TextBrush", "AccentBrush", "HoverBrush" })
            Resources[key] = owner.FindResource(key);
        var fieldBrush = (Brush)owner.FindResource("AppBrush");
        Resources["CardBrush"] = fieldBrush;
        var fieldBorder = (Brush)owner.FindResource("InputBorderBrush");
        var selected = restore ?? current;
        var families = new List<(string Label, bool V4, bool V6)>();
        var hasV4 = current.StaticServers is not null && selected.StaticServers is not null;
        var hasV6 = current.StaticServersV6 is not null && selected.StaticServersV6 is not null;
        if (hasV4) families.Add(("IPv4", true, false));
        if (hasV6) families.Add(("IPv6", false, true));
        if (hasV4 && hasV6) families.Add((T("IPv4 und IPv6", "IPv4 and IPv6"), true, true));
        var scope = Picker("DnsFamily", T("IP-Version", "IP version"), families.Select(item => item.Label));
        scope.Width = 135;
        scope.Margin = new Thickness(0, 0, 10, 0);
        scope.SelectedIndex = families.Count > 0 ? 0 : -1;
        var automatic = T("Automatisch", "Automatic");
        var manual = T("Manuell", "Manual");
        var mode = Picker("DnsMode", T("DNS-Profil", "DNS profile"), restore is null
            ? new object[] { automatic, manual }.Concat(NetworkDnsProfile.All)
            : new[] { T("Gesicherte Einstellung", "Saved settings") });
        var initial = hasV4 ? selected.StaticServers : selected.StaticServersV6;
        mode.SelectedIndex = restore is not null || string.IsNullOrEmpty(initial) ? 0 : 1;
        mode.IsEnabled = restore is null;
        var manualV4 = selected.StaticServers ?? "";
        var manualV6 = selected.StaticServersV6 ?? "";
        var input = AddressBox("DnsAddresses", "IPv4");
        var inputV6 = AddressBox("DnsAddressesV6", "IPv6");
        var rowV4 = AddressRow("IPv4", input);
        var rowV6 = AddressRow("IPv6", inputV6);
        var before = DialogLayout.Note("", palette);
        var choices = new DockPanel { Children = { scope, mode } };
        DockPanel.SetDock(scope, Dock.Left);
        var description = DialogLayout.Note("", palette, 12);
        description.Margin = new Thickness(0, 6, 0, 0);
        var problem = DialogLayout.Note("", palette, 12);
        var fields = DialogLayout.Stack(Label(T("Aktuell", "Current")), before,
            Label(T("Nach der Änderung", "After the change"), 12), choices, rowV4, rowV6, description, problem);
        var warning = DialogLayout.Note(restore is null
            ? T("Nur der ausgewählte Adapter wird geändert. Die bisherige Einstellung wird für eine Rückkehr gespeichert.",
                "Only the selected adapter is changed. The previous settings are saved so you can restore them.")
            : T("Die ausgewählten IP-Versionen werden aus der Sicherung für diesen Adapter wiederhergestellt.",
                "The selected IP versions will be restored from this adapter’s backup."), palette, 12);
        warning.Foreground = (Brush)owner.FindResource("WarningBrush");
        var warningBox = Box(warning);
        warningBox.Margin = new Thickness(0, 10, 0, 0);
        warningBox.BorderBrush = warning.Foreground;
        var copy = new Button { Content = T("Befehl kopieren", "Copy command"), Height = 34, Padding = new Thickness(10, 0, 10, 0) };
        var close = new Button { Content = T("Schließen", "Close"), IsCancel = true, Height = 34, Padding = new Thickness(14, 0, 14, 0) };
        var apply = new Button { Name = "ApplyNetworkDns", Content = restore is null ? T("Änderung übernehmen", "Apply change") : T("Wiederherstellen", "Restore"),
            Height = 34, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(10, 0, 0, 0) };
        var right = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Children = { close, apply } };
        var footer = new DockPanel { LastChildFill = false, Children = { copy, right } };
        DockPanel.SetDock(right, Dock.Right);
        DialogLayout.Compose(this, frame, palette, title, english, DialogLayout.Stack(Box(fields), warningBox), footer, preferredWidth: 615);
        UnifiedDialogAppearance.Button(this, copy, DialogButtonKind.Primary);
        UnifiedDialogAppearance.Button(this, close, DialogButtonKind.Primary);
        UnifiedDialogAppearance.Button(this, apply, DialogButtonKind.Primary);
        var subtitle = UnifiedDialogAppearance.Children<TextBlock>(frame).FirstOrDefault(text => text.Text == "PowerLeitstand PRO");
        if (subtitle is not null) subtitle.Text = adapter.Name + " · " + T("Administratorrechte erforderlich", "Administrator rights required");

        void Refresh()
        {
            Request = null;
            problem.Visibility = Visibility.Collapsed;
            copy.IsEnabled = apply.IsEnabled = false;
            var family = scope.SelectedIndex < 0 ? default : families[scope.SelectedIndex];
            rowV4.Visibility = family.V4 ? Visibility.Visible : Visibility.Collapsed;
            rowV6.Visibility = family.V6 ? Visibility.Visible : Visibility.Collapsed;
            var previous = new List<string>();
            if (family.V4) previous.Add("IPv4  " + FormatCurrent(current.StaticServers!, adapter.DnsV4));
            if (family.V6) previous.Add("IPv6  " + FormatCurrent(current.StaticServersV6!, adapter.DnsV6));
            before.Text = previous.Count == 0 ? T("Keine passende IP-Version verfügbar.", "No matching IP version available.")
                : string.Join(Environment.NewLine, previous);
            if (previous.Count == 0) return;
            var isManual = restore is null && mode.SelectedIndex == 1;
            var profile = mode.SelectedItem as NetworkDnsProfile;
            var v4 = restore is not null || isManual ? manualV4 : profile?.Ipv4 ?? "";
            var v6 = restore is not null || isManual ? manualV6 : profile?.Ipv6 ?? "";
            input.IsReadOnly = inputV6.IsReadOnly = !isManual;
            input.Text = v4;
            inputV6.Text = v6;
            description.Text = profile is not null ? T(profile.German, profile.English)
                : restore is not null ? T("Ein leeres Adressfeld steht für automatische Zuweisung.", "An empty address field means automatic assignment.")
                : isManual ? T("Mehrere Adressen durch Kommas trennen.", "Separate multiple addresses with commas.")
                : T("Windows bezieht die DNS-Server automatisch.", "Windows obtains DNS servers automatically.");
            if (isManual && ((family.V4 && string.IsNullOrWhiteSpace(v4)) || (family.V6 && string.IsNullOrWhiteSpace(v6)))) return;
            var request = new RepairNetworkRequest(adapter.Id, family.V4 ? v4 : null, Ipv4Only: family.V4 && !family.V6,
                DnsV6: family.V6 ? v6 : null, PreviousV4: family.V4 ? current.StaticServers : null,
                PreviousV6: family.V6 ? current.StaticServersV6 : null);
            try
            {
                var command = request.Prelude("dns-servers");
                var unchanged = request with { Dns = request.PreviousV4, DnsV6 = request.PreviousV6 };
                apply.IsEnabled = administrator && command != unchanged.Prelude("dns-servers");
            }
            catch (ArgumentException)
            {
                problem.Text = T("Prüfe, ob die Adressen zur jeweiligen IP-Version passen.", "Check that each address matches its IP version.");
                problem.Visibility = Visibility.Visible;
                return;
            }
            Request = request;
            copy.IsEnabled = true;
        }
        mode.SelectionChanged += (_, _) => Refresh();
        scope.SelectionChanged += (_, _) => Refresh();
        input.TextChanged += (_, _) => { if (!input.IsReadOnly && input.Text != manualV4) { manualV4 = input.Text; Refresh(); } };
        inputV6.TextChanged += (_, _) => { if (!inputV6.IsReadOnly && inputV6.Text != manualV6) { manualV6 = inputV6.Text; Refresh(); } };
        Refresh();
        apply.Click += (_, _) => { if (apply.IsEnabled && ElevationService.IsElevated()) DialogResult = true; };
        copy.Click += (_, _) =>
        {
            if (Request is null) return;
            try
            {
                System.Windows.Clipboard.SetText(RepairTool.Find("dns-servers")!.CommandFor(english, Request));
                copy.Content = T("Kopiert", "Copied");
            }
            catch (ExternalException) { copy.Content = T("Erneut versuchen", "Try again"); }
        };

        string FormatCurrent(string saved, string effective) => saved.Length == 0
            ? (effective.Length == 0 ? "" : effective + " · ") + T("Automatisch bezogen", "Obtained automatically")
            : saved + " · " + T("Manuell eingetragen", "Manually configured");

        ComboBox Picker(string name, string label, System.Collections.IEnumerable items)
        {
            var picker = new ComboBox
            {
                Name = name, Style = (Style)owner.FindResource(typeof(ComboBox)),
                ItemContainerStyle = (Style)owner.FindResource(typeof(ComboBoxItem)), ItemsSource = items,
                Background = fieldBrush, BorderBrush = fieldBorder
            };
            ScrollViewer.SetVerticalScrollBarVisibility(picker, ScrollBarVisibility.Hidden);
            System.Windows.Automation.AutomationProperties.SetName(picker, label);
            return picker;
        }

        TextBox AddressBox(string name, string ipVersion)
        {
            var box = new TextBox
            {
                Name = name, MaxLength = 300, Height = 29, Padding = new Thickness(8, 4, 8, 4),
                Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0)
            };
            box.SetResourceReference(TextBox.ForegroundProperty, "TextBrush");
            box.SetResourceReference(TextBox.CaretBrushProperty, "TextBrush");
            System.Windows.Automation.AutomationProperties.SetName(box, ipVersion + " " + T("DNS-Adressen", "DNS addresses"));
            return box;
        }

        DockPanel AddressRow(string ipVersion, TextBox box)
        {
            var caption = Label(ipVersion);
            caption.Width = 42;
            caption.VerticalAlignment = VerticalAlignment.Center;
            caption.Margin = new Thickness(0);
            var field = new Border
            {
                Child = box, Background = fieldBrush, BorderBrush = fieldBorder, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7)
            };
            box.IsKeyboardFocusWithinChanged += (_, _) =>
                field.BorderBrush = box.IsKeyboardFocusWithin ? (Brush)Resources["AccentBrush"] : fieldBorder;
            return new DockPanel { Margin = new Thickness(0, 6, 0, 0), Children = { caption, field } };
        }

        TextBlock Label(string text, double top = 0)
        {
            var label = DialogLayout.Note(text, palette, 12);
            label.Margin = new Thickness(0, top, 0, 5);
            label.FontWeight = FontWeights.SemiBold;
            label.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            return label;
        }

        Border Box(FrameworkElement body)
        {
            var border = new Border { Child = body, Padding = new Thickness(14, 10, 14, 12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
            border.SetResourceReference(Border.BackgroundProperty, "InputBrush");
            border.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            return border;
        }
    }
}
