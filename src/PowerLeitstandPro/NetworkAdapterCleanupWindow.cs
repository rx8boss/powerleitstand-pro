using System.ComponentModel;
using System.IO;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Border = System.Windows.Controls.Border;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using ListBox = System.Windows.Controls.ListBox;
using Path = System.Windows.Shapes.Path;

namespace PowerControlProWpf;

internal sealed class NetworkAdapterCleanupWindow : Window
{
    internal NetworkAdapterCleanupWindow(MainWindow owner, ThemePalette palette, bool english, bool administrator,
        IReadOnlyList<NetworkAdapterEntry>? fixture = null, Func<Task<IReadOnlySet<string>>>? fixtureDisableCheck = null)
    {
        string T(string de, string en) => english ? en : de;
        Title = T("Adapterverwaltung", "Manage adapters");
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Resources["DialogEnglish"] = english;

        var list = new ListBox { Name = "AdapterCleanupList", MinHeight = 92, MaxHeight = 222, BorderThickness = new Thickness(0) };
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Hidden);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        System.Windows.Automation.AutomationProperties.SetName(list, T("Netzwerkadapter und Status", "Network adapters and status"));
        var listMessage = DialogLayout.Note(T("Adapter werden geladen …", "Loading adapters …"), palette);
        listMessage.Name = "AdapterListMessage";
        listMessage.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        listMessage.VerticalAlignment = VerticalAlignment.Center;
        var surface = new Border
        {
            Child = new Grid { Children = { list, listMessage } }, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(4),
            Background = (Brush)owner.FindResource("InputBrush"),
            BorderBrush = (Brush)owner.FindResource("InputBorderBrush"),
            Margin = new Thickness(0, 8, 0, 4)
        };
        var description = DialogLayout.Note(T("Klicke auf eine Zeile für Details. Setze ein Häkchen zum Entfernen.",
            "Select a row for details. Tick the checkbox to mark it for removal."), palette);
        var status = DialogLayout.Note("", palette, 12);
        var checkStatus = DialogLayout.Note(T("Windows prüft im Hintergrund, welche Adapter sich deaktivieren lassen …",
            "Windows is checking which adapters can be disabled …"), palette, 11);
        checkStatus.Name = "AdapterDisableCheckStatus";
        checkStatus.Margin = new Thickness(0, 0, 0, 7);
        var selection = DialogLayout.Note("", palette, 11);
        selection.Name = "AdapterSelectionDetails";
        var selectionTitle = new TextBlock { Name = "AdapterDetailTitle", FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var reload = new Button { Content = T("Aktualisieren", "Refresh") };
        var close = new Button { Content = T("Schließen", "Close"), IsCancel = true };
        var removeLabel = new TextBlock { FontWeight = FontWeights.SemiBold };
        var remove = new Button { Name = "RemoveAdapterEntry", Content = removeLabel, IsEnabled = false };
        var toggle = new Button { Name = "ToggleAdapter", Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(toggle, Dock.Right);
        var details = new Border
        {
            Child = new DockPanel { Children = { toggle, DialogLayout.Stack(selectionTitle, selection) } },
            Padding = new Thickness(10, 5, 10, 6), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1), BorderBrush = surface.BorderBrush
        };
        var footer = DialogLayout.Stack(details, status, checkStatus, DialogLayout.Actions(reload, close, remove));
        status.Margin = new Thickness(0, 0, 0, 7);
        var frame = new Border();
        Content = frame;
        DialogLayout.Compose(this, frame, palette, Title, english, DialogLayout.Stack(description, surface), footer, preferredWidth: 600);
        list.Template = (ControlTemplate)FindResource("AdapterListTemplate");
        var rowStyle = (Style)FindResource("AdapterListRow");
        var checkStyle = (Style)FindResource("AdapterRemoveCheck");
        var markedBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Colors[3]));
        var routeBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Ready));
        var textBrush = (Brush)FindResource("UnifiedText");
        var accentBrush = (Brush)FindResource("UnifiedAccent");
        var mutedBrush = (Brush)FindResource("UnifiedMuted");
        Resources["AdapterOutline"] = surface.BorderBrush;
        Resources["AdapterSelectionSurface"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Colors[9]));
        Resources["AdapterTickBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Colors[4]));
        selectionTitle.Foreground = textBrush;
        details.Background = markedBrush;
        UnifiedDialogAppearance.Button(this, reload, DialogButtonKind.Primary);
        UnifiedDialogAppearance.Button(this, close, DialogButtonKind.Primary);
        UnifiedDialogAppearance.Button(this, remove, DialogButtonKind.Danger);
        UnifiedDialogAppearance.Button(this, toggle, DialogButtonKind.Primary);

        var checkedEntries = new HashSet<NetworkAdapterEntry>();
        var refreshRows = new List<Action>();
        IReadOnlySet<string>? disableCandidates = null;
        Task? disableCheck = null;
        bool reading = false, changing = false, closed = false;
        NetworkAdapterEntry? Selected() => (list.SelectedItem as ListBoxItem)?.Tag is NetworkAdapterEntry entry
            ? disableCandidates?.Contains(entry.InstanceId) == true ? entry with { DisableVerified = true } : entry
            : null;
        string RouteFamily(NetworkAdapterEntry entry) => entry.InternetV4 && entry.InternetV6 ? "IPv4 / IPv6"
            : entry.InternetV6 ? "IPv6" : "IPv4";
        string State(NetworkAdapterEntry entry) => entry switch
        {
            { InternetV4: true, InternetV6: true } => T("Internetroute (IPv4 und IPv6)", "Internet route (IPv4 and IPv6)"),
            { InternetV4: true } => T("Internetroute (IPv4)", "Internet route (IPv4)"),
            { InternetV6: true } => T("Internetroute (IPv6)", "Internet route (IPv6)"),
            { Present: false } => T("Nicht mehr vorhanden", "No longer present"),
            { Connection: AdapterConnection.Disabled } => T("Deaktiviert", "Disabled"),
            { Connection: AdapterConnection.Connected } => T("Verbunden", "Connected"),
            { Connection: AdapterConnection.Disconnected } => T("Nicht verbunden", "Disconnected"),
            _ => T("Verbindungsstatus unbekannt", "Connection status unknown")
        };

        string Purpose(NetworkAdapterEntry entry)
        {
            var name = entry.Name;
            bool Has(string text) => name.Contains(text, StringComparison.OrdinalIgnoreCase);
            if (Has("Bluetooth")) return T("Bluetooth · Netzwerkverbindung über Bluetooth.", "Bluetooth · Networking over Bluetooth.");
            if (Has("Hyper-V") || Has("VMware") || Has("VirtualBox")) return T("Virtueller Adapter · Netzwerk für virtuelle Maschinen.", "Virtual adapter · Networking for virtual machines.");
            if (Has("VPN") || Has("Wintun") || Has("WireGuard") || Has("TAP-Windows")) return T("VPN-Adapter · verschlüsselte Netzwerkverbindungen.", "VPN adapter · Encrypted network connections.");
            if (Has("WAN Miniport") || Has("WAN-Miniport")) return T("Windows-Systemadapter · unterstützt WAN- und Tunnelverbindungen.", "Windows system adapter · Supports WAN and tunnel connections.");
            if (Has("Virtual") || Has("Virtuell")) return T("Virtueller Adapter · Netzwerkverbindung für Windows oder Software.", "Virtual adapter · Networking for Windows or software.");
            if (entry.InterfaceType == (int)NetworkInterfaceType.Wireless80211 || Has("Wi-Fi") || Has("WLAN") || Has("Wireless") || Has("802.11"))
                return T("WLAN · drahtlose Netzwerkverbindung.", "Wi-Fi · Wireless network connection.");
            if ((NetworkInterfaceType)entry.InterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.GigabitEthernet || Has("Ethernet") || Has("LAN-Adapter"))
                return entry.InstanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)
                    ? T("USB-Ethernet · Netzwerk per Kabel über USB.", "USB Ethernet · Wired networking over USB.")
                    : T("Ethernet · kabelgebundene Netzwerkverbindung.", "Ethernet · Wired network connection.");
            return T("Windows meldet keinen eindeutigen Einsatzzweck.", "Windows does not identify a specific purpose.");
        }

        bool CanChange() => !reading && !changing && administrator && !owner.IsBusy && NetworkAdapterEntries.RemovalSupported;
        void UpdateActions()
        {
            var entry = Selected();
            list.IsEnabled = !reading && !changing;
            reload.IsEnabled = list.IsEnabled && disableCheck is null;
            foreach (var refreshRow in refreshRows) refreshRow();
            checkStatus.Visibility = disableCheck is null ? Visibility.Collapsed : Visibility.Visible;
            close.IsEnabled = !changing;
            remove.IsEnabled = CanChange() && checkedEntries.Count > 0;
            removeLabel.Text = checkedEntries.Count switch
            {
                0 => T("Adapter entfernen", "Remove adapters"),
                1 => T("1 Adapter entfernen", "Remove 1 adapter"),
                var count => T($"{count} Adapter entfernen", $"Remove {count} adapters")
            };
            System.Windows.Automation.AutomationProperties.SetName(remove, removeLabel.Text);
            details.Visibility = entry is null ? Visibility.Collapsed : Visibility.Visible;
            toggle.Visibility = entry is { CanEnable: true } or { CanDisable: true } ? Visibility.Visible : Visibility.Collapsed;
            toggle.IsEnabled = CanChange() && toggle.Visibility == Visibility.Visible;
            toggle.Content = entry?.CanEnable == true ? T("Wieder aktivieren", "Re-enable") : T("Deaktivieren", "Disable");
            if (entry is null) return;
            selectionTitle.Text = "Details · " + entry.Name;
            selectionTitle.ToolTip = entry.Name;
            var protection = entry.IsInternetRoute || entry.Connection == AdapterConnection.Connected
                ? T("Aktive Verbindung · geschützt", "Active connection · protected")
                : entry is { Present: true, CanEnable: false, CanDisable: false } && disableCheck is null
                    ? T("Geschützt · eine Änderung ist für diesen Adapter nicht freigegeben.", "Protected · changes are unavailable for this adapter.")
                    : "";
            selection.Text = string.Join("\n", new[] { Purpose(entry), protection }.Where(line => line.Length > 0));
            var routeNote = entry.IsInternetRoute ? T("Windows-Route zu einem Internet-Referenzziel. Die Prüfung sendet keine Daten.",
                "Windows route to an internet reference address. This check sends no data.") : null;
            selection.ToolTip = string.Join("\n", new[] { entry.Manufacturer, entry.InstanceId, routeNote }.Where(line => !string.IsNullOrEmpty(line)));
        }

        void AddRow(NetworkAdapterEntry entry)
        {
            var name = new TextBlock { Text = entry.Name, ToolTip = entry.Name, Foreground = textBrush, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
            var presence = State(entry);
            var detail = new TextBlock { Foreground = mutedBrush, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            var check = new CheckBox
            {
                Width = 30, Height = 30, VerticalAlignment = VerticalAlignment.Center, Style = checkStyle,
                Visibility = entry.CanRemove ? Visibility.Visible : Visibility.Collapsed,
                IsEnabled = administrator && NetworkAdapterEntries.RemovalSupported
            };
            var lockIcon = new Path
            {
                Name = "AdapterProtectionIcon", Width = 13, Height = 14, Stroke = mutedBrush, StrokeThickness = 1.15,
                Data = Geometry.Parse("M 2,6 L 2,4 A 4,4 0 0 1 10,4 L 10,6 M 0.5,6 H 11.5 V 13 H 0.5 Z M 6,8.5 V 10.5"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                ToolTip = T("Vor Entfernen geschützt", "Protected from removal")
            };
            var marker = new Grid { Width = 32, Margin = new Thickness(0, 0, 6, 0), Children = { check, lockIcon } };
            var badgeText = new TextBlock { Name = "AdapterRowBadge", FontSize = 11, FontWeight = FontWeights.SemiBold };
            var badge = new Border
            {
                Child = badgeText, Background = surface.Background, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1),
                Padding = new Thickness(7, 2, 7, 3), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(marker, Dock.Left);
            DockPanel.SetDock(badge, Dock.Right);
            var content = new DockPanel { Children = { marker, badge, DialogLayout.Stack(name, detail) } };
            var row = new ListBoxItem { Content = content, Tag = entry, Style = rowStyle };
            check.Checked += (_, _) => { checkedEntries.Add(entry); UpdateActions(); };
            check.Unchecked += (_, _) => { checkedEntries.Remove(entry); UpdateActions(); };
            System.Windows.Automation.AutomationProperties.SetName(check, T("Zum Entfernen auswählen: ", "Select for removal: ") + entry.Name);

            refreshRows.Add(() =>
            {
                var marked = checkedEntries.Contains(entry);
                var protectedEntry = !entry.CanRemove && !entry.CanEnable && !entry.CanDisable && disableCandidates?.Contains(entry.InstanceId) != true;
                row.Background = marked ? markedBrush : Brushes.Transparent;
                name.FontWeight = row.IsSelected ? FontWeights.SemiBold : FontWeights.Normal;
                lockIcon.Visibility = protectedEntry ? Visibility.Visible : Visibility.Collapsed;
                detail.Text = marked && row.IsSelected ? presence + T(" · Markiert", " · Marked") : presence;
                badgeText.Text = row.IsSelected ? T("Details geöffnet", "Details open")
                    : entry.IsInternetRoute ? "Internet · " + RouteFamily(entry)
                    : marked ? T("Markiert", "Marked") : protectedEntry ? T("Geschützt", "Protected") : "";
                badgeText.Foreground = !row.IsSelected && entry.IsInternetRoute ? routeBrush
                    : marked || row.IsSelected ? accentBrush : mutedBrush;
                badge.BorderBrush = row.IsSelected || marked || entry.IsInternetRoute ? badgeText.Foreground : surface.BorderBrush;
                badge.Visibility = badgeText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                System.Windows.Automation.AutomationProperties.SetName(row, entry.Name + " · " + presence +
                    (protectedEntry ? T(" · Vor Entfernen geschützt", " · Protected from removal") : "") +
                    (marked ? T(" · Zum Entfernen markiert", " · Marked for removal") : ""));
            });
            list.Items.Add(row);
        }

        async Task<bool> ReloadAsync()
        {
            if (reading || closed) return false;
            reading = true;
            disableCheck = null;
            disableCandidates = null;
            checkedEntries.Clear();
            refreshRows.Clear();
            list.Items.Clear();
            status.Text = "";
            listMessage.Text = T("Adapter werden geladen …", "Loading adapters …");
            listMessage.Visibility = Visibility.Visible;
            UpdateActions();
            try
            {
                var entries = fixture ?? await Task.Run(() => NetworkAdapterConnections.Attach(NetworkAdapterEntries.Read()));
                if (closed) return false;
                listMessage.Text = T("Windows meldet keine Netzwerkadapter.", "Windows reports no network adapters.");
                if (entries.Count > 0) listMessage.Visibility = Visibility.Collapsed;
                foreach (var entry in entries.OrderBy(item => !item.IsInternetRoute).ThenBy(item => item.Connection != AdapterConnection.Connected)
                    .ThenBy(item => !item.CanRemove).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
                    AddRow(entry);
                var count = entries.Count(item => item.CanRemove);
                status.Text = !NetworkAdapterEntries.RemovalSupported
                    ? T("Das Entfernen ist ab Windows 10 Version 2004 verfügbar.", "Removal is available from Windows 10 version 2004.")
                    : !administrator
                        ? T("Für Änderungen PowerLeitstand als Administrator öffnen.", "Open PowerLeitstand as administrator to make changes.")
                        : T($"Nicht vorhandene Adapter: {count}", $"Adapters not present: {count}");
                if (administrator && NetworkAdapterEntries.RemovalSupported && (fixture is null || fixtureDisableCheck is not null) &&
                    entries.Any(entry => entry is { Present: true, DeviceDisabled: false, Connection: AdapterConnection.Disconnected, IsInternetRoute: false }))
                {
                    var pending = fixtureDisableCheck?.Invoke() ?? Task.Run(async () =>
                    {
                        var candidates = await NetworkAdapterConnections.ReadDisableCandidatesAsync().ConfigureAwait(false);
                        return (IReadOnlySet<string>)NetworkAdapterConnections.Attach(entries, candidates).Where(entry => entry.CanDisable)
                            .Select(entry => entry.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    });
                    disableCheck = pending;
                    _ = ApplyDisableCheckAsync(pending);
                }
                return true;
            }
            catch (Exception error) when (error is Win32Exception or UnauthorizedAccessException or IOException)
            {
                listMessage.Text = T("Die Adapterliste konnte nicht geladen werden. ", "Could not load the adapter list. ") + UiText.Error(error, english);
                return false;
            }
            finally
            {
                reading = false;
                if (!closed) UpdateActions();
            }
        }

        async Task ApplyDisableCheckAsync(Task<IReadOnlySet<string>> pending)
        {
            IReadOnlySet<string>? candidates = null;
            try { candidates = await pending; }
            catch (Exception error) when (error is Win32Exception or UnauthorizedAccessException or IOException or InvalidOperationException) { }
            if (closed || disableCheck != pending) return;
            disableCandidates = candidates;
            disableCheck = null;
            UpdateActions();
        }

        async Task RemoveSelectedAsync()
        {
            if (!CanChange() || checkedEntries.Count == 0 || fixture is not null || !ElevationService.IsElevated()) return;
            var targets = list.Items.OfType<ListBoxItem>().Select(row => (NetworkAdapterEntry)row.Tag)
                .Where(checkedEntries.Contains).DistinctBy(entry => entry.InstanceId, StringComparer.OrdinalIgnoreCase).ToArray();
            var names = string.Join("\n\n", targets.Select(entry => $"{entry.Name}\n{entry.InstanceId}"));
            var question = T($"Ausgewählte Geräteeinträge entfernen? Die Treiberpakete bleiben verfügbar.\n\n{names}",
                $"Remove the selected device entries? The driver packages stay available.\n\n{names}");
            if (!Confirm(question)) return;
            var results = new List<(NetworkAdapterEntry Entry, int? Code, string? Error)>();
            foreach (var entry in targets)
            {
                status.Text = T($"Adapter {results.Count + 1} von {targets.Length} wird geprüft …", $"Checking adapter {results.Count + 1} of {targets.Length} …");
                var result = await RunAsync(() => NetworkAdapterEntries.RemoveAsync(entry));
                results.Add((entry, result.Code, result.Error));
            }
            var refreshed = await ReloadAsync();
            var remaining = list.Items.OfType<ListBoxItem>().Select(row => ((NetworkAdapterEntry)row.Tag).InstanceId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var removed = results.Count(item => item.Code == 0 && refreshed && !remaining.Contains(item.Entry.InstanceId));
            Finish(removed == results.Count ? T($"Entfernt: {removed} von {results.Count}.", $"Removed: {removed} of {results.Count}.")
                : T($"Entfernt: {removed} von {results.Count}. Details im Ergebnis.", $"Removed: {removed} of {results.Count}. See results for details."));
            if (removed == results.Count) return;
            var lines = results.Select(item => $"{item.Entry.Name}\n" + (item.Error ?? (item.Code switch
            {
                0 when refreshed && !remaining.Contains(item.Entry.InstanceId) => T("Entfernt.", "Removed."),
                _ => Outcome(item.Code)
            })));
            ThemedMessageBox.Show(this, string.Join("\n\n", lines), Title, MessageBoxButton.OK, MessageBoxImage.Warning, fitHeightToOwner: false);
        }

        async Task ToggleAsync()
        {
            if (!toggle.IsEnabled || !CanChange() || Selected() is not { } entry || fixture is not null || !ElevationService.IsElevated()) return;
            var enable = entry.CanEnable;
            if (!enable && !entry.CanDisable) return;
            var question = enable ? T($"„{entry.Name}“ wieder aktivieren?", $"Re-enable “{entry.Name}”?")
                : T($"„{entry.Name}“ deaktivieren? Bis zur erneuten Aktivierung steht dieser Adapter nicht zur Verfügung.",
                    $"Disable “{entry.Name}”? This adapter will be unavailable until re-enabled.");
            if (!Confirm(question)) return;
            status.Text = T("Windows prüft den Adapter …", "Windows is checking the adapter …");
            var result = await RunAsync(() => NetworkAdapterEntries.ChangeEnabledAsync(entry, enable));
            await ReloadAsync();
            var matches = list.Items.OfType<ListBoxItem>().Where(item => item.Tag is NetworkAdapterEntry candidate &&
                candidate.InstanceId.Equals(entry.InstanceId, StringComparison.OrdinalIgnoreCase)).ToArray();
            var row = matches is [var match] ? match : null;
            if (row is not null) list.SelectedItem = row;
            Finish(result.Error ?? (result.Code == 0 && row?.Tag is NetworkAdapterEntry current && current.DeviceDisabled == !enable
                ? enable ? T("Adapter wieder aktiviert.", "Adapter re-enabled.") : T("Adapter deaktiviert.", "Adapter disabled.")
                : Outcome(result.Code)));
        }

        bool Confirm(string question)
        {
            if (ThemedMessageBox.Show(this, question, Title, MessageBoxButton.YesNo, MessageBoxImage.Warning,
                fitHeightToOwner: false) != MessageBoxResult.Yes || !CanChange()) return false;
            changing = true;
            UpdateActions();
            return true;
        }

        void Finish(string message)
        {
            changing = false;
            status.Text = message;
            UpdateActions();
        }

        string Outcome(int? code) => code switch
        {
            null => T("Übersprungen. Der aktuelle Status erlaubt die Änderung nicht.", "Skipped. The current status does not allow the change."),
            0 => T("Windows meldet Erfolg. Die aktualisierte Liste bestätigt die Änderung noch nicht.", "Windows reported success. The refreshed list has not confirmed the change yet."),
            3010 => T("Windows benötigt einen Neustart, um die Änderung abzuschließen.", "Windows requires a restart to complete the change."),
            _ => T($"Änderung fehlgeschlagen. Windows-Code {code}.", $"Change failed. Windows code {code}.")
        };

        async Task<(int? Code, string? Error)> RunAsync(Func<Task<int?>> action)
        {
            try { return (await Task.Run(action), null); }
            catch (Exception error) when (error is Win32Exception or UnauthorizedAccessException or IOException or InvalidOperationException or NotSupportedException)
            {
                return (null, T("Änderung fehlgeschlagen. ", "Change failed. ") + UiText.Error(error, english));
            }
        }

        Loaded += async (_, _) => await ReloadAsync();
        list.SelectionChanged += (_, _) => UpdateActions();
        reload.Click += async (_, _) => await ReloadAsync();
        close.Click += (_, _) => Close();
        remove.Click += async (_, _) => await RemoveSelectedAsync();
        toggle.Click += async (_, _) => await ToggleAsync();
        Closing += (_, args) => args.Cancel = changing;
        Closed += (_, _) => closed = true;
        UpdateActions();
    }
}
