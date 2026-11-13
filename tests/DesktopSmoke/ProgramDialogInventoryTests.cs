using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PowerControlProWpf;

internal static class ProgramDialogInventoryTests
{
    internal static int Run(App app, string output, bool observe, bool picturesOnly = false)
    {
        int checks = 0;
        void Assert(bool ok, string name) { if (!ok) throw new Exception(name); checks++; }
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        void Set(ProgramCatalogView view, ProgramInventory inventory) => typeof(ProgramCatalogView).GetProperty("Inventory", flags)!.SetValue(view, inventory);
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Wait(Task task) { while (!task.IsCompleted) { Pump(); Thread.Sleep(5); } task.GetAwaiter().GetResult(); Pump(); }
        var observations = new List<object>();
        UiSoundService.MuteForTests = true; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var main = new MainWindow(Path.Combine(output, "Fixture"), new AppSettings { Theme = "Standard", SizeIndex = 1 }) { ShowActivated = false };
        try
        {
            main.Show(); main.ShowWindowsUpdatesPage(); Pump();
            var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage"); var view = page.ShowPrograms(false); view.Assets.AllowOnlineIcons = false;
            view.Service = new ProgramCatalogTests.Fake { ResolveHandler = (entry, _) => Task.FromResult<ProgramPackage?>(entry.Id == "chatgpt" ? null : new(entry.PackageId!, entry.Id == "extra-electronicarts-eadesktop" ? "99.0.0" : "2.0", "Fixture", DateTime.UtcNow, VersionSource: "WinGet")) };
            var eaEntry = ProgramCatalog.Entries.Single(e => e.Id == "extra-electronicarts-eadesktop");
            var eaProof = observe ? null : ProgramLocalInventory.Read([eaEntry], CancellationToken.None, includeManaged: false).GetValueOrDefault(eaEntry.Id);
            var themes = (ComboBox)main.FindName("ThemeBox");
            var choices = themes.Items.OfType<ComboBoxItem>().Where(x => x.Tag is string).ToArray();
            foreach (var theme in observe || picturesOnly ? choices.Take(1) : choices)
            foreach (bool english in observe || picturesOnly ? new[] { false } : new[] { false, true })
            foreach (int size in observe ? new[] { 1 } : new[] { 1, 4 })
            {
                themes.SelectedItem = theme; ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size;
                page.SetLanguage(english); view.SetCategory("ai"); Pump();
                foreach (string id in observe ? new[] { "chatgpt", "extra-electronicarts-eadesktop" } : new[] { "chatgpt", "powertoys", "extra-electronicarts-eadesktop" })
                {
                    if (!observe && id == eaEntry.Id && eaProof is null) continue;
                    var entry = ProgramCatalog.Entries.Single(e => e.Id == id);
                    var local = id == "chatgpt" ? new Dictionary<string, string?>() : new Dictionary<string, string?> { [id] = "1.0" };
                    Set(view, new(false, local, IsPartial: true));
                    view.ShowInfo(entry, view, false); var info = view.Info!; Wait(info.Loading);
                    var frame = info.Frame; double width = frame.Width, height = frame.Height;
                    string folder = Path.Combine(output, "Known " + id); Directory.CreateDirectory(folder);
                    string version = id == "chatgpt" ? "1.2026.190.0" : id == eaEntry.Id && eaProof is not null ? eaProof.Version! : "1.0";
                    var final = new ProgramInventory(true, new Dictionary<string, string?> { [id] = version },
                        Updates: id == "powertoys" ? new Dictionary<string, string> { [id] = "2.0" } : null, UpdateCheckComplete: true,
                        InstalledSources: new Dictionary<string, string> { [id] = id == "chatgpt" ? "Windows-App" : "Registry" },
                        Evidence: new Dictionary<string, ProgramInstallation> { [id] = id == eaEntry.Id && eaProof is not null ? eaProof : new(version, id == "chatgpt" ? "Windows-App" : "Registry", folder, id != "chatgpt") });
                    Set(view, final); typeof(ProgramCatalogView).GetMethod("RenderTiles", flags)!.Invoke(view, null); Pump();
                    var texts = UnifiedDialogAppearance.Children<TextBlock>(frame).ToArray();
                    var buttons = UnifiedDialogAppearance.Children<Button>(frame).ToArray();
                    bool versionUpdated = texts.Single(t => t.Name == "ProgramInfoInstalledVersion").Text == version;
                    bool folderUpdated = buttons.Any(b => b.Visibility == Visibility.Visible && b.ToolTip?.ToString() == (english ? "Open program folder" : "Programmordner öffnen"));
                    bool updateVisible = buttons.Any(b => b.Name == "ProgramInfoUpdateAction" && b.Visibility == Visibility.Visible);
                    bool publisherVisible = buttons.Any(b => b.Name == "ProgramInfoOpenPublisherApp" && b.Visibility == Visibility.Visible);
                    bool sourceUpdated = id != "chatgpt" || texts.Any(t => t.Text.Contains("Windows-App", StringComparison.OrdinalIgnoreCase) || t.Text.Contains("Microsoft Store", StringComparison.OrdinalIgnoreCase) || t.Text.Contains("Windows app", StringComparison.OrdinalIgnoreCase));
                    observations.Add(new { Theme = theme.Tag, English = english, Size = size, Id = id, VersionUpdated = versionUpdated, FolderUpdated = folderUpdated, UpdateVisible = updateVisible, SourceUpdated = sourceUpdated });
                    if (!observe)
                    {
                        if (!english && theme.Tag?.ToString() == "Standard" && id != "powertoys")
                        {
                            main.UpdateLayout(); frame.UpdateLayout();
                            foreach (var text in UnifiedDialogAppearance.Children<TextBlock>(frame)) text.InvalidateVisual();
                            main.InvalidateVisual(); Pump(); main.UpdateLayout();
                            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(main.ActualWidth), (int)Math.Ceiling(main.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                            bitmap.Render(main);
                            var bounds = frame.TransformToAncestor(main).TransformBounds(new Rect(frame.RenderSize));
                            var crop = new CroppedBitmap(bitmap, new Int32Rect((int)Math.Floor(bounds.X), (int)Math.Floor(bounds.Y), (int)Math.Floor(bounds.Width), (int)Math.Floor(bounds.Height)));
                            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(crop));
                            using var image = File.Create(Path.Combine(output, "Dialog-" + id + "-" + size + ".png")); png.Save(image);
                        }
                        Assert(versionUpdated, "Late inventory must update installed version: " + id);
                        Assert(folderUpdated, "Late inventory must replace missing-file chooser with folder action: " + id);
                        Assert(sourceUpdated, "Store source follows late inventory");
                        bool actionCorrect = id == "chatgpt" || (id == eaEntry.Id ? publisherVisible && !updateVisible : updateVisible);
                        Assert(actionCorrect, "Late update gets its verified or publisher action: " + id);
                        Assert(ReferenceEquals(frame, view.Info?.Frame), "Inventory refresh must preserve the same open dialog");
                        Assert(frame.Width == width && frame.Height == height, "Inventory refresh must preserve dialog dimensions");
                        if (id == eaEntry.Id && !english && size == 1 && theme.Tag?.ToString() == "Standard")
                        {
                            var previousLaunch = ProgramPublisherLauncher.Launch;
                            var launched = new TaskCompletionSource<System.Diagnostics.ProcessStartInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
                            try
                            {
                                ProgramPublisherLauncher.Launch = command => launched.TrySetResult(command);
                                ((CancellationTokenSource)typeof(ProgramInfoCard).GetField("_cancel", flags)!.GetValue(info)!).Cancel();
                                buttons.Single(b => b.Name == "ProgramInfoOpenPublisherApp").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                                Wait(launched.Task.WaitAsync(TimeSpan.FromSeconds(12))); Pump();
                                Assert(Path.GetFileName(launched.Task.Result.FileName) == "EADesktop.exe" && launched.Task.Result.Arguments.Length == 0, "EA button targets the reverified main app without installer arguments");
                                Assert(!info.IsOpen && !view.IsWorking, "EA button closes info without starting an installation queue");
                            }
                            finally { ProgramPublisherLauncher.Launch = previousLaunch; }
                        }
                        if (id == "chatgpt")
                        {
                            Set(view, final with { Complete = false, StoreCheckComplete = false, StoreError = "Fixture Store unavailable" });
                            typeof(ProgramCatalogView).GetMethod("RenderTiles", flags)!.Invoke(view, null); Pump();
                            Assert(UnifiedDialogAppearance.Children<TextBlock>(frame).Single(t => t.Name == "ProgramInfoInstalledVersion").Text == version, "Failed recheck keeps last confirmed package version");
                            Assert(UnifiedDialogAppearance.Children<TextBlock>(frame).Any(t => t.Text.Contains(english ? "last confirmed" : "letzter bestätigter", StringComparison.OrdinalIgnoreCase)), "Retained package version explicitly labeled as last confirmed");
                            Set(view, final with { Complete = false, Installed = new Dictionary<string, string?>(), Evidence = new Dictionary<string, ProgramInstallation>(), StoreCheckComplete = false, StoreError = "Fixture Store unavailable" });
                            typeof(ProgramCatalogView).GetMethod("RenderTiles", flags)!.Invoke(view, null); Pump();
                            Assert(!view.ShowManualMark(id) && !UnifiedDialogAppearance.Children<System.Windows.Controls.CheckBox>(frame).Any(c => c.IsVisible), "Unknown failed Store check cannot become manual-installed toggle");
                            Assert(UnifiedDialogAppearance.Children<TextBlock>(frame).Single(t => t.Name == "ProgramInfoInstalledVersion").Text == (english ? "Check failed" : "Prüfung fehlgeschlagen"), "Failed Store enumeration is different from absent installation");
                        }
                    }
                    view.CloseInfo();
                }
            }
            File.WriteAllText(Path.Combine(output, observe ? "Before-Dialog-Race.json" : "Dialog-Inventory-Checks.json"), JsonSerializer.Serialize(new { Checks = checks, Observations = observations }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(observe ? "OBSERVED baseline dialog inventory behavior; no installed apps changed." : $"PASS {checks} late-dialog inventory checks; " + (picturesOnly ? "Standard/DE image pass, compact/large." : "seven themes, DE/EN, compact/large."));
            return 0;
        }
        finally { main.Close(); Pump(); }
    }
}
