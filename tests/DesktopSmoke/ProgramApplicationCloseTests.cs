using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class ProgramApplicationCloseTests
{
    internal static int Child(string marker,bool block,bool hidden)
    {
        var app=new System.Windows.Application();bool cleanup=false;
        var window=new Window{Title="Owned application-close test",Width=160,Height=80,Left=-10000,Top=-10000,ShowActivated=false};
        window.Closing+=(_,e)=>{if(block&&!cleanup)e.Cancel=true;else File.WriteAllText(marker+".closed","window close received");};
        app.SessionEnding+=(_,e)=>{if(block&&!cleanup)e.Cancel=true;else File.WriteAllText(marker+".closed","session close received");};
        var exit=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};var clock=Stopwatch.StartNew();
        exit.Tick+=(_,_)=>{if(File.Exists(marker+".exit")||clock.Elapsed.TotalSeconds>90){cleanup=true;app.Shutdown();}};
        window.Loaded+=(_,_)=>{if(hidden)window.Hide();File.WriteAllText(marker+".ready",Environment.ProcessId.ToString());exit.Start();};
        app.Run(window);return 0;
    }
    internal static async Task<int> Run(string output)
    {
        int checks=0;void Assert(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
        string root=Path.Combine(output,"Discord");Directory.CreateDirectory(Path.Combine(root,"app-1.0.1"));Directory.CreateDirectory(Path.Combine(root,"app-1.0.2"));
        var discord=ProgramCatalog.Entries.Single(e=>e.Id=="discord");
        var proof=new ProgramInstallation("1.0.2","Registry",Path.Combine(root,"app-1.0.2","Discord.exe"));
        Assert(ProgramApplicationCloser.MatchesPath(discord,proof,Path.Combine(root,"app-1.0.1","Discord.exe")),"Squirrel's older running copy is scoped to the same registered app root");
        Assert(!ProgramApplicationCloser.MatchesPath(discord,proof,Path.Combine(output,"DiscordCanary","app-1.0.1","Discord.exe")),"A different channel outside the registered root is not targeted");
        string desktop=Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        Assert(!ProgramApplicationCloser.MatchesPath(discord,new("1.0","Portable",Path.Combine(desktop,"Discord.exe"),false),Path.Combine(desktop,"Other.exe")),"A portable EXE never owns other programs sharing Desktop");
        Assert(!ProgramApplicationCloser.MatchesPath(discord,new("1.0","Registry",desktop),Path.Combine(desktop,"Discord.exe")),"A broad registered folder is not an application root");
        Assert(!ProgramApplicationCloser.MatchesPath(discord,null,Path.Combine(root,"Discord.exe")),"No location cannot authorize a process close");
        string clone=Path.Combine(output,"NativeFixture");Directory.CreateDirectory(clone);
        foreach(string file in Directory.EnumerateFiles(AppContext.BaseDirectory))File.Copy(file,Path.Combine(clone,Path.GetFileName(file)),true);
        string executable=Path.Combine(clone,"DesktopSmoke.exe");var info=FileVersionInfo.GetVersionInfo(executable);
        var entry=new ProgramEntry("owned-close-fixture",info.ProductName!,["tools"],null,info.CompanyName!,"fixture","fixture","https://example.invalid","",null,null,"1");
        var fixtureProof=new ProgramInstallation(info.FileVersion,"Portable",executable,false);
        foreach(var kind in new[]{"window","hidden","refuse"})
        {
            string marker=Path.Combine(output,kind);var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
            foreach(string arg in new[]{"--application-close-child",marker,kind})start.ArgumentList.Add(arg);
            using var process=Process.Start(start)??throw new Exception("Owned child did not start");
            try
            {
                var ready=Stopwatch.StartNew();while(!File.Exists(marker+".ready")&&!process.HasExited&&ready.Elapsed.TotalSeconds<25)await Task.Delay(50);
                Assert(File.Exists(marker+".ready"),"Owned close fixture becomes ready: "+kind);
                var found=ProgramApplicationCloser.Find(entry,fixtureProof,default);
                Assert(found.Count==1&&found[0].Id==process.Id&&found[0].Started==process.StartTime.ToUniversalTime().ToFileTimeUtc(),"Only the private owned process and exact start time are identified: "+kind);
                Assert(!ProgramApplicationCloser.Alive(found[0] with{Started=found[0].Started+1}),"Same PID with a different start time is not the captured process instance");
                Assert(ProgramApplicationCloser.Find(entry,fixtureProof with{Path=Environment.ProcessPath},default).Count==0,"Another copy of the same product is not closed");
                bool unknown=false;try{ProgramApplicationCloser.Find(entry,null,default);}catch(IOException){unknown=true;}
                Assert(unknown&&!process.HasExited,"Missing location cannot close a matching but unassociated process");
                using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(kind=="refuse"?8:20));bool failed=false,closed=false;
                try{closed=await ProgramApplicationCloser.CloseAsync(entry,fixtureProof,limit.Token);}catch(Exception error)when(error is IOException or OperationCanceledException){failed=true;File.WriteAllText(marker+".error",error.ToString());Console.WriteLine("Native close "+kind+": "+error.Message);}
                if(kind=="refuse")Assert(failed&&!process.HasExited&&!File.Exists(marker+".closed"),"Refusing/saving application is not forcibly terminated");
                else{Assert(closed&&!failed,"Windows gracefully closes the owned fixture: "+kind);await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));Assert(File.Exists(marker+".closed"),"The fixture handled a graceful close notification: "+kind);}
            }
            finally
            {
                File.WriteAllText(marker+".exit","owned test cleanup");
                if(!process.HasExited)
                {
                    try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));}
                    catch(TimeoutException)
                    {
                        if(process.MainModule?.FileName!=executable)throw;
                        process.Kill();await process.WaitForExitAsync();throw new Exception("Owned close fixture did not honor its cleanup marker");
                    }
                }
            }
        }
        Console.WriteLine("PASS "+checks+" application identity/native graceful-close checks; owned fixtures only");return 0;
    }
}
