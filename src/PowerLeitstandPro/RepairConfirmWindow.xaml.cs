using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Border = System.Windows.Controls.Border;
using Brush = System.Windows.Media.Brush;

namespace PowerControlProWpf;

public partial class RepairConfirmWindow : Window
{
    private readonly bool _english;
    internal RepairNetworkRequest? NetworkRequest { get; private set; }
    internal string? NetworkConfiguration { get; }

    internal RepairConfirmWindow(string command, ThemePalette palette, bool english)
        : this(RepairTool.All.First(tool => string.Equals(tool.Command, command, StringComparison.OrdinalIgnoreCase)), palette, english) { }

    internal RepairConfirmWindow(RepairTool tool, ThemePalette palette, bool english, bool? administrator = null,
        DiagnosticReportStore? reports = null, IReadOnlyList<RepairNetworkAdapter>? networkAdapters = null, bool selectAdapter = false,
        string? tcpIpConfiguration = null)
    {
        InitializeComponent();
        _english = english;
        NetworkConfiguration = tool.Id == "tcpip-reset" ? tcpIpConfiguration ?? RepairNetworkActions.ReadTcpIpConfiguration(english) : null;
        var keys = new[] { "DialogBackground", "DialogText", "DialogSubtleText", "DialogAccent",
            "DialogBorder", "DialogButtonBackground", "DialogHover" };
        var colors = new[] { palette.Colors[0], palette.Colors[5], palette.Colors[6], palette.Colors[7],
            palette.Colors[8], palette.Colors[9], palette.Colors[10] };
        for (var index = 0; index < keys.Length; index++)
            Resources[keys[index]] = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colors[index]));

        CommandText.Text = tool.CommandFor(english);
        CommandText.VerticalScrollBarVisibility = (tool.IsReset || tool.Executable == "powershell.exe") ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        if (tool.IsNetworkAction)
        {
            CommandText.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            CommandText.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
        }
        CopyCaption.Text = english ? "Copy" : "Kopieren";
        CopyButton.Width = 92;
        ((Grid)CommandText.Parent).ColumnDefinitions[1].Width = GridLength.Auto;
        CancelButton.Style = (Style)FindResource("DialogButton");
        CancelCaption.Text = english ? "Close" : "Schließen";
        StartCaption.Text = tool.OpensWindow ? english ? "Open" : "Öffnen"
            : tool.IsNetworkReset ? english ? "Reset now" : "Jetzt zurücksetzen"
            : tool.IsRepair ? english ? "Start repair" : "Reparatur starten"
            : tool.Id == "dns" ? english ? "Flush cache" : "Cache leeren"
            : tool.Id == "dns-servers" ? english ? "Apply DNS settings" : "DNS übernehmen"
            : tool.Id == "dhcp-renew" ? english ? "Renew address" : "Adresse erneuern"
            : tool.Id == "adapter-restart" ? english ? "Restart adapter" : "Adapter neu starten"
            : english ? "Start check" : "Prüfung starten";

        var commandBox = (Border)((Grid)CommandText.Parent).Parent;
        commandBox.Margin = new Thickness(0, 0, 0, 8);
        commandBox.Height = (tool.IsReset || tool.Executable == "powershell.exe") ? 74 : 38;
        var titles = english
            ? new[] { "What does this command do?", "When to use it?", "When to avoid it?", "How it runs", "Notes", "Next step" }
            : new[] { "Was macht der Befehl?", "Wann verwenden?", "Wann nicht verwenden?", "Ausführungsart", "Hinweise", "Nächster Schritt" };
        var description = english ? tool.En : tool.De;
        var mode = tool.OpensWindow
            ? english ? "Opens a Windows window · Admin rights required · Windows 10 and 11"
                : "Öffnet ein Windows-Fenster · Adminrechte erforderlich · Windows 10 und 11"
            : english ? "Output in the app · Admin rights required · Windows 10 and 11"
                : "Ausgabe in der App · Adminrechte erforderlich · Windows 10 und 11";
        string[] texts = [description[0], description[1], description[2], mode, description[3], description[4]];
        if (tool.IsNetworkReset)
            texts[4] += english ? " Restart Windows after the reset." : " Starte Windows nach dem Reset neu.";

        Border Section(int index)
        {
            var heading = DialogLayout.Note(titles[index], palette, 13);
            heading.Foreground = (Brush)Resources["DialogAccent"];
            heading.FontWeight = FontWeights.SemiBold;
            heading.Margin = new Thickness(0, 0, 0, 3);
            var content = DialogLayout.Note(texts[index], palette, 12);
            content.Foreground = (Brush)Resources["DialogText"];
            return new Border
            {
                Background = (Brush)Resources["DialogBackground"], BorderBrush = (Brush)Resources["DialogBorder"],
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(9, 6, 9, 6),
                Margin = new Thickness(0, 0, 0, 8), Child = DialogLayout.Stack(heading, content)
            };
        }

        Grid Pair(int left, int right)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var second = Section(right);
            Grid.SetColumn(second, 2);
            row.Children.Add(Section(left));
            row.Children.Add(second);
            return row;
        }

        var canStart = administrator ?? ElevationService.IsElevated();
        var note = DialogLayout.Note(canStart
            ? tool.Id == "adapter-restart" ? (english ? "This briefly interrupts the selected adapter’s connection." : "Die Verbindung des gewählten Adapters wird kurz unterbrochen.")
                : english ? "The command starts after confirmation." : "Der Befehl startet nach deiner Bestätigung."
            : english ? "Start PowerLeitstand as administrator to run this tool. Use the administrator button in the main window."
                : "Starte PowerLeitstand als Administrator, um dieses Werkzeug auszuführen. Nutze dafür die Admin-Schaltfläche im Hauptfenster.",
            palette, 12);
        note.Margin = new Thickness(0, 0, 0, 8);
        FrameworkElement body;
        if (tool.IsNetworkReset)
        {
            var resetBody = DialogLayout.Stack(Section(0), Section(4));
            if (NetworkConfiguration is not null)
            {
                var configuration = DialogLayout.Note(NetworkConfiguration, palette, 12);
                configuration.Name = "TcpIpConfiguration";
                var configurationBox = Section(3);
                var heading = DialogLayout.Note(english ? "Current IP and DNS configuration" : "Aktuelle IP- und DNS-Konfiguration", palette, 13);
                heading.Foreground = (Brush)Resources["DialogAccent"];
                heading.FontWeight = FontWeights.SemiBold;
                heading.Margin = new Thickness(0, 0, 0, 4);
                configurationBox.Child = DialogLayout.Stack(heading, configuration);
                resetBody.Children.Add(configurationBox);
            }
            resetBody.Children.Add(DialogLayout.Take(commandBox));
            body = resetBody;
        }
        else
        {
            body = tool.NeedsAdapter
                ? DialogLayout.Stack(BuildAdapterFields(), commandBox, Section(0), Pair(1, 2), Pair(3, 4), Section(5))
                : DialogLayout.Stack(commandBox, Section(0), Pair(1, 2), Pair(3, 4), Section(5));
        }
        var footer = DialogLayout.Stack(note, DialogLayout.Actions(CancelButton, StartButton));
        FrameworkElement? toolbar = null;
        if (reports is not null)
        {
            var details = body;
            var history = DiagnosticReportHistory.Build(this, palette, english, reports, tool.Title(english));
            history.Visibility = Visibility.Collapsed;
            var historyButton = new System.Windows.Controls.Button
            {
                Name = "DiagnosticShowHistory", Content = english ? "Reports" : "Protokolle",
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right, MinWidth = 100,
                Height = 30, Margin = new Thickness(0, 0, 0, 8)
            };
            historyButton.Click += (_, _) =>
            {
                var showHistory = history.Visibility == Visibility.Collapsed;
                historyButton.Content = showHistory ? (english ? "Back to details" : "Zurück zu Details")
                    : (english ? "Reports" : "Protokolle");
                history.Visibility = showHistory ? Visibility.Visible : Visibility.Collapsed;
                details.Visibility = note.Visibility = StartButton.Visibility = showHistory ? Visibility.Collapsed : Visibility.Visible;
                StartButton.IsDefault = canStart && !tool.IsReset && !tool.IsNetworkReset && !tool.NeedsAdapter && !showHistory;
            };
            toolbar = historyButton;
            body = DialogLayout.Stack(details, history);
        }
        DialogLayout.Compose(this, DialogFrame, palette, tool.Title(english), english, body, footer, preferredWidth: 640, toolbar: toolbar);

        UnifiedDialogAppearance.Button(this, StartButton, DialogButtonKind.Primary);
        StartButton.IsEnabled = canStart && (!tool.NeedsAdapter || NetworkRequest is not null);
        if (!canStart)
        {
            StartButton.Background = System.Windows.Media.Brushes.Gray;
            StartButton.BorderBrush = System.Windows.Media.Brushes.Gray;
        }
        if (tool.IsReset || tool.IsNetworkReset || tool.NeedsAdapter || !canStart) StartButton.IsDefault = false;

        FrameworkElement BuildAdapterFields()
        {
            var dns = tool.Id == "dns-servers";
            string T(string de, string en) => english ? en : de;
            var adapterLabel = T("LAN- oder WLAN-Adapter auswählen", "Select an Ethernet or Wi-Fi adapter");
            var picker = new System.Windows.Controls.ComboBox
            {
                Name = "RepairNetworkAdapterPicker", DisplayMemberPath = nameof(RepairNetworkAdapter.Name),
                MinHeight = 30, Margin = new Thickness(0, 4, 0, 4)
            };
            System.Windows.Automation.AutomationProperties.SetName(picker, adapterLabel);
            try
            {
                picker.ItemsSource = (networkAdapters ?? RepairNetworkAdapter.Read().ToArray())
                    .Where(item => tool.Id != "dhcp-renew" || item.Dhcp).OrderBy(item => item.Name).ToArray();
            }
            catch (System.Net.NetworkInformation.NetworkInformationException) { }
            Loaded += (_, _) =>
            {
                if (Owner is null) return;
                foreach (var key in new[] { "InputBrush", "TextBrush", "AccentBrush", "CardBrush", "HoverBrush" })
                    if (Owner.TryFindResource(key) is Brush brush) Resources[key] = brush;
                if (Owner.TryFindResource(typeof(System.Windows.Controls.ComboBox)) is Style style) picker.Style = style;
                if (Owner.TryFindResource(typeof(ComboBoxItem)) is Style itemStyle) picker.ItemContainerStyle = itemStyle;
            };
            var current = DialogLayout.Note(picker.Items.Count == 0
                ? tool.Id == "dhcp-renew" ? T("Kein verbundener DHCP-Adapter gefunden.", "No connected DHCP adapter found.")
                    : T("Kein LAN- oder WLAN-Adapter gefunden.", "No Ethernet or Wi-Fi adapter found.")
                : T("Bitte einen Adapter auswählen.", "Select an adapter."), palette, 12);
            var automatic = new System.Windows.Controls.CheckBox
            {
                Name = "RepairDnsAutomatic", Content = T("DNS-Server automatisch beziehen", "Obtain DNS servers automatically"),
                Foreground = (Brush)Resources["DialogText"], Margin = new Thickness(0, 6, 0, 4)
            };
            var serverLabel = T("Neue DNS-Adressen (durch Komma getrennt)", "New DNS addresses (comma separated)");
            var servers = new System.Windows.Controls.TextBox
            {
                Name = "RepairDnsServers", MaxLength = 300, MinHeight = 28, Padding = new Thickness(6, 4, 6, 4),
                Foreground = (Brush)Resources["DialogText"], Background = (Brush)Resources["DialogBackground"],
                BorderBrush = (Brush)Resources["DialogBorder"], CaretBrush = (Brush)Resources["DialogText"]
            };
            System.Windows.Automation.AutomationProperties.SetName(servers, serverLabel);
            var invalid = DialogLayout.Note(T("Gib gültige IPv4- oder IPv6-Adressen ein.", "Enter valid IPv4 or IPv6 addresses."), palette, 12);
            invalid.Visibility = Visibility.Collapsed;

            void Refresh()
            {
                NetworkRequest = null;
                StartButton.IsEnabled = CopyButton.IsEnabled = false;
                CommandText.Text = T("Befehl erscheint nach der Auswahl.", "The command appears after selection.");
                invalid.Visibility = Visibility.Collapsed;
                servers.IsEnabled = automatic.IsChecked != true;
                if (picker.SelectedItem is not RepairNetworkAdapter item) return;
                current.Text = dns ? T("Aktuelle DNS-Server: ", "Current DNS servers: ") +
                    (item.Dns.Length > 0 ? item.Dns : T("Keine angezeigt", "None shown"))
                    : item.Dhcp ? T("DHCP aktiv", "DHCP enabled") : item.Name;
                if (dns && automatic.IsChecked != true && string.IsNullOrWhiteSpace(servers.Text)) return;
                var request = new RepairNetworkRequest(item.Id, dns ? automatic.IsChecked == true ? "" : servers.Text : null);
                try { CommandText.Text = tool.CommandFor(english, request); }
                catch (ArgumentException) { invalid.Visibility = Visibility.Visible; return; }
                NetworkRequest = request;
                CopyButton.IsEnabled = true;
                StartButton.IsEnabled = canStart;
            }
            picker.SelectionChanged += (_, _) => Refresh();
            automatic.Checked += (_, _) => Refresh();
            automatic.Unchecked += (_, _) => Refresh();
            servers.TextChanged += (_, _) => Refresh();
            if (selectAdapter && picker.Items.Count == 1) picker.SelectedIndex = 0;
            Refresh();
            var label = DialogLayout.Note(adapterLabel, palette, 13);
            return new Border
            {
                Background = (Brush)Resources["DialogBackground"], BorderBrush = (Brush)Resources["DialogBorder"],
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(9, 6, 9, 6),
                Margin = new Thickness(0, 0, 0, 8),
                Child = dns ? DialogLayout.Stack(label, picker, current, automatic, DialogLayout.Note(serverLabel, palette, 12), servers, invalid)
                    : DialogLayout.Stack(label, picker, current)
            };
        }
    }

    private void DragDialog(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }

    private void CancelDialog(object sender, RoutedEventArgs e) => DialogResult = false;
    private void StartDialog(object sender, RoutedEventArgs e)
    {
        if (StartButton.IsEnabled && ElevationService.IsElevated()) DialogResult = true;
    }

    private void CopyCommand(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(CommandText.Text);
            CopyCaption.Text = _english ? "Copied" : "Kopiert";
        }
        catch (ExternalException)
        {
            CopyCaption.Text = _english ? "Try again" : "Erneut";
        }
    }
}
