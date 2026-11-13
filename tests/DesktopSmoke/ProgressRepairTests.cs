using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class ProgressRepairTests
{
    internal static async Task<int> ConfirmExisting(string output, string msi, string expectedHash)
    {
        if (!ProgramPortableUpdates.Hash(msi).Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Official artifact hash differs from prior verified download.");
        var service = new TeamSpeakInfoService(); var snapshot = await service.ReadLocalAsync(CancellationToken.None);
        var copy = snapshot.Copies.Single(x => x.Edition == 6 && x.Source == "Registry" && x.Path is not null);
        var request = new TeamSpeakInstallRequest(6, "6.0.0-beta5", TeamSpeakInstallKind.Reinstall, snapshot, copy);
        var plan = new TeamSpeakInstallPlan(request, TeamSpeakInstaller.Url(6, request.Version), null, null);
        var metadata = TeamSpeakInstaller.Validate(msi, plan);
        var found = TeamSpeakInstaller.VerifiedCopy(request, metadata, snapshot, out var reason) ?? throw new IOException(reason);
        TeamSpeakInstallReceipts.Save(found, request.Version);
        var checkedAgain = await service.ReadLocalAsync(CancellationToken.None);
        if (checkedAgain.Copies.Single(x => x.Edition == 6).Version != request.Version) throw new IOException("Persisted verification not accepted.");
        File.WriteAllText(Path.Combine(output, "TeamSpeak-Bestaetigt.json"), JsonSerializer.Serialize(new { Verified = true, NumericVersion = TeamSpeakInfoService.FixedFileVersion(FileVersionInfo.GetVersionInfo(found.Path!))?.ToString(), Version = request.Version, found.Path, found.ProductCode, InstallerExecuted = false }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS actual existing TeamSpeak 6 verified using numeric version, MSI identity and publisher signatures; receipt saved; no installer executed"); return 0;
    }
    internal static int Run(App app, string output)
    {
        int count = 0; void Assert(bool ok, string label) { if (!ok) throw new Exception(label); count++; }
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher)); UiSoundService.MuteForTests = true;
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Wait(Task task) { var watch = Stopwatch.StartNew(); while (!task.IsCompleted && watch.Elapsed.TotalSeconds < 20) { Pump(); Thread.Sleep(4); } if (!task.IsCompleted) throw new TimeoutException(); task.GetAwaiter().GetResult(); Pump(); }
        Assert(TeamSpeakInfoService.FixedFileVersion(6, 0, 0, 5163)?.ToString() == "6.0.0.5163", "Numeric fixed version preserves real TeamSpeak revision despite broken text field");
        Assert(TeamSpeakInfoService.FixedFileVersion(0, 0, 0, 0) is null && TeamSpeakInfoService.FixedFileVersion(6, -1, 0, 0) is null, "Missing or invalid numeric resource is not invented");
        var copy = new TeamSpeakCopy(6, "6.0.0.0", Path.Combine(output, "TeamSpeak.exe"), "Registry", "{C6F94575-927B-4BD1-BE22-BA6FE3552FC7}", "user");
        var request = new TeamSpeakInstallRequest(6, "6.0.0-beta5", TeamSpeakInstallKind.Install, new([]), null);
        var metadata = new TeamSpeakMsiInfo("TeamSpeak", "TeamSpeak", "6.0.0", copy.ProductCode!, "6.0.0.5163", "x64", "1", "");
        TeamSpeakCopy? Verify(TeamSpeakSnapshot snapshot, TeamSpeakMsiInfo data, Version? fixedValue, bool signature = true) => TeamSpeakInstaller.VerifiedCopy(request, data, snapshot, out _, _ => fixedValue, _ => signature);
        Assert(Verify(new([copy]), metadata, new(6,0,0,5163)) == copy, "Correct fixed version confirms installed MSI");
        Assert(Verify(new([copy]), metadata, new(6,0,0,5162)) is null, "Wrong numeric revision cannot confirm target");
        Assert(Verify(new([copy]), metadata, null) is null && Verify(new([copy]), metadata, new(6,0,0,5163), false) is null, "Absent numeric version or untrusted signature prevents confirmation");
        Assert(Verify(new([copy with { ProductCode = Guid.NewGuid().ToString("B") }]), metadata, new(6,0,0,5163)) is null, "Wrong MSI identity prevents confirmation");
        Assert(Verify(new([copy],false), metadata, new(6,0,0,5163)) is null && Verify(new([copy,copy]), metadata, new(6,0,0,5163)) is null, "Incomplete or multiple targets remain blocked");
        foreach (bool german in new[]{true,false})
        {
            var events = new List<ProgramStepEvent>(); var bytes = new List<ProgramDownload>(); var parser = new ProgramWinGetSteps(events.Add, "Vendor.Main", bytes.Add);
            parser.Line("\uFEFF\u001b[32m" + (german ? "Download läuft" : "Downloading") + " https://example.invalid/main.exe\u001b[0m");
            parser.Line("  25,5 MB / 100 MB"); parser.Line(german ? "Der Installer-Hash wurde erfolgreich überprüft" : "Successfully verified installer hash");
            parser.Line(german ? "Paketinstallation wird gestartet..." : "Starting package install...");
            parser.Line(german ? "Erfolgreich installiert" : "Successfully installed");
            Assert(parser.DownloadConfirmed && bytes.Single().Percent == 25.5 && events.Contains(new(ProgramStep.Download, ProgramStepState.Done)), "Localized UTF8/ANSI/BOM download and measured percentage accepted");
            parser.Line("Downloading https://example.invalid/late.exe"); Assert(events.Count(e => e == new ProgramStepEvent(ProgramStep.Download, ProgramStepState.Active)) == 1, "Late download start cannot regress installation");
        }
        {
            var events = new List<ProgramStepEvent>(); var bytes = new List<ProgramDownload>(); var api = new ProgramApiSteps(events.Add, bytes.Add);
            api.Report(new(ProgramApiPhase.Installing, 100, 100)); Assert(events[0] == new ProgramStepEvent(ProgramStep.Download, ProgramStepState.Done) && bytes.Single().Percent == 100, "Final API counters recover a missed downloading callback");
            events.Clear(); api = new(events.Add, bytes.Add); api.Report(new(ProgramApiPhase.Installing, 0, 0)); api.Complete(true); Assert(events[0].State == ProgramStepState.Unknown, "No callback or counters is not a guessed cache hit");
            events.Clear(); int beforeLate=bytes.Count; api = new(events.Add, bytes.Add); api.Report(new(ProgramApiPhase.Installing, 0, 0)); api.Report(new(ProgramApiPhase.Installing, 200, 200)); Assert(bytes.Count==beforeLate&&events.Count(e=>e==new ProgramStepEvent(ProgramStep.Download,ProgramStepState.Done))==1&&events.Count(e=>e==new ProgramStepEvent(ProgramStep.Install,ProgramStepState.Active))==1,"Late final counters confirm download without restarting installation or changing its progress bar");
            events.Clear(); api = new(events.Add, bytes.Add); api.Report(new(ProgramApiPhase.Downloading, 12, 0)); api.Report(new(ProgramApiPhase.Installing, 0, 0)); Assert(events.Contains(new(ProgramStep.Download,ProgramStepState.Done)), "Observed download followed by installing confirms artifact acceptance");
        }
        {
            int api=0,cli=0;
            int result=Task.Run(()=>ProgramWinGetApi.PreferAsync(()=>Task.FromException<Func<Task<int>>>(new ProgramApiUnavailableException("Fixture unavailable before start")),()=>{cli++;return Task.FromResult(0);},_=>{})).GetAwaiter().GetResult();
            Assert(result==0&&cli==1,"Unavailable API falls back once before operation start");
            bool failed=false;try{Task.Run(()=>ProgramWinGetApi.PreferAsync(()=>Task.FromResult<Func<Task<int>>>(()=>{api++;throw new ProgramApiUnavailableException("Fixture failure after start");}),()=>{cli++;return Task.FromResult(0);},_=>{})).GetAwaiter().GetResult();}catch(ProgramApiUnavailableException){failed=true;}
            Assert(failed&&api==1&&cli==1,"Failure after API operation start never launches a second installer");
        }
        {
            var events = new List<ProgramStepEvent>(); var bytes = new List<ProgramDownload>(); var parser = new ProgramWinGetSteps(events.Add, null, bytes.Add);
            var start = new ProcessStartInfo(Environment.ProcessPath!); start.ArgumentList.Add("--download-child-fixture");
            bool whileRunning=false; Task<WinGetResult>? running=null;
            running=ProgramInstallService.RunHiddenAsync(start,()=>{},line=>{parser.Line(line);if(parser.DownloadConfirmed && running is {IsCompleted:false})whileRunning=true;}); Wait(running);
            Assert(running.Result.ExitCode == 0 && whileRunning && parser.DownloadConfirmed, "Actual hidden child reports UTF8 stderr milestones before exit");
            Assert(bytes.Single().Percent == 25 && events.Contains(new(ProgramStep.Install,ProgramStepState.Done)), "Actual stdout carriage-return bytes and stderr proof are both retained");
        }
        var main = new MainWindow(Path.Combine(output,"Fixture"),new AppSettings{Theme="Standard",SizeIndex=1}){ShowActivated=false};
        void Save(string name)
        {Pump();var bitmap=new RenderTargetBitmap((int)main.ActualWidth,(int)main.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(main);var image=new PngBitmapEncoder();image.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,name+".png"));image.Save(file);}
        try
        {
            main.Show();main.ShowWindowsUpdatesPage();Pump();var page=(WindowsUpdatesPage)main.FindName("WindowsUpdatesPage");var view=page.ShowPrograms(false);view.Assets.AllowOnlineIcons=false;
            var first=ProgramCatalog.Entries.First(x=>x.Id=="firefox");var second=ProgramCatalog.Entries.First(x=>x.Id=="7-zip");
            foreach(var theme in ((ComboBox)main.FindName("ThemeBox")).Items.OfType<ComboBoxItem>().Where(x=>x.Tag is string).ToArray())
            foreach(bool english in new[]{false,true})foreach(int size in new[]{1,4})
            {
                ((ComboBox)main.FindName("ThemeBox")).SelectedItem=theme;((ComboBox)main.FindName("SizeBox")).SelectedIndex=size;page.SetLanguage(english);Pump();
                using var panel=new ProgramProgressPanel(view,[first,second]);panel.Current(first,english?"Download":"Download");
                panel.Step(first.Id,new(ProgramStep.Check,ProgramStepState.Done));panel.Step(first.Id,new(ProgramStep.Download,ProgramStepState.Active));panel.Download(new(25,100));Pump();
                Assert(!panel.Progress.IsIndeterminate&&panel.Progress.ShowPercentage&&panel.Progress.Value==25,"Real percentage visible in all sizes/themes/languages");
                if(theme.Tag?.ToString()=="Standard"&&!english)Save("Download-Prozent-"+size);
                Assert(panel.StepState(second.Id,ProgramStep.Download)==ProgramStepState.Waiting,"Other program retains independent download status");
                panel.Step(first.Id,new(ProgramStep.Download,ProgramStepState.Done));panel.Step(first.Id,new(ProgramStep.Install,ProgramStepState.Active));Pump();
                Assert(UnifiedDialogAppearance.Children<TextBlock>(panel.Frame).Any(t=>t.Text=="Download ✓"&&t.Foreground is SolidColorBrush color&&color.Color.G>color.Color.R),"Confirmed download gets green check");
                if(theme.Tag?.ToString()=="Standard"&&!english)Save("Download-Bestaetigt-"+size);
                panel.Current(second,english?"Installation":"Installation");panel.Step(second.Id,new(ProgramStep.Download,ProgramStepState.Unknown));panel.Step(second.Id,new(ProgramStep.Install,ProgramStepState.Active));Pump();
                Assert(panel.Progress.IsIndeterminate&&!panel.Progress.ShowPercentage,"Unknown progress remains animated without invented percentage");
                Assert(UnifiedDialogAppearance.Children<TextBlock>(panel.Frame).Any(t=>t.Text.Contains(english?"WinGet did not report":"Downloadfortschritt von WinGet")),"Missing download gets visible translated explanation");
                Assert(UnifiedDialogAppearance.Children<TextBlock>(panel.Frame).Any(t=>t.Text=="Download ?"),"Unknown download no longer an unexplained dash");
                foreach(var button in UnifiedDialogAppearance.Children<Button>(panel.Frame)){var p=button.TranslatePoint(new Point(),panel.Frame);Assert(p.X>=-1&&p.X+button.ActualWidth<=panel.Frame.ActualWidth+1&&p.Y+button.ActualHeight<=panel.Frame.ActualHeight+1,"Progress actions inside frame");}
                if(theme.Tag?.ToString()=="Standard"&&!english){var bitmap=new RenderTargetBitmap((int)main.ActualWidth,(int)main.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(main);var image=new PngBitmapEncoder();image.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,"Download-Hinweis-"+size+".png"));image.Save(file);}
                panel.State(second.Id,english?"Verified":"Bestätigt");panel.Finish(false);Pump();Assert(UnifiedDialogAppearance.Children<TextBlock>(panel.Frame).Any(t=>t.Text.Contains(english?"Question mark":"Fragezeichen")),"Finished view explains unreported stages");
            }
        }
        finally {((WindowsUpdatesPage)main.FindName("WindowsUpdatesPage")).ProgramView?.CloseInfo();main.Close();Pump();}
        Console.WriteLine("PASS "+count+" progress/TeamSpeak numeric-version repair checks; real UTF8 child, seven themes, DE/EN, compact/large; no third-party installer executed");return 0;
    }
}
