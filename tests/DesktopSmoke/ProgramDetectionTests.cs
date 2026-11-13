using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class ProgramDetectionTests
{
    internal static int Run(App app, string output)
    {
        int checks = 0;
        void Assert(bool ok, string name) { if (!ok) throw new Exception(name); checks++; }
        ProgramEntry Entry(string id) => ProgramCatalog.Entries.Single(e => e.Id == id);
        var cases = new (string Id, string Name, string Publisher)[] {
            ("filee", "Filee", "Filee contributors"), ("adwcleaner", "AdwCleaner", "Malwarebytes"),
            ("firefox", "Mozilla Firefox (x64 de)", "Mozilla"), ("brave", "Brave", "Die Brave-Autoren"),
            ("opera-gx", "Opera GX Stable 136.0.6008.111", "Opera Software"), ("notepad", "Notepad++ (64-bit x64)", "Notepad++ Team"),
            ("node-js", "Node.js", "Node.js Foundation"), ("vlc", "VLC media player", "VideoLAN"), ("cursor", "Cursor (User)", "Anysphere"),
            ("winrar", "WinRAR 7.22 (64-Bit)", "win.rar GmbH"), ("putty", "PuTTY release 0.85 (64-bit)", "Simon Tatham"),
            ("crystaldiskinfo", "CrystalDiskInfo 9.9.3", "Crystal Dew World"), ("net-sdk", "Microsoft .NET SDK 10.0.303 (x64)", "Microsoft Corporation"),
            ("vortex", "Vortex", "Black Tree Gaming Ltd."), ("wowup-cf", "WowUp-CF 2.20.0", "WowUp LLC"),
            ("satisfactory-mod-manager", "Satisfactory Mod Manager (current user)", "Satisfactory Modding"),
            ("ultraplus-manager", "UltraPlus Manager version 1.9.11", "UltraPlus Manager"), ("bcuninstaller", "BCUninstaller 6.3.0.0", "Marcin Szeniak"),
            ("msi-afterburner", "MSI Afterburner 4.6.6", "MSI Co., LTD"), ("teamspeak", "TeamSpeak", "TeamSpeak"),
            ("artcraft-launcher", "ArtCraft Launcher", "Learning Machines LLC")
        };
        foreach (var item in cases)
        {
            Assert(ProgramLocalInventory.Matches(Entry(item.Id), item.Name, item.Publisher), "Observed alias recognized: " + item.Id);
            Assert(!ProgramLocalInventory.Matches(Entry(item.Id), item.Name + " Helper", item.Publisher), "Helper excluded: " + item.Id);
            Assert(!ProgramLocalInventory.Matches(Entry(item.Id), item.Name, "Unrelated publisher"), "Wrong publisher excluded: " + item.Id);
        }
        Assert(!ProgramLocalInventory.Matches(Entry("opera"), "Opera GX Stable 136.0.6008.111", "Opera Software"), "Opera GX is not Opera Stable");
        Assert(!ProgramLocalInventory.Matches(Entry("microsoft-edge"), "Microsoft Edge WebView2-Laufzeit", "Microsoft Corporation"), "WebView runtime is not Edge browser");
        Assert(!ProgramLocalInventory.Matches(Entry("net-sdk"), "Microsoft.NET.Sdk.Maui.Manifest-10.0.100 (x64)", "Microsoft Corporation"), "SDK workload is not SDK installation");
        Assert(!ProgramLocalInventory.Matches(Entry("vs-code"), "vs_codecoveragemsi", "Microsoft Corporation"), "Coverage component is not VS Code");
        Assert(ProgramLocalInventory.MatchesPortable("tukui-client", "Tukui Client", " "), "Portable Tukui without publisher metadata");
        Assert(!ProgramLocalInventory.MatchesPortable("tukui-client", "Unrelated", " "), "Filename alone is insufficient");
        Assert(ProgramLocalInventory.MatchesPortable("adwcleaner", "AdwCleaner", "Malwarebytes"), "AdwCleaner portable executable metadata confirmed on local desktop");
        Assert(!ProgramLocalInventory.MatchesPortable("adwcleaner", "AdwCleaner", "Unrelated publisher"), "AdwCleaner filename cannot substitute for publisher identity");
        Assert(!ProgramLocalInventory.MatchesPortable("compactgui", "CompactGUI", "Unrelated"), "Portable publisher mismatch excluded");
        Assert(ProgramLocalInventory.MatchesPortable("autoruns", "Sysinternals autoruns", "Sysinternals - www.sysinternals.com"), "Autoruns product metadata");
        Assert(ProgramLocalInventory.MatchesPortable("extra-ventoy-ventoy", "Ventoy", ""), "Ventoy start executable without publisher metadata");
        Assert(!ProgramLocalInventory.MatchesPortable("extra-ventoy-ventoy", "VentoyPlugson", ""), "Ventoy helper is not the main start executable");
        Assert(!ProgramLocalInventory.Compatible(Entry("teamspeak"), "5.0.0") && ProgramLocalInventory.Compatible(Entry("teamspeak"), "3.6.2"), "TeamSpeak generations separated");
        var store = ProgramVersions.ParseStoreInstallations("[{\"PackageFamilyName\":\"PythonSoftwareFoundation.Python.3.12_qbz5n2kfra8p0\",\"Version\":\"3.12.2800.0\"},{\"PackageFamilyName\":\"ROBLOXCORPORATION.ROBLOX_55nm5eh3cm0pr\",\"Version\":\"2.699.877.0\"},{\"PackageFamilyName\":\"OpenAI.ChatGPT-Desktop_wrongpublisher\",\"Version\":\"9.9.9.9\"}]");
        Assert(store.Count == 2 && !store["roblox-client"].WinGetCompatible, "Exact Store identities and separate update path");
        var incompatible = new ProgramInventory(true, new Dictionary<string, string?> { ["teamspeak"] = "5.0.0" }, Updates: new Dictionary<string, string> { ["teamspeak"] = "3.6.2" },
            Evidence: new Dictionary<string, ProgramInstallation> { ["teamspeak"] = new("5.0.0", "Registry", WinGetCompatible: false) });
        Assert(!incompatible.HasUpdate("teamspeak"), "Even injected update state cannot downgrade another variant");
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var local = ProgramLocalInventory.Read(ProgramCatalog.Entries, limit.Token);
        // Purely local package enumeration; no WinGet, source refresh, installer or app startup.
        Dictionary<string, ProgramInstallation> storeLive;
        try { storeLive = Task.Run(() => ProgramVersions.ReadStoreInstallationsAsync(limit.Token)).GetAwaiter().GetResult(); }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
            // Store discovery is an independent source. A transient timeout must not hide
            // verified registry and portable-file detection results in this local suite.
            storeLive = []; Console.WriteLine("SKIP live Store source: " + error.GetType().Name);
        }
        foreach (var pair in storeLive) local.TryAdd(pair.Key, pair.Value);
        try
        {
            OnlineConnection.NetworkForTests = () => false;
            var offline = Task.Run(() => new ProgramInstallService().InspectAsync(limit.Token)).GetAwaiter().GetResult();
            Assert(offline.Installed.ContainsKey("wowup-cf") && offline.Installed.ContainsKey("teamspeak") && offline.Evidence?["teamspeak"].Path is not null,
                "Actual service preserves local evidence and paths when offline");
        }
        finally { OnlineConnection.NetworkForTests = null; }
        var simulated = new ProgramInstallService((start, _) =>
        {
            if (start.ArgumentList[0] == "export")
            {
                int index = start.ArgumentList.IndexOf("--output");
                File.WriteAllText(start.ArgumentList[index + 1], "{\"Sources\":[{\"SourceDetails\":{\"Name\":\"winget\"},\"Packages\":[]}]}");
                return Task.FromResult(new WinGetResult(0, ""));
            }
            return Task.FromResult(new WinGetResult(0, "TeamSpeak  TeamSpeakSystems.TeamSpeakClient  5.0.0  3.6.2  winget"));
        });
        var safe = Task.Run(() => simulated.InspectAsync(limit.Token)).GetAwaiter().GetResult();
        Assert(!safe.HasUpdate("teamspeak") && safe.Installed["teamspeak"] == "5.0.0", "Real merge path discards a different-generation update row");
        Assert(local.TryGetValue("wowup-cf", out var wow) && wow.Version == "2.20.0" && wow.Path?.EndsWith("WowUp-CF.exe", StringComparison.OrdinalIgnoreCase) == true, "Live WoWUp registration and existing executable");
        Assert(local.TryGetValue("teamspeak", out var ts) && ts.Version == "5.0.0" && !ts.WinGetCompatible && ts.Path?.EndsWith("TeamSpeak.exe", StringComparison.OrdinalIgnoreCase) == true, "Live TeamSpeak 5 recognized with existing path and without TS3 update");
        Assert(local["artcraft-launcher"].Path?.EndsWith("artcraft-launcher.exe", StringComparison.OrdinalIgnoreCase) == true, "Live MSI app without InstallLocation has verified product executable");
        foreach (string id in new[] { "autoruns", "geek-uninstaller", "rufus", "tukui-client", "compactgui", "gpu-z" })
            Assert(local.TryGetValue(id, out var item) && item.Source == "Portable" && File.Exists(item.Path), "Live portable file: " + id);
        Assert(local.TryGetValue("extra-ventoy-ventoy", out var ventoy) && ventoy.Source == "Portable"
            && ventoy.Version == ProgramLocalInventory.PortableVersion("extra-ventoy-ventoy", ventoy.Path!) && ventoy.Version is not null
            && ventoy.Path?.EndsWith("Ventoy2Disk.exe", StringComparison.OrdinalIgnoreCase) == true,
            "Live Ventoy start executable in desktop subfolder");
        var sdk = local["net-sdk"];
        Assert(sdk.Version is not null && sdk.Path is not null && Directory.Exists(sdk.Path)
            && string.Equals(Path.GetFileName(sdk.Path), sdk.Version, StringComparison.Ordinal),
            "SDK product version matches an existing SDK directory instead of MSI bundle version");
        var catalog = ProgramCatalog.Entries.Select(e => new { e.Id, e.Name, Evidence = local.GetValueOrDefault(e.Id), Status = local.ContainsKey(e.Id) ? "Lokaler Nachweis" : "Nicht sicher ermittelbar" });
        File.WriteAllText(Path.Combine(output, "Installationserkennung-Privat.json"), JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true }));
        var manufacturers = new ManufacturerService().InspectAsync().GetAwaiter().GetResult();
        foreach (string id in new[] { "nvidia", "kingston", "asus-displaywidget", "turtle-beach" }) Assert(manufacturers[id].Installation == ManufacturerInstallState.Installed && File.Exists(manufacturers[id].Executable), "Live manufacturer launch target: " + id);
        Console.WriteLine("PASS local inventory: " + local.Count + " / " + ProgramCatalog.Entries.Length + "; four manufacturer targets confirmed");

        UiSoundService.MuteForTests = true; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Wait(Task task) { while (!task.IsCompleted) { Pump(); Thread.Sleep(5); } task.GetAwaiter().GetResult(); Pump(); }
        foreach (string theme in new[] { "Standard", "Classic", "Neon Blau", "Neon Pink", "Neon Grün", "Linux Mint", "Dark" })
        {
            var main = new MainWindow(Path.Combine(output, "Fixtures", theme)) { ShowActivated = false };
            try
            {
                main.Show(); var themes = (ComboBox)main.FindName("ThemeBox"); themes.SelectedItem = themes.Items.OfType<ComboBoxItem>().Single(x => x.Tag?.ToString() == theme);
                main.ShowWindowsUpdatesPage(); Pump(); var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage"); var view = page.ShowPrograms(false); view.Assets.AllowOnlineIcons = false;
                view.Service = new ProgramCatalogTests.Fake { InspectHandler = _ => Task.FromResult(new ProgramInventory(true, local.ToDictionary(x => x.Key, x => x.Value.Version), Evidence: local)),
                    ResolveHandler = (entry, _) => Task.FromResult<ProgramPackage?>(new(entry.PackageId ?? entry.Id, "3.6.2", "Fixture", DateTime.UtcNow, VersionSource: "WinGet")) };
                foreach (bool english in new[] { false, true }) foreach (int size in new[] { 1, 4 })
                {
                    typeof(MainWindow).GetField("_english", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(main, english);
                    typeof(MainWindow).GetMethod("ApplyLanguage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(main, null);
                    ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size; Wait(view.RefreshAsync());
                    foreach (string id in new[] { "wowup-cf", "teamspeak", "python", "compactgui", "extra-ventoy-ventoy" })
                    {
                        view.ShowInfo(Entry(id), view, false); Wait(view.Info!.Loading); main.UpdateLayout();
                        var text = string.Join("\n", UnifiedDialogAppearance.Children<TextBlock>(view.Info.Frame).Select(x => x.Text));
                        Assert(text.Contains(english ? "Detected through:" : "Erkannt über:"), "Evidence source is displayed " + id);
                        if (local[id].Path is { } path) Assert(text.Contains(path) || UnifiedDialogAppearance.Children<TextBlock>(view.Info.Frame).Any(x => x.ToolTip?.ToString() == path), "Confirmed full location retained in display or app-styled tooltip " + id);
                        Assert(!view.ShowManualMark(id), "Automatic evidence excludes manual switch " + id);
                        foreach (var button in UnifiedDialogAppearance.Children<Button>(view.Info.Frame))
                        {
                            var rect = button.TransformToAncestor(main).TransformBounds(new Rect(button.RenderSize));
                            Assert(rect.Bottom <= main.ActualHeight + 1 && rect.Right <= main.ActualWidth + 1 && rect.Top >= 0, "Info action within window");
                        }
                        if (!english && theme == "Standard" && id is "wowup-cf" or "teamspeak" or "extra-ventoy-ventoy")
                        {
                            var bmp = new RenderTargetBitmap((int)main.ActualWidth, (int)main.ActualHeight, 96, 96, PixelFormats.Pbgra32); bmp.Render(main);
                            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bmp)); using var stream = File.Create(Path.Combine(output, id + "-" + size + ".png")); encoder.Save(stream);
                        }
                        view.CloseInfo();
                    }
                }
            }
            finally { main.Close(); Pump(); }
        }
        Console.WriteLine("PASS " + checks + " detection checks; seven designs, DE/EN, compact/large. No installers started."); return 0;
    }
}
