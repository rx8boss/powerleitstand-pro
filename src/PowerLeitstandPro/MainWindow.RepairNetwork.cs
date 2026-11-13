using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace PowerControlProWpf;

public partial class MainWindow
{
    private bool? _networkViewEnglish;
    private int _networkTab;
    private Guid? _networkAdapterId;
    private string _networkHost = "example.com";
    private string _networkPort = "443";
    private bool _networkTesting;
    private StackPanel? _networkTabBody;
    private ComboBox? _networkPicker;
    private RepairNetworkAdapter? SelectedNetworkAdapter => (_networkPicker?.SelectedItem as ComboBoxItem)?.Tag as RepairNetworkAdapter;
    private string DnsBackupFolder => Path.Combine(_logDirectory, "DnsBackup");

    private string NetworkText(string de, string en) => _english ? en : de;

    private void BuildNetworkView()
    {
        _networkViewEnglish = _english;
        RepairNetworkPanel.Children.Clear();
        var tabs = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 8) };
        var labels = new[] { NetworkText("Diagnose", "Diagnostics"), NetworkText("Reparatur", "Repair"), NetworkText("Erweitert", "Advanced") };
        for (int index = 0; index < labels.Length; index++)
        {
            int tab = index;
            var button = NetworkButton(labels[index], () =>
            {
                _networkTab = tab;
                foreach (Button item in tabs.Children)
                    SetNetworkTabStyle(item, Equals(item.Tag, tab));
                BuildNetworkTab();
            });
            button.Tag = index;
            button.Margin = new Thickness(0, 0, index == 2 ? 0 : 8, 0);
            SetNetworkTabStyle(button, index == _networkTab);
            tabs.Children.Add(button);
        }
        RepairNetworkPanel.Children.Add(tabs);

        var selection = new Grid();
        selection.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        selection.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        selection.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        var caption = NetworkLabel(NetworkText("Netzwerkadapter", "Network adapter"));
        caption.FontWeight = FontWeights.SemiBold;
        caption.Margin = new Thickness(0, 0, 14, 0);
        var picker = new ComboBox { Name = "NetworkAdapterPicker", Margin = new Thickness(0, 0, 14, 0), MinWidth = 0 };
        System.Windows.Automation.AutomationProperties.SetName(picker, caption.Text);
        var status = NetworkLabel("");
        status.SetResourceReference(TextBlock.ForegroundProperty, "ReadyBrush");
        Grid.SetColumn(picker, 1);
        Grid.SetColumn(status, 2);
        selection.Children.Add(caption);
        selection.Children.Add(picker);
        selection.Children.Add(status);
        RepairNetworkPanel.Children.Add(NetworkBox(selection, new Thickness(0, 0, 0, 8)));
        var adapters = ReadNetworkAdapters();
        foreach (var adapter in adapters)
        {
            var connected = adapter.Connected ? NetworkText("Verbunden", "Connected") : NetworkText("Getrennt", "Disconnected");
            picker.Items.Add(new ComboBoxItem { Content = adapter.Name + " · " + connected, Tag = adapter });
        }
        _networkPicker = picker;
        _networkTabBody = new StackPanel();
        RepairNetworkPanel.Children.Add(_networkTabBody);
        void UpdateSelection()
        {
            var adapter = SelectedNetworkAdapter;
            _networkAdapterId = adapter?.Id;
            status.Text = adapter is null ? NetworkText("Kein LAN- oder WLAN-Adapter gefunden", "No Ethernet or Wi-Fi adapter found")
                : string.Join(" · ", new[]
                {
                    adapter.Dhcp ? NetworkText("IPv4 per DHCP", "IPv4 via DHCP") : adapter.Ipv4.Length > 0 ? "IPv4" : "",
                    adapter.HasIpv6 ? NetworkText("IPv6 verfügbar", "IPv6 available") : ""
                }.Where(value => value.Length > 0));
            BuildNetworkTab();
        }
        var previous = Array.FindIndex(adapters, adapter => adapter.Id == _networkAdapterId);
        picker.SelectedIndex = previous >= 0 ? previous : adapters.Length > 0 ? 0 : -1;
        picker.SelectionChanged += (_, _) => UpdateSelection();
        UpdateSelection();
    }

    private static RepairNetworkAdapter[] ReadNetworkAdapters()
    {
        try { return RepairNetworkAdapter.Read().OrderByDescending(adapter => adapter.Connected).ThenBy(adapter => adapter.Name).ToArray(); }
        catch (System.Net.NetworkInformation.NetworkInformationException) { return []; }
    }

    private void BuildNetworkTab()
    {
        var body = _networkTabBody!;
        body.Children.Clear();
        var adapter = SelectedNetworkAdapter;
        if (_networkTab == 0)
        {
            BuildNetworkDiagnostics(body, adapter);
        }
        else if (_networkTab == 1)
        {
            var actions = new StackPanel();
            actions.Children.Add(NetworkHeading(NetworkText("Netzwerkreparatur", "Network repair")));
            AddTool(actions, "dns");
            actions.Children.Add(NetworkButton(NetworkText("DNS-Server ändern", "Change DNS servers"),
                async () => await ChangeNetworkDnsAsync(adapter!), requiresAdmin: true,
                available: adapter is { Index: > 0 } or { Ipv6Index: > 0 }));
            if (adapter is not null && NetworkDnsBackup.Exists(DnsBackupFolder, adapter.Id))
                actions.Children.Add(NetworkButton(NetworkText("DNS-Einstellung wiederherstellen", "Restore DNS settings"),
                    async () => await ChangeNetworkDnsAsync(adapter, restore: true), requiresAdmin: true,
                    available: adapter.Index > 0 || adapter.Ipv6Index > 0));
            AddTool(actions, "dhcp-renew", adapter, adapter?.Dhcp == true);
            AddTool(actions, "adapter-restart", adapter, adapter is not null);
            body.Children.Add(NetworkBox(actions));
        }
        else
        {
            var columns = new Grid();
            columns.ColumnDefinitions.Add(new ColumnDefinition());
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            columns.ColumnDefinitions.Add(new ColumnDefinition());
            var actions = new StackPanel();
            actions.Children.Add(NetworkHeading(NetworkText("Erweiterte Netzwerkdiagnose", "Advanced network diagnostics")));
            AddTool(actions, "network-diagnostics");
            AddTool(actions, "network-services");
            actions.Children.Add(NetworkButton(NetworkText("Adapterverwaltung", "Manage adapters"), () =>
            {
                if (IsBusy || _networkTesting) return;
                new NetworkAdapterCleanupWindow(this, ThemePalette.For(SelectedTheme()), _english, IsAdmin()).ShowDialog();
            }));
            columns.Children.Add(NetworkBox(actions));
            var resets = new StackPanel();
            resets.Children.Add(NetworkHeading(NetworkText("Netzwerk zurücksetzen", "Reset network")));
            AddTool(resets, "winsock-reset");
            AddTool(resets, "tcpip-reset");
            var resetBox = NetworkBox(resets);
            Grid.SetColumn(resetBox, 2);
            columns.Children.Add(resetBox);
            body.Children.Add(columns);
        }
        UpdateNetworkActions();

        void AddTool(StackPanel panel, string id, RepairNetworkAdapter? target = null, bool available = true)
        {
            var tool = RepairTool.Find(id)!;
            panel.Children.Add(NetworkButton(tool.Title(_english), async () => await StartRepairAsync(tool, target),
                requiresAdmin: true, available: available));
        }
    }

    private void BuildNetworkDiagnostics(StackPanel body, RepairNetworkAdapter? adapter)
    {
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        var details = new StackPanel();
        details.Children.Add(NetworkHeading(NetworkText("Verbindung und DNS", "Connection and DNS")));
        var values = new Grid();
        values.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star) });
        values.ColumnDefinitions.Add(new ColumnDefinition());
        var rows = new[]
        {
            ("IPv4", adapter?.Ipv4),
            (NetworkText("Standardgateway", "Default gateway"), adapter?.Gateway),
            ("DNS (IPv4)", adapter?.DnsV4), ("DNS (IPv6)", adapter?.DnsV6)
        };
        for (int index = 0; index < rows.Length; index++)
        {
            var (label, value) = rows[index];
            values.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var key = NetworkLabel(label);
            var text = NetworkLabel(string.IsNullOrEmpty(value) ? NetworkText("Nicht eingetragen", "Not configured") : value);
            key.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
            key.Margin = text.Margin = new Thickness(0, 0, 6, 4);
            Grid.SetRow(key, index);
            Grid.SetRow(text, index);
            Grid.SetColumn(text, 1);
            values.Children.Add(key);
            values.Children.Add(text);
        }
        details.Children.Add(values);
        var result = new StackPanel();
        var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        buttons.Children.Add(NetworkButton(NetworkText("Details anzeigen", "Show details"), () => ShowNetworkDetails(adapter!, rows, result), available: adapter is not null));
        buttons.Children.Add(NetworkButton(NetworkText("WLAN-Bericht öffnen", "Open Wi-Fi report"), async () => await OpenWlanReportAsync(),
            requiresAdmin: true, available: _networkPicker!.Items.OfType<ComboBoxItem>().Any(item => item.Tag is RepairNetworkAdapter { Wireless: true })));
        details.Children.Add(buttons);
        columns.Children.Add(NetworkBox(details));

        var test = new StackPanel();
        test.Children.Add(NetworkHeading(NetworkText("Verbindung testen", "Test connection")));
        var target = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
        var labelTarget = NetworkLabel(NetworkText("Ziel", "Target"));
        labelTarget.Margin = new Thickness(0, 0, 8, 0);
        var port = NetworkInput("Port", _networkPort, 5);
        port.Width = 65;
        port.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(port, Dock.Right);
        var host = NetworkInput(labelTarget.Text, _networkHost, 253);
        target.Children.Add(labelTarget);
        target.Children.Add(port);
        target.Children.Add(host);
        test.Children.Add(target);
        host.TextChanged += (_, _) => _networkHost = host.Text;
        port.TextChanged += (_, _) => _networkPort = port.Text;
        var testActions = new WrapPanel();
        var available = adapter is { Connected: true } && (adapter.Index > 0 || adapter.Ipv6Index > 0);
        testActions.Children.Add(NetworkButton(NetworkText("DNS testen", "Test DNS"), async () => await RunNetworkTestAsync(adapter!, result, true), true, available));
        testActions.Children.Add(NetworkButton(NetworkText("Verbindung testen", "Test connection"), async () => await RunNetworkTestAsync(adapter!, result, false), true, available));
        test.Children.Add(testActions);
        var resultBox = NetworkBox(result);
        resultBox.MinHeight = 45;
        result.Children.Add(NetworkLabel(NetworkText("Der Test läuft nach deinem Klick.", "The test runs when you click its button.")));
        test.Children.Add(resultBox);
        var testBox = NetworkBox(test);
        Grid.SetColumn(testBox, 2);
        columns.Children.Add(testBox);
        body.Children.Add(columns);
    }

    private void ShowNetworkDetails(RepairNetworkAdapter adapter, (string Label, string? Value)[] rows, StackPanel result)
    {
        var report = string.Join(Environment.NewLine,
            new[] { adapter.Name, adapter.Connected ? NetworkText("Verbunden", "Connected") : NetworkText("Getrennt", "Disconnected") }
            .Concat(rows.Select(row => row.Label + ": " + (string.IsNullOrEmpty(row.Value) ? "—" : row.Value)))
            .Concat(result.Children.OfType<TextBlock>().Select(text => text.Text)));
        var title = NetworkText("Netzwerkdiagnose", "Network diagnostics");
        var summary = new RepairResultSummary(title, NetworkText("Bericht zum gewählten Adapter", "Report for the selected adapter"),
            adapter.Name, true, false, Output: report) { ShowFullOutput = true };
        new RepairResultWindow(summary, ThemePalette.For(SelectedTheme()), _english) { Owner = this }.ShowDialog();
    }

    private async Task RunNetworkTestAsync(RepairNetworkAdapter adapter, StackPanel result, bool dns)
    {
        if (_networkTesting || IsBusy || !IsAdmin()) return;
        var host = _networkHost.Trim();
        var port = 0;
        var hostType = Uri.CheckHostName(host);
        if (hostType == UriHostNameType.Unknown || (dns && hostType != UriHostNameType.Dns))
        {
            SetNetworkTestResult(result, dns ? "dns" : "tcp", NetworkText("Gib einen gültigen Hostnamen ein.", "Enter a valid host name."), "WarningBrush");
            return;
        }
        if (!dns && (!int.TryParse(_networkPort, out port) || port is < 1 or > 65535))
        {
            SetNetworkTestResult(result, "tcp", NetworkText("Gib einen Port zwischen 1 und 65535 ein.", "Enter a port between 1 and 65535."), "WarningBrush");
            return;
        }
        var key = dns ? "dns" : "tcp";
        _networkTesting = true;
        UpdateNetworkActions();
        SetNetworkTestResult(result, key, NetworkText("Test läuft …", "Test running …"), "SubtleTextBrush");
        try
        {
            var current = ReadNetworkAdapters().FirstOrDefault(item => item.Id == adapter.Id && item.Connected)
                ?? throw new InvalidOperationException(NetworkText("Der gewählte Adapter ist nicht mehr verbunden.", "The selected adapter is no longer connected."));
            if (dns)
            {
                var status = await Task.Run(() => NetworkConnectionTest.QueryDns(host, current));
                if (status != 0)
                    throw new InvalidOperationException(NetworkText($"DNS-Auflösung fehlgeschlagen (Code {status}).", $"DNS resolution failed (code {status})."));
            }
            else
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await NetworkConnectionTest.TestTcpAsync(host, port, current, timeout.Token);
            }
            var success = dns ? NetworkText($"✓ DNS-Antwort für {host} erhalten", $"✓ DNS response received for {host}")
                : NetworkText($"✓ TCP-Verbindung zu {host} auf Port {port} möglich", $"✓ TCP connection to {host} on port {port} succeeded");
            SetNetworkTestResult(result, key, success, "ReadyBrush");
        }
        catch (OperationCanceledException)
        {
            SetNetworkTestResult(result, key, NetworkText("Zeitlimit für die Verbindung erreicht.", "Connection timed out."), "WarningBrush");
        }
        catch (Exception error) when (error is SocketException or ArgumentException or InvalidOperationException)
        {
            SetNetworkTestResult(result, key, UiText.Error(error, _english), "WarningBrush");
        }
        finally
        {
            _networkTesting = false;
            UpdateNetworkActions();
        }
    }

    private static void SetNetworkTestResult(StackPanel result, string key, string text, string brush)
    {
        var line = result.Children.OfType<TextBlock>().FirstOrDefault(item => Equals(item.Tag, key));
        if (line is null)
        {
            if (result.Children.Count == 1 && ((TextBlock)result.Children[0]).Tag is null) result.Children.Clear();
            line = NetworkLabel("");
            line.Tag = key;
            result.Children.Add(line);
        }
        line.Text = text;
        line.SetResourceReference(TextBlock.ForegroundProperty, brush);
    }

    private async Task ChangeNetworkDnsAsync(RepairNetworkAdapter adapter, bool restore = false)
    {
        if (IsBusy || !IsAdmin()) return;
        try
        {
            var current = NetworkDnsBackup.Capture(adapter);
            var previous = restore ? NetworkDnsBackup.Read(DnsBackupFolder, adapter.Id) : null;
            if (restore && previous is null) return;
            var dialog = new NetworkDnsWindow(this, ThemePalette.For(SelectedTheme()), _english, IsAdmin(), adapter, current, previous);
            if (dialog.ShowDialog() != true || dialog.Request is not { } request || IsBusy) return;
            var stillPresent = ReadNetworkAdapters().FirstOrDefault(item => item.Id == adapter.Id)
                ?? throw new InvalidOperationException(NetworkText("Der gewählte Adapter ist nicht mehr verfügbar.", "The selected adapter is no longer available."));
            var latest = NetworkDnsBackup.Capture(stillPresent);
            if ((request.Dns is not null && latest.StaticServers != request.PreviousV4) ||
                (request.DnsV6 is not null && latest.StaticServersV6 != request.PreviousV6))
                throw new InvalidOperationException(NetworkText("Die DNS-Einstellung wurde inzwischen geändert. Öffne den Dialog erneut.", "DNS settings changed in the meantime. Open the dialog again."));
            if (!restore) latest.SaveBefore(DnsBackupFolder, request);
            await StartRepairAsync(RepairTool.Find("dns-servers")!, request: request);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or
            JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            ThemedMessageBox.Show(this, UiText.Error(error, _english), NetworkText("DNS-Einstellung konnte nicht übernommen werden", "Could not apply DNS settings"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task OpenWlanReportAsync()
    {
        if (IsBusy || _networkTesting || !IsAdmin()) return;
        _networkTesting = true;
        UpdateNetworkActions();
        try
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"), "wlan show wlanreport")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var process = Process.Start(start) ?? throw new InvalidOperationException("netsh.exe");
            var output = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            var report = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "WlanReport", "wlan-report-latest.html");
            if (process.ExitCode != 0 || !File.Exists(report))
                throw new InvalidOperationException((await output).Trim());
            _ = await output;
            using var browser = Process.Start(new ProcessStartInfo(report) { UseShellExecute = true });
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ThemedMessageBox.Show(this, UiText.Error(error, _english), NetworkText("WLAN-Bericht", "Wi-Fi report"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _networkTesting = false;
            UpdateNetworkActions();
        }
    }

    private void UpdateNetworkActions()
    {
        if (_networkTabBody is null) return;
        foreach (var button in UnifiedDialogAppearance.Children<Button>(_networkTabBody))
            if (button.Tag is ValueTuple<bool, bool> flags)
                button.IsEnabled = flags.Item2 && (!flags.Item1 || IsAdmin() && !IsBusy && !_networkTesting);
    }

    private Button NetworkButton(string title, Action action, bool requiresAdmin = false, bool available = true)
    {
        var button = new Button { Content = title, Tag = (requiresAdmin, available), Style = (Style)FindResource("SmallButton"),
            Height = 31, Margin = new Thickness(0, 0, 6, 5), Padding = new Thickness(10, 0, 10, 0),
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left };
        button.Click += (_, _) => { if (button.IsEnabled) action(); };
        return button;
    }

    private static void SetNetworkTabStyle(Button button, bool selected)
    {
        button.FontWeight = selected ? FontWeights.Bold : FontWeights.Normal;
        button.SetResourceReference(Button.BackgroundProperty, selected ? "HoverBrush" : "InputBrush");
        button.SetResourceReference(Button.BorderBrushProperty, selected ? "AccentBrush" : "InputBorderBrush");
        System.Windows.Automation.AutomationProperties.SetItemStatus(button, selected ? "✓" : "");
    }

    private static TextBlock NetworkLabel(string text)
    {
        var label = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        return label;
    }

    private static TextBlock NetworkHeading(string text)
    {
        var label = NetworkLabel(text);
        label.FontSize = 16;
        label.FontWeight = FontWeights.Bold;
        label.Margin = new Thickness(0, 0, 0, 6);
        label.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        return label;
    }

    private static Border NetworkBox(FrameworkElement child, Thickness margin = default)
    {
        var box = new Border { Child = child, Margin = margin, Padding = new Thickness(10, 7, 10, 7),
            CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1) };
        box.SetResourceReference(Border.BackgroundProperty, "InputBrush");
        box.SetResourceReference(Border.BorderBrushProperty, "InputBorderBrush");
        return box;
    }

    private static TextBox NetworkInput(string label, string text, int maxLength)
    {
        var box = new TextBox { Text = text, MaxLength = maxLength, Height = 30, Padding = new Thickness(8, 4, 8, 4) };
        box.SetResourceReference(TextBox.BackgroundProperty, "InputBrush");
        box.SetResourceReference(TextBox.ForegroundProperty, "TextBrush");
        box.SetResourceReference(TextBox.BorderBrushProperty, "AccentBrush");
        box.SetResourceReference(TextBox.CaretBrushProperty, "TextBrush");
        System.Windows.Automation.AutomationProperties.SetName(box, label);
        return box;
    }
}
