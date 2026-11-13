using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class RepairToolsTests
{
    internal static int Run(string output)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var tools = RepairTool.All;
        Check(tools.Count == 24 && tools.GroupBy(tool => tool.Group).OrderBy(group => group.Key)
            .Select(group => group.Count()).SequenceEqual([8, 4, 4, 8]), "Twenty-four tools in four categories");
        Check(tools.Select(tool => tool.Id).Distinct().Count() == tools.Count, "Unique tool identifiers");
        Check(tools.Count(tool => tool.OpensWindow) == 4, "Four tools open Windows windows");
        Check(RepairTool.Find("disk")!.Arguments.StartsWith(Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\')), "CHKDSK uses Windows drive");
        Check(RepairTool.Find("update-dism")!.Command.Contains("/RestoreHealth") &&
            !RepairTool.Find("update-dism")!.Command.Contains("scannow"), "Combined operation includes DISM only");
        Check(!RepairResultSummary.Create("scan", 0, false, "", "", TimeSpan.Zero, false).CanRestart, "Diagnostics do not offer a restart");
        Check(RepairResultSummary.Create("disk", 1, false, "", "", TimeSpan.Zero, false).IsSuccess, "CHKDSK code 1 reports repaired errors");
        RepairNetworkActionTests.Run();
        foreach (var reset in tools.Where(tool => tool.IsNetworkReset))
        {
            var success = RepairResultSummary.Create(reset.Id, 0, false, "fixture", "", TimeSpan.Zero, false);
            var failure = RepairResultSummary.Create(reset.Id, 1, false, "fixture", "", TimeSpan.Zero, false);
            Check(success.IsSuccess && success.Message.Contains("neu") && !success.CanRestart && !failure.IsSuccess,
                "Reset results distinguish errors and explain the manual restart");
        }
        const string tcpIpFixture = "Ethernet (Test) · DHCP\nIPv4: 192.0.2.20/24\nIPv6: 2001:db8::20/64\nGateway: 192.0.2.1\nDNS: 192.0.2.53";
        var oldAdapter = new NetworkAdapterEntry(@"USB\VID_0000&PID_0001\TEST01", "USB-LAN-Adapter (Test)", false);
        var oldAdapter2 = new NetworkAdapterEntry(@"USB\VID_0000&PID_0001\TEST05", oldAdapter.Name, false);
        var presentAdapter = new NetworkAdapterEntry(@"PCI\VEN_0000&DEV_0001\TEST02", "Ethernet (Test)", true,
            DeviceDisabled: false, Connection: AdapterConnection.Connected, InternetV4: true);
        var wireless = new NetworkAdapterEntry(@"PCI\VEN_0000&DEV_0001\TEST04", "WLAN (Test)", true,
            DeviceDisabled: false, Connection: AdapterConnection.Disconnected, DisableVerified: true);
        var ipv6Adapter = wireless with { InstanceId = @"PCI\VEN_0000&DEV_0001\TEST07", Connection = AdapterConnection.Connected, InternetV6 = true };
        var disabled = wireless with { InstanceId = @"PCI\VEN_0000&DEV_0001\TEST06", DeviceDisabled = true, Connection = AdapterConnection.Disabled };
        var unknownAdapter = new NetworkAdapterEntry(@"ROOT\NET\TEST03", "Statusprüfung (Test)", null);
        Check(NetworkAdapterEntries.FindRemovable(oldAdapter, [oldAdapter, presentAdapter, unknownAdapter]) == oldAdapter &&
            NetworkAdapterEntries.FindRemovable(oldAdapter, [presentAdapter]) is null &&
            NetworkAdapterEntries.FindRemovable(oldAdapter, [oldAdapter with { Present = true }]) is null &&
            NetworkAdapterEntries.FindRemovable(oldAdapter, [oldAdapter with { InternetV6 = true }]) is null &&
            NetworkAdapterEntries.FindRemovable(oldAdapter, [oldAdapter with { Present = null }]) is null,
            "Removal rechecks the exact device instance and rejects missing, present and unknown entries");
        Check(NetworkAdapterEntries.CreateRemovalCommand(oldAdapter).ArgumentList.SequenceEqual(["/remove-device", oldAdapter.InstanceId]) &&
            !(oldAdapter with { InstanceId = "/force" }).CanRemove, "The Windows command targets a single validated instance without extra switches");
        Check(wireless.CanDisable && !presentAdapter.CanDisable && !ipv6Adapter.CanDisable &&
            !(wireless with { InternetV6 = true }).CanDisable && !(wireless with { DisableVerified = false }).CanDisable &&
            !unknownAdapter.CanDisable && disabled.CanEnable && !wireless.CanEnable && !disabled.CanRemove,
            "Only independently verified disconnected adapters can be disabled and the active IPv4 and IPv6 routes are protected");
        var liveAdapters = NetworkAdapterConnections.Attach(NetworkAdapterEntries.Read());
        Console.WriteLine($"Read-only Windows adapter probe: {liveAdapters.Count} devices, {liveAdapters.Count(item => item.IsInternetRoute)} internet route adapters");

        var devices = RepairTool.Find("device-problems")!;
        var events = RepairTool.Find("system-events")!;
        var reliability = RepairTool.Find("reliability")!;
        Check(devices.ShowsDiagnosticOutput && devices.Command.Contains(
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362) ? "/enum-devices /problem" : "ConfigManagerErrorCode"),
            "Device diagnostic uses the correct query for this Windows version");
        Check(events.ShowsDiagnosticOutput && events.Command.Contains("Level = 1,2") && events.Command.Contains("-MaxEvents 20"),
            "System diagnostic selects at most twenty critical events and errors");
        Check(reliability.OpensWindow && reliability.Executable == "perfmon.exe" && reliability.Arguments == "/rel",
            "Reliability Monitor uses the Windows timeline");
        const string diagnosticSample = "2026-11-07 10:00:00 | Critical | Fixture 42\r\nSimulierte Beispielmeldung.";
        var eventSummary = RepairResultSummary.Create(events.Id, 0, false, diagnosticSample, "", TimeSpan.Zero, false);
        Check(eventSummary is { IsSuccess: true, CanRestart: false, Output: diagnosticSample },
            "Diagnostic output survives successful completion");
        var emptySummary = RepairResultSummary.Create(events.Id, 0, false, "", "", TimeSpan.Zero, true);
        Check(emptySummary is { IsSuccess: true, Output: "" } && emptySummary.Message.Contains("no matching entries"),
            "Empty diagnostic output is explained");
        Check(RepairResultSummary.Create(events.Id, 1, false, diagnosticSample, "Access denied", TimeSpan.Zero, true)
            is { IsSuccess: false, Output: diagnosticSample }, "Diagnostic failures keep their output");

        if (!ElevationService.IsElevated())
        {
            var deniedCleanup = false;
            try { NetworkAdapterEntries.RemoveAsync(oldAdapter).GetAwaiter().GetResult(); }
            catch (UnauthorizedAccessException) { deniedCleanup = true; }
            Check(deniedCleanup, "Removal is rejected without administrator rights before probing the device");

            var denied = false;
            try
            {
                RepairEngine.RunHelperAsync(["--repair-helper", "scan", "PowerControlProRepair-" + Guid.NewGuid().ToString("N")])
                    .GetAwaiter().GetResult();
            }
            catch (UnauthorizedAccessException) { denied = true; }
            Check(denied, "A non-admin repair helper rejects a valid command before connecting");
            denied = false;
            try { RepairEngine.RunElevatedAsync("scan", _ => { }).GetAwaiter().GetResult(); }
            catch (UnauthorizedAccessException) { denied = true; }
            Check(denied, "A non-admin repair request is blocked before any helper can start");
        }

        var reportsRoot = Path.Combine(output, "ReportFixtures", Guid.NewGuid().ToString("N"));
        var eventReports = new DiagnosticReportStore(reportsRoot, events.ReportFolder!);
        var deviceReports = new DiagnosticReportStore(reportsRoot, devices.ReportFolder!);
        var firstReport = eventReports.Save(DiagnosticReportStatus.Completed, "Oldest fixture").Report;
        for (var index = 1; index <= 6; index++)
            eventReports.Save(DiagnosticReportStatus.Completed, $"Diagnostic output {index}");
        var kept = eventReports.List();
        Check(kept.Count == 5 && kept.All(entry => entry != firstReport), "Retention keeps the five newest reports");
        Check(File.ReadAllText(eventReports.OpenablePath(kept[0])) == "Diagnostic output 6", "Newest report can be opened");
        var unrelated = Path.Combine(eventReports.OpenableFolder(), "manual-notes.log");
        File.WriteAllText(unrelated, "Unmanaged");
        var otherReport = deviceReports.Save(DiagnosticReportStatus.Empty, "Other tool").Report;
        var detailedSamples = new Dictionary<string, string>
        {
            ["network-diagnostics"] = "Aktive Adapter: 1/1\nGateway 192.0.2.1: antwortet\nInternet: Testziel erreichbar.\nDNS: Testname aufgelöst.\nEthernet | LAN/Ethernet | aktiv\n  IP: 192.0.2.10 | Gateway: 192.0.2.1\n  DNS: 192.0.2.53\n\n" +
                "Netzwerkteams\nBüro (LBFO) | Teamstatus: eingeschränkt\n  Mitglied: Ethernet | Adapterstatus: aktiv\n  Mitglied: LAN 2 | Adapterstatus: inaktiv\nHyper-V SET: Keine Teams gemeldet.",
            ["network-services"] = "DHCP-Client (Dhcp): läuft\nDNS-Client (Dnscache): läuft\nWLAN-Dienst (WlanSvc): beendet",
            ["storage-health"] = "Physische Laufwerke: 1\n1. Beispiel-SSD | SSD | NVMe\n  Zustand: Fehlerfrei laut Windows\n  Temperatur: 37 °C | Verschleiß: 0 %\n  Lesefehler gesamt: 0 | nicht korrigiert: 0\n  Schreibfehler gesamt: nicht verfügbar | nicht korrigiert: nicht verfügbar",
            ["driver-packages"] = "Drittanbieter-Treiberpakete: 2\nVeröffentlichte INF | Original-INF | Anbieter | Version\noem4.inf | example-net.inf | Beispielanbieter | 1.0.0.4\noem9.inf | example-usb.inf | Beispielanbieter | 2.1.0.0"
        };
        foreach (var (id, sample) in detailedSamples)
        {
            var reportStore = new DiagnosticReportStore(reportsRoot, RepairTool.Find(id)!.ReportFolder!);
            reportStore.Save(DiagnosticReportStatus.Failed, "Old report\n\nCommand\nCommand placeholder\n\nResult\nEarlier check");
            reportStore.Save(DiagnosticReportStatus.Completed,
                $"Protokoll\r\n\r\nBefehl\r\nBefehlsplatzhalter\r\n\r\nErgebnis\r\nErgebnisse dieser Abfrage.\r\n\r\n{sample}");
        }
        var largeReportStore = new DiagnosticReportStore(reportsRoot, "Grosses-Treiberinventar");
        var largeOutput = new string('X', 70_000);
        var largeReport = largeReportStore.Save(DiagnosticReportStatus.Completed, "Treiberpakete\n\nErgebnis\n" + largeOutput).Report;
        Check(largeReportStore.ReadResult(largeReport) == largeOutput, "Large driver inventories remain readable from report history");
        var otherSamples = new Dictionary<string, string>
        {
            [events.Id] = "Diagnostic output 6",
            [devices.Id] = "Other tool"
        };
        foreach (var tool in tools.Where(tool => tool.ReportFolder is not null && !detailedSamples.ContainsKey(tool.Id) && !otherSamples.ContainsKey(tool.Id)))
        {
            var sample = "Gespeichertes Ergebnis: " + tool.German;
            var store = new DiagnosticReportStore(reportsRoot, tool.ReportFolder!);
            store.Save(DiagnosticReportStatus.Completed, $"Protokoll\n\nBefehl\nBefehlsplatzhalter\n\nErgebnis\n{sample}");
            otherSamples.Add(tool.Id, sample);
        }
        var forged = kept[0] with { FileName = deviceReports.OpenablePath(otherReport) };
        bool rejected = false;
        try { eventReports.OpenablePath(forged); }
        catch (IOException) { rejected = true; }
        Check(rejected, "A report link cannot resolve outside its diagnosis folder");


        foreach (var (theme, english, compact) in new[] { ("Standard", false, false), ("Standard", false, true),
                     ("Classic", true, true), ("Neon Blau", false, false) })
        {
            var main = new MainWindow(output, new AppSettings { Theme = theme, Language = english ? "en" : "de", SizeIndex = compact ? 1 : 2 },
                loadUsersForTests: false)
            { ShowActivated = false };
            main.Show();
            main.ShowRepairPage();
            Pump(main);
            try
            {
                var grid = (UniformGrid)main.FindName("RepairToolGroups");
                var back = (Button)main.FindName("RepairBackButton");
                var overview = grid.Children.OfType<Button>().ToArray();
                Check(overview.Length == 4 && grid.Columns == 2 && !back.IsVisible, "Four overview tiles");
                Check(((FrameworkElement)main.FindName("RepairAdminNotice")).IsVisible == !ElevationService.IsElevated(),
                    "Overview explains why starting tools requires administrator rights");
                var repairScroll = (ScrollViewer)main.FindName("RepairToolScroll");
                var repairCard = (Border)main.FindName("RepairCardSurface");
                var ready = (TextBlock)main.FindName("RepairProgressLabel");
                var result = (TextBlock)main.FindName("RepairResult");
                Check(result.Visibility == Visibility.Collapsed, "An empty result does not reserve a line");
                Check(repairCard.ActualHeight - (ready.TranslatePoint(new Point(0, 0), repairCard).Y + ready.ActualHeight) < 18,
                    "Ready message sits close to the lower card edge");
                if (theme == "Standard")
                    Check(repairScroll.ScrollableHeight < 1, "All four overview tiles fit without scrolling");

                Check(overview.All(button => button.Background is LinearGradientBrush) &&
                    overview.Select(button => ((SolidColorBrush)button.BorderBrush).Color).Distinct().Count() == 4,
                    "Each category has a distinct frame and gradient");
                var categoryBrushes = overview.Select(button => (button.Background, button.BorderBrush)).ToArray();
                var suffix = compact ? "Kompakt" : "Standard";
                if (theme == "Standard") Save(main, Path.Combine(output, "Seite3-" + suffix + ".png"));

                foreach (var category in new[] { 1, 2, 3, 0 })
                {
                    overview[category].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Pump(main);
                    if (category == 3)
                    {
                        var network = (StackPanel)main.FindName("RepairNetworkPanel");
                        var picker = UnifiedDialogAppearance.Children<ComboBox>(network).Single(box => box.Name == "NetworkAdapterPicker");
                        var adapter = new RepairNetworkAdapter(Guid.NewGuid(), "Ethernet (Test)", true, "192.0.2.53", Connected: true,
                            Ipv4: "192.0.2.25", Gateway: "192.0.2.1", DnsV4: "192.0.2.53", DnsV6: "2001:db8::53",
                            HasIpv6: true, Index: 24, Ipv6Index: 24);
                        picker.Items.Clear();
                        picker.Items.Add(new ComboBoxItem { Content = adapter.Name + (english ? " · Connected" : " · Verbunden"), Tag = adapter });
                        picker.SelectedIndex = 0;
                        Pump(main);
                        Check(network.IsVisible && !grid.IsVisible &&
                            UnifiedDialogAppearance.Children<TextBlock>(network).Any(label => label.Text == adapter.DnsV4),
                            "Network category opens the diagnostics panel for the selected adapter");
                        if (theme == "Standard") Save(main, Path.Combine(output, "Netzwerk-Diagnose-" + suffix + ".png"));
                        var tabs = UnifiedDialogAppearance.Children<Button>(network).Where(button => button.Tag is int).ToArray();
                        Check(tabs.Length == 3, "Network has three sections");
                        tabs[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Pump(main);
                        Check(picker.SelectedIndex == 0 && UnifiedDialogAppearance.Children<Button>(network).Any(button =>
                            Equals(button.Content, english ? "Change DNS servers" : "DNS-Server ändern")), "Repair retains the selected adapter");
                        var backup = new NetworkDnsBackup(adapter.Id, "", DateTimeOffset.Now);
                        var backupFolder = Path.Combine(reportsRoot, "NetworkBackup");
                        backup.Save(backupFolder);
                        Check(NetworkDnsBackup.Read(backupFolder, adapter.Id) == backup &&
                            NetworkDnsBackup.Read(backupFolder, Guid.NewGuid()) is null, "Automatic DNS backup round-trips for its own adapter only");
                        var currentDns = backup with { StaticServersV6 = "2001:db8::53" };
                        currentDns.SaveBefore(backupFolder, new(adapter.Id, null, DnsV6: "", PreviousV6: currentDns.StaticServersV6));
                        Check(NetworkDnsBackup.Read(backupFolder, adapter.Id) == currentDns, "An IPv6 backup preserves the existing IPv4 entry");
                        var dnsDialog = new NetworkDnsWindow(main, ThemePalette.For(theme), english, administrator: true, adapter, currentDns)
                            { ShowActivated = false };
                        dnsDialog.Show();
                        try
                        {
                            var mode = UnifiedDialogAppearance.Children<ComboBox>(dnsDialog).Single(box => box.Name == "DnsMode");
                            var dnsInput = UnifiedDialogAppearance.Children<TextBox>(dnsDialog).Single(box => box.Name == "DnsAddresses");
                            Check(mode.SelectedIndex == 0 && dnsDialog.Request is { Dns: "", Ipv4Only: true }, "Automatic DNS is selected for the existing mode");
                            mode.SelectedIndex = 1;
                            dnsInput.Text = "2001:db8::53";
                            Check(dnsDialog.Request is null, "The IPv4 dialog rejects IPv6 addresses");
                            dnsInput.Text = "192.0.2.54";
                            Check(dnsDialog.Request is { Ipv4Only: true, Dns: "192.0.2.54" } &&
                                dnsDialog.Request.AdapterId == adapter.Id, "Manual DNS is scoped to the selected adapter and IPv4");
                            var family = UnifiedDialogAppearance.Children<ComboBox>(dnsDialog).Single(box => box.Name == "DnsFamily");
                            family.SelectedIndex = 2;
                            Check(mode.Items.Count == 12, "Automatic and manual are followed by ten DNS profiles");
                            foreach (var profile in NetworkDnsProfile.All)
                            {
                                mode.SelectedItem = profile;
                                Check(dnsDialog.Request is { } profileRequest && profileRequest.Dns == profile.Ipv4 &&
                                    profileRequest.DnsV6 == profile.Ipv6 && dnsInput.IsReadOnly,
                                    "Each profile supplies validated DNS addresses for both families");
                            }
                            mode.SelectedItem = NetworkDnsProfile.All.Single(profile => profile.Name == "AdGuard DNS");
                            Pump(dnsDialog);
                            if (theme == "Standard" && compact)
                            {
                                mode.IsDropDownOpen = true;
                                try
                                {
                                    Pump(dnsDialog);
                                    var popup = (Popup)mode.Template.FindName("PART_Popup", mode);
                                    var popupFrame = (Border)popup.Child;
                                    var list = (ScrollViewer)popupFrame.Child;
                                    Check(popup.IsOpen && list.VerticalScrollBarVisibility == ScrollBarVisibility.Hidden && list.ScrollableHeight > 0,
                                        "The DNS profile list can scroll without a visible scrollbar");
                                    list.ScrollToTop();
                                    Pump(dnsDialog);
                                    var top = list.VerticalOffset;
                                    list.ScrollToBottom();
                                    Pump(dnsDialog);
                                    Check(list.VerticalOffset > top, "The DNS profile list reaches the lower entries");
                                    Check(ReferenceEquals(popupFrame.Background, main.FindResource("AppBrush")),
                                        "The DNS profile popup uses the active theme's darker surface");
                                }
                                finally { mode.IsDropDownOpen = false; }
                                Pump(dnsDialog);
                            }
                            if (theme == "Standard") Save(dnsDialog, Path.Combine(output, "Netzwerk-DNS-Bestaetigung-" + suffix + ".png"));
                        }
                        finally { dnsDialog.Close(); }
                        tabs[2].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Check(UnifiedDialogAppearance.Children<Button>(network).Any(button =>
                            Equals(button.Content, english ? "Network services" : "Netzwerkdienste")), "Existing tools are reachable under Advanced");
                        foreach (var reset in tools.Where(tool => tool.IsNetworkReset))
                        {
                            var action = UnifiedDialogAppearance.Children<Button>(network).Single(button => Equals(button.Content, reset.Title(english)));
                            Check(action.IsEnabled == ElevationService.IsElevated(), "The advanced menu requires administrator rights for network resets");
                            Pump(main);
                            Check(action.TranslatePoint(new Point(0, action.ActualHeight), repairScroll).Y <= repairScroll.ActualHeight + 1,
                                "The reset buttons fit without scrolling in the advanced menu");
                        }
                        Pump(main);
                        if (theme == "Standard") Save(main, Path.Combine(output, "Netzwerk-Erweitert-" + suffix + ".png"));
                        Check(UnifiedDialogAppearance.Children<Button>(network).Any(button =>
                            Equals(button.Content, english ? "Manage adapters" : "Adapterverwaltung")),
                            "Adapter cleanup is accessible from Advanced");
                        var disableCheck = new TaskCompletionSource<IReadOnlySet<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var cleanup = new NetworkAdapterCleanupWindow(main, ThemePalette.For(theme), english, administrator: true,
                            fixture: [oldAdapter, oldAdapter2, presentAdapter, ipv6Adapter, wireless with { DisableVerified = false }, disabled, unknownAdapter],
                            fixtureDisableCheck: () => disableCheck.Task) { ShowActivated = false };
                        cleanup.Show();
                        try
                        {
                            Pump(cleanup);
                            var list = UnifiedDialogAppearance.Children<ListBox>(cleanup).Single(item => item.Name == "AdapterCleanupList");
                            var remove = UnifiedDialogAppearance.Children<Button>(cleanup).Single(item => item.Name == "RemoveAdapterEntry");
                            var toggle = UnifiedDialogAppearance.Children<Button>(cleanup).Single(item => item.Name == "ToggleAdapter");
                            var details = UnifiedDialogAppearance.Children<TextBlock>(cleanup).Single(item => item.Name == "AdapterSelectionDetails");
                            var detailTitle = UnifiedDialogAppearance.Children<TextBlock>(cleanup).Single(item => item.Name == "AdapterDetailTitle");
                            var pending = UnifiedDialogAppearance.Children<TextBlock>(cleanup).Single(item => item.Name == "AdapterDisableCheckStatus");
                            var listMessage = UnifiedDialogAppearance.Children<TextBlock>(cleanup).Single(item => item.Name == "AdapterListMessage");
                            Check(list.Items.Count == 7 && !remove.IsEnabled && ((NetworkAdapterEntry)((ListBoxItem)list.Items[0]).Tag).IsInternetRoute,
                                "The dialog lists all states and prioritizes active internet routes");
                            list.SelectedItem = list.Items.OfType<ListBoxItem>().Single(row => ((NetworkAdapterEntry)row.Tag).InstanceId == wireless.InstanceId);
                            Check(list.IsEnabled && !toggle.IsEnabled && listMessage.Visibility == Visibility.Collapsed &&
                                pending.IsVisible == NetworkAdapterEntries.RemovalSupported, "The list is usable while Windows checks whether disabling is allowed");
                            var selected = list.SelectedItem;
                            if (theme == "Standard" && compact) Save(cleanup, Path.Combine(output, "Adapterverwaltung-Hintergrundpruefung.png"));
                            disableCheck.SetResult(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { wireless.InstanceId });
                            Pump(cleanup);
                            Check(!pending.IsVisible && list.SelectedItem == selected && toggle.IsEnabled == NetworkAdapterEntries.RemovalSupported,
                                "Finishing the background check enables the eligible action without rebuilding the list");
                            var removableRows = new List<(ListBoxItem Row, CheckBox Check)>();
                            foreach (var row in list.Items.OfType<ListBoxItem>())
                            {
                                list.SelectedItem = row;
                                var device = (NetworkAdapterEntry)row.Tag;
                                var check = UnifiedDialogAppearance.Children<CheckBox>(row).Single();
                                var rowBadge = UnifiedDialogAppearance.Children<TextBlock>(row).Single(item => item.Name == "AdapterRowBadge");
                                var lockIcon = UnifiedDialogAppearance.Children<System.Windows.Shapes.Path>(row).Single(item => item.Name == "AdapterProtectionIcon");
                                Check(!remove.IsEnabled && (check.Visibility == Visibility.Visible) == device.CanRemove,
                                    "Clicking a row only displays details, and only absent adapters have a checkbox");
                                Check(rowBadge.Text == (english ? "Details open" : "Details geöffnet") && detailTitle.Text.EndsWith(device.Name),
                                    "The row badge and details title identify the same adapter");
                                if (device.IsInternetRoute) Check(lockIcon.Visibility == Visibility.Visible, "An active internet adapter shows a protection lock");
                                Check(toggle.IsEnabled == (NetworkAdapterEntries.RemovalSupported && (device.InstanceId == wireless.InstanceId || device.CanEnable)),
                                    "Only verified inactive or disabled devices offer a state change");
                                if (!device.CanRemove) continue;
                                Check(details.Text.Contains("USB") && details.Text.Contains(english ? "Wired" : "Kabel"), "The selected adapter explains its purpose");
                                removableRows.Add((row, check));
                            }
                            Check(removableRows.Count == 2, "Two old adapter instances are available for marking");
                            list.SelectedItem = removableRows[1].Row;
                            foreach (var (_, check) in removableRows)
                            {
                                if (check.IsEnabled)
                                {
                                    var peer = new System.Windows.Automation.Peers.CheckBoxAutomationPeer(check);
                                    var toggleProvider = (System.Windows.Automation.Provider.IToggleProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle)!;
                                    toggleProvider.Toggle();
                                }
                                else check.IsChecked = true;
                            }
                            list.ScrollIntoView(removableRows[1].Row);
                            Pump(cleanup);
                            var markedBadge = UnifiedDialogAppearance.Children<TextBlock>(removableRows[0].Row).Single(item => item.Name == "AdapterRowBadge");
                            Check(list.SelectedItem == removableRows[1].Row && markedBadge.Text == (english ? "Marked" : "Markiert"),
                                "Marking one adapter for removal does not switch the open details");
                            var label = (TextBlock)remove.Content;
                            Check(label.Text.StartsWith(english ? "Remove 2" : "2 Adapter") && label.Foreground == Brushes.White &&
                                remove.IsEnabled == NetworkAdapterEntries.RemovalSupported, "The count and contrast reflect both checked entries");
                            Save(cleanup, Path.Combine(output, $"Adapterauswahl-{theme.Replace(' ', '-')}-{suffix}.png"));
                            removableRows[0].Check.IsChecked = false;
                            Check(label.Text.StartsWith(english ? "Remove 1" : "1 Adapter"), "Unchecking one adapter preserves the other selection");
                        }
                        finally { cleanup.Close(); }
                        tabs[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Pump(main);
                        Check(grid.IsVisible && grid.Children.Count == 4 && !network.IsVisible, "Back restores the repair overview");
                        continue;
                    }
                    var visibleTools = grid.Children.OfType<Button>().ToArray();
                    Check(back.IsVisible && grid.Columns == (compact ? 2 : 3), "Category uses responsive tile columns");
                    Check(visibleTools.Select(button => (string)button.Tag).SequenceEqual(tools.Where(tool => tool.Group == category).Select(tool => tool.Id)),
                        "Category contains only its tools");
                    Check(visibleTools.All(button => button.IsVisible && button.ActualHeight > 60 &&
                        button.TranslatePoint(new Point(0, 0), grid).Y + button.ActualHeight <= grid.ActualHeight),
                        "Tool tiles are laid out and reachable");
                    Check(visibleTools.All(button => ReferenceEquals(button.Background, categoryBrushes[category].Background) &&
                        ReferenceEquals(button.BorderBrush, categoryBrushes[category].BorderBrush)), "Tool tiles inherit the category palette");
                    if (theme == "Standard")
                        Save(main, Path.Combine(output, new[] { "Diagnose", "Reparatur", "Windows-Werkzeuge", "Netzwerk" }[category] + "-Kacheln-" + suffix + ".png"));
                    if (category == 0)
                    {
                        repairScroll.ScrollToEnd();
                        Pump(main);
                        var last = visibleTools[^1];
                        Check(last.TranslatePoint(new Point(0, last.ActualHeight), repairScroll).Y <= repairScroll.ActualHeight + 1,
                            "The last diagnostic tile is reachable in the scroll area");
                        repairScroll.ScrollToHome();
                        break;
                    }
                    back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Pump(main);
                    Check(grid.Children.Count == 4 && !back.IsVisible, "Back button restores the overview");
                }

                var scanTile = grid.Children.OfType<Button>().Single(button => Equals(button.Tag, "scan"));
                bool routed = false;
                main.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                {
                    var routedDialog = System.Windows.Application.Current.Windows.OfType<RepairConfirmWindow>()
                        .SingleOrDefault(candidate => candidate.IsVisible);
                    if (routedDialog is null) return;
                    routed = ((TextBox)routedDialog.FindName("CommandText")).Text == RepairTool.Find("scan")!.Command;
                    if (!ElevationService.IsElevated())
                    {
                        var blockedStart = (Button)routedDialog.FindName("StartButton");
                        blockedStart.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        routed &= routedDialog.IsVisible && !blockedStart.IsEnabled;
                    }
                    var historyButton = UnifiedDialogAppearance.Children<Button>(routedDialog).Single(button => button.Name == "DiagnosticShowHistory");
                    historyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    routed &= ((Button)routedDialog.FindName("StartButton")).Visibility == Visibility.Collapsed;
                    ((Button)routedDialog.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                scanTile.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(routed && back.IsVisible, "Tile opens the existing confirmation dialog and stays in the category on cancel");
                foreach (var tool in tools)
                {
                    var reports = tool.ReportFolder is { } folder ? new DiagnosticReportStore(reportsRoot, folder) : null;
                    RepairNetworkAdapter[] adapters =
                    [
                        new(Guid.NewGuid(), "LAN (Test)", false, "192.0.2.10"),
                        new(Guid.NewGuid(), "WLAN (Test)", true, "192.0.2.53")
                    ];
                    var toolDialog = new RepairConfirmWindow(tool, ThemePalette.For(theme), english, reports: reports, networkAdapters: adapters,
                        tcpIpConfiguration: tcpIpFixture)
                    { Owner = main, ShowActivated = false };
                    toolDialog.Show();
                    try
                    {
                        Pump(toolDialog);
                        var button = UnifiedDialogAppearance.Children<Button>(toolDialog)
                            .SingleOrDefault(candidate => candidate.Name == "DiagnosticShowHistory");
                        Check((button is null) == (reports is null), tool.Id + ": report button matches availability");
                        if (tool.IsNetworkReset)
                        {
                            var start = (Button)toolDialog.FindName("StartButton");
                            Check(!start.IsDefault && start.IsEnabled == ElevationService.IsElevated(),
                                "A reset needs an explicit administrator confirmation");
                            Check(UnifiedDialogAppearance.Children<TextBlock>(toolDialog).Any(item => item.Text.Contains(
                                english ? "Restart Windows after the reset." : "Starte Windows nach dem Reset neu.")),
                                "The confirmation explains the restart even without administrator rights");
                            var snapshot = UnifiedDialogAppearance.Children<TextBlock>(toolDialog)
                                .SingleOrDefault(item => item.Name == "TcpIpConfiguration");
                            Check(tool.Id == "tcpip-reset" ? snapshot?.Text == tcpIpFixture && snapshot.IsVisible : snapshot is null,
                                "TCP/IP shows the original configuration before the reset");
                            if (theme == "Standard") Save(toolDialog, Path.Combine(output, tool.Id + "-" + suffix + ".png"));
                        }
                        if (tool.NeedsAdapter)
                        {
                            var start = (Button)toolDialog.FindName("StartButton");
                            var picker = UnifiedDialogAppearance.Children<ComboBox>(toolDialog).Single(item => item.Name == "RepairNetworkAdapterPicker");
                            Check(!start.IsEnabled && toolDialog.NetworkRequest is null && !start.IsDefault,
                                tool.Id + ": explicit selection and confirmation required");
                            Check(picker.Items.Count == (tool.Id == "dhcp-renew" ? 1 : 2), tool.Id + ": only eligible adapters selectable");
                            picker.SelectedItem = adapters[1];
                            if (tool.Id == "dns-servers")
                            {
                                var input = UnifiedDialogAppearance.Children<TextBox>(toolDialog).Single(item => item.Name == "RepairDnsServers");
                                input.Text = "not-an-address";
                                Check(toolDialog.NetworkRequest is null && !start.IsEnabled, "Invalid DNS cannot be submitted");
                                input.Text = "192.0.2.54, 2001:db8::53";
                                Check(toolDialog.NetworkRequest?.Dns == input.Text, "Manual DNS uses the entered addresses");
                                if (theme == "Standard")
                                {
                                    Pump(toolDialog);
                                    Save(toolDialog, Path.Combine(output, "DNS-Server-" + suffix + ".png"));
                                }
                                var auto = UnifiedDialogAppearance.Children<CheckBox>(toolDialog).Single(item => item.Name == "RepairDnsAutomatic");
                                auto.IsChecked = true;
                                Check(toolDialog.NetworkRequest?.Dns == "" && !input.IsEnabled, "Automatic DNS ignores the manual field");
                            }
                            Check(toolDialog.NetworkRequest?.AdapterId == adapters[1].Id && start.IsEnabled == ElevationService.IsElevated(),
                                tool.Id + ": only the selected GUID is passed and admin is required");
                        }
                        else if (tool.UsesLocalizedQuery)
                        {
                            var copiedCommand = ((TextBox)toolDialog.FindName("CommandText")).Text;
                            Check(copiedCommand.StartsWith(english ? "$english = $true" : "$english = $false", StringComparison.Ordinal),
                                tool.Id + ": command and its output use the selected language");
                            Check(((Button)toolDialog.FindName("StartButton")).IsEnabled == ElevationService.IsElevated(),
                                tool.Id + ": diagnostics keep the administrator requirement");
                        }
                        if (button is null) continue;
                        var appStyle = toolDialog.FindResource("UnifiedDialogAction");
                        var history = UnifiedDialogAppearance.Children<Border>(toolDialog).Single(border => border.Name == "DiagnosticHistory");
                        var capture = theme == "Standard" && tool.Id == devices.Id;
                        Check(button.IsVisible && ReferenceEquals(button.Style, appStyle) && !history.IsVisible,
                            tool.Id + ": report button uses app styling in the details view");
                        if (capture) Save(toolDialog, Path.Combine(output, "Geraetediagnose-Details-" + suffix + ".png"));
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Pump(toolDialog);
                        Check(history.IsVisible && Equals(button.Content, english ? "Back to details" : "Zurück zu Details") &&
                            ReferenceEquals(button.Style, appStyle), tool.Id + ": return button uses app styling in the report view");
                        if (capture) Save(toolDialog, Path.Combine(output, "Geraetediagnose-Protokolle-" + suffix + ".png"));
                        if (otherSamples.TryGetValue(tool.Id, out var archivedSample))
                        {
                            var entries = UnifiedDialogAppearance.Children<Button>(history).Where(row => row.Name == "DiagnosticSelectReport").ToArray();
                            var preview = UnifiedDialogAppearance.Children<TextBox>(history).Single(box => box.Name == "DiagnosticReportPreviewText");
                            Check(entries.Length == reports!.List().Count && entries.Length > 0 && !preview.IsVisible,
                                tool.Id + ": archived reports have selectable rows");
                            entries[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            Pump(toolDialog);
                            Check(preview.IsVisible && preview.Text == archivedSample, tool.Id + ": selecting a row shows the saved result");
                            if (theme == "Standard" && tool.Id == "verify")
                                Save(toolDialog, Path.Combine(output, "Systemdateien-Zusammenfassung-" + suffix + ".png"));
                        }
                        if (detailedSamples.TryGetValue(tool.Id, out var sample))
                        {
                            var entries = UnifiedDialogAppearance.Children<Button>(history).Where(row => row.Name == "DiagnosticSelectReport").ToArray();
                            var preview = UnifiedDialogAppearance.Children<TextBox>(history).Single(box => box.Name == "DiagnosticReportPreviewText");
                            Check(entries.Length == 2 && !preview.IsVisible, tool.Id + ": saved reports are selectable");
                            entries[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            Check(preview.IsVisible && preview.Text == "Earlier check", tool.Id + ": English report opens inside history");
                            entries[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            Pump(toolDialog);
                            Check(preview.Text.ReplaceLineEndings("\n") == "Ergebnisse dieser Abfrage.\n\n" + sample,
                                tool.Id + ": switching to the latest report shows its entire saved result without the command");
                            if (theme == "Standard") Save(toolDialog, Path.Combine(output, tool.Id + "-Protokoll-" + suffix + ".png"));

                            var summary = RepairResultSummary.Create(tool.Id, 0, false, sample, "", TimeSpan.FromSeconds(1), english);
                            var resultWindow = new RepairResultWindow(summary, ThemePalette.For(theme), english, reports, tool.Title(english))
                            { Owner = main, ShowActivated = false };
                            resultWindow.Show();
                            try
                            {
                                Pump(resultWindow);
                                var outputBox = UnifiedDialogAppearance.Children<TextBox>(resultWindow.FindName("ResultCard") as DependencyObject).Single();
                                Check(outputBox.IsVisible && outputBox.Text.ReplaceLineEndings("\n") == sample && double.IsPositiveInfinity(outputBox.MaxHeight),
                                    tool.Id + ": the entire result is visible by default without an inner height limit");
                                if (theme == "Standard") Save(resultWindow, Path.Combine(output, tool.Id + "-Ergebnis-" + suffix + ".png"));
                            }
                            finally { resultWindow.Close(); }
                        }
                        if (tool.Id == devices.Id || tool.Id == events.Id)
                        {
                            var count = reports!.List().Count;
                            var expected = english
                                ? count == 1 ? "1 saved report" : $"{count} saved reports"
                                : count == 1 ? "1 gespeichertes Protokoll" : $"{count} gespeicherten Protokolle";
                            var fits = false;
                            toolDialog.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                            {
                                var question = System.Windows.Application.Current.Windows.OfType<ThemedMessageBoxWindow>()
                                    .Single(window => window.IsVisible);
                                try
                                {
                                    Pump(question);
                                    var scroll = UnifiedDialogAppearance.Children<ScrollViewer>(question).Single();
                                    var message = (TextBlock)question.FindName("DialogMessage");
                                    fits = scroll.ScrollableHeight < 1 && message.Text.Contains(expected, StringComparison.Ordinal) &&
                                        message.Text.Contains(reports.RelativeFolder, StringComparison.Ordinal);
                                    if (theme == "Standard")
                                        Save(question, Path.Combine(output, $"Protokoll-Loeschen-{count}-{suffix}.png"));
                                }
                                finally
                                {
                                    ((Button)question.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                }
                            });
                            UnifiedDialogAppearance.Children<Button>(history).Single(candidate => candidate.Name == "DiagnosticDeleteReports")
                                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            Check(fits, tool.Id + ": the nested deletion question, folder and warning fit without scrolling");
                            Check(reports.List().Count == count, tool.Id + ": cancelling keeps the reports");
                        }
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Check(!history.IsVisible && Equals(button.Content, english ? "Reports" : "Protokolle"),
                            tool.Id + ": return restores details and the original label");
                    }
                    finally { toolDialog.Close(); }
                }
                var scan = RepairTool.Find("scan")!;
                var dialog = new RepairConfirmWindow(scan, ThemePalette.For(main.CurrentThemeName), english)
                { Owner = main, ShowActivated = false };
                dialog.Show();
                Pump(dialog);
                try
                {
                    Check(((TextBox)dialog.FindName("CommandText")).Text == scan.Command, "Full command in copy source");
                    var start = (Button)dialog.FindName("StartButton");
                    Check(start.IsVisible && start.IsEnabled == ElevationService.IsElevated(), "Start button follows the real administrator token");
                    Check(((Button)dialog.FindName("CopyButton")).IsEnabled, "Copy is available without elevation");
                    Check(((Button)dialog.FindName("CancelButton")).IsVisible, "Close button is visible");
                    if (theme == "Standard") Save(dialog, Path.Combine(output, "ScanHealth-Dialog-" + suffix + ".png"));
                }
                finally { dialog.Close(); }

                var simulatedAdmin = new RepairConfirmWindow(scan, ThemePalette.For(main.CurrentThemeName), english, administrator: true)
                { Owner = main, ShowActivated = false };
                simulatedAdmin.Show();
                try
                {
                    Pump(simulatedAdmin);
                    Check(((Button)simulatedAdmin.FindName("StartButton")).IsEnabled, "Admin appearance enables Start");
                }
                finally { simulatedAdmin.Close(); }
                {
                    var diagnosticsResult = new RepairResultWindow(eventSummary, ThemePalette.For(theme), english, eventReports, events.Title(english))
                    { Owner = main, ShowActivated = false };
                    diagnosticsResult.Show();
                    try
                    {
                        Pump(diagnosticsResult);
                        var history = UnifiedDialogAppearance.Children<Border>(diagnosticsResult).Single(border => border.Name == "DiagnosticHistory");
                        Check(history.IsVisible && ((FrameworkElement)diagnosticsResult.FindName("ResultCard")).IsVisible,
                            "Report history and result are visible together on first open");
                        var position = history.TranslatePoint(new Point(0, 0), diagnosticsResult);
                        var resultCard = (FrameworkElement)diagnosticsResult.FindName("ResultCard");
                        Check(position.Y >= resultCard.TranslatePoint(new Point(0, resultCard.ActualHeight), diagnosticsResult).Y,
                            "Report history is below the current result");
                        var heading = UnifiedDialogAppearance.Children<TextBlock>(history).Single(text => text.Name == "DiagnosticHistoryHeading");
                        Check(heading.Text == (english ? "Reports · 5 of max. 5" : "Protokolle · 5 von max. 5"),
                            "History header shows the number of reports in the selected language");
                        var controls = UnifiedDialogAppearance.Children<Button>(history).ToArray();
                        Check(controls.Count(button => button.Name == "DiagnosticOpenReport") == 5, "Every retained report has a file button");
                        var historyRows = controls.Where(button => button.Name == "DiagnosticSelectReport").ToArray();
                        Check(historyRows.Length == 5, "Every retained report is selectable from the result dialog");
                        historyRows[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        var reportPreview = UnifiedDialogAppearance.Children<TextBox>(history).Single(box => box.Name == "DiagnosticReportPreviewText");
                        Check(reportPreview.IsVisible && reportPreview.Text == "Diagnostic output 5", "The result dialog shows the selected older report");
                        Check(ReferenceEquals(controls.Single(button => button.Name == "DiagnosticDeleteReports").Style,
                            diagnosticsResult.FindResource("UnifiedDialogDanger")), "Report deletion is styled as a destructive action");
                        if (theme == "Standard") Save(diagnosticsResult, Path.Combine(output, "Systemdiagnose-Verlauf-" + suffix + ".png"));
                        var fullOutput = UnifiedDialogAppearance.Children<TextBox>(resultCard).Single();
                        var toggleOutput = UnifiedDialogAppearance.Children<Button>(resultCard).Single(button => button.Name == "DiagnosticFullOutput");
                        Check(fullOutput.Visibility == Visibility.Collapsed, "Long output does not cover the history by default");
                        toggleOutput.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Check(fullOutput.Visibility == Visibility.Visible && fullOutput.Text == diagnosticSample, "Full result stays accessible");
                        toggleOutput.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        if (theme == "Neon Blau")
                        {
                            foreach (var confirm in new[] { false, true })
                            {
                                diagnosticsResult.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                                {
                                    var question = System.Windows.Application.Current.Windows.OfType<ThemedMessageBoxWindow>()
                                        .Single(window => window.IsVisible);
                                    Pump(question);
                                    Save(question, Path.Combine(output, "Systemdiagnose-Protokolle-Loeschen.png"));
                                    ((Button)question.FindName(confirm ? "ConfirmButton" : "CancelButton"))
                                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                });
                                controls.Single(button => button.Name == "DiagnosticDeleteReports").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                Check(eventReports.List().Count == (confirm ? 0 : 5), "Only confirmed deletion removes reports");
                            }
                            Check(heading.Text.Contains("0 von max. 5"), "History updates after deletion");
                            Check(File.Exists(unrelated) && File.Exists(deviceReports.OpenablePath(otherReport)), "Other files survive deletion");
                        }
                    }
                    finally { diagnosticsResult.Close(); }
                }
                if (theme == "Standard")
                {
                    const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    typeof(MainWindow).GetField("_selectedRepairTool", flags)!.SetValue(main, scan);
                    typeof(MainWindow).GetMethod("UpdateRepairRunLabels", flags)!.Invoke(main, null);
                    ((UIElement)main.FindName("RepairToolScroll")).Visibility = Visibility.Collapsed;
                    ((UIElement)main.FindName("RepairRunningPanel")).Visibility = Visibility.Visible;
                    ((TextBox)main.FindName("RepairLiveOutput")).Text = "00:12  [==================           42.0%                         ]";
                    var progress = (AnimatedProgressBar)main.FindName("RepairProgress");
                    typeof(MainWindow).GetField("_repairRunning", flags)!.SetValue(main, true);
                    typeof(MainWindow).GetField("_repairHasMeasuredProgress", flags)!.SetValue(main, true);
                    typeof(MainWindow).GetMethod("SetTimerState", flags)!.Invoke(main, [false]);
                    ((TextBlock)main.FindName("RepairProgressLabel")).Text = "Weitere Reparaturaktionen sind während des Vorgangs gesperrt.";
                    ((TextBlock)main.FindName("RepairTimeText")).Text = "Laufzeit 00:12 · Restzeit noch nicht schätzbar";
                    progress.Value = 42;
                    progress.ShowPercentage = true;
                    var cancel = (Button)main.FindName("RepairCancelButton");
                    cancel.IsEnabled = true;
                    Pump(main);
                    Check(cancel.IsVisible && cancel.ActualHeight > 20, "Running operation has a reachable stop button");
                    Save(main, Path.Combine(output, "ScanHealth-Laufanzeige-" + suffix + ".png"));
                    typeof(MainWindow).GetField("_repairRunning", flags)!.SetValue(main, false);
                }
            }
            finally { main.Close(); }
        }
        Console.WriteLine("Repair tools catalog, result policy and WPF views passed.");
        return 0;
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }

    private static void Pump(Window window)
    {
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static void Save(Window window, string path)
    {
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}

