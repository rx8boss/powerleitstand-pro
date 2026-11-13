using System.Diagnostics;
using System.IO;
using System.Text.Json;
using PowerControlProWpf;

internal static class ProgramScoopGitTests
{
    internal static async Task<int> Run(string output, bool live)
    {
        int checks = 0; void Assert(bool condition, string why) { if (!condition) throw new Exception(why); checks++; }
        string? parentPath = Environment.GetEnvironmentVariable("PATH");
        if (live)
        {
            var roots = ProgramManagedInventory.Roots().ToArray(); var root = roots.First(r => r.Manager == "Scoop" && r.Scope == "User");
            var target = new ProgramManagedTarget("Scoop", root.Directory, "", "7zip", "User", "main", "64bit", null, null, "", null);
            string git = ProgramScoopGit.Find(target, roots) ?? throw new Exception("No verified local Git found");
            Assert(ProgramScoopGit.ConfirmFile(git), "Local Git product/publisher/PE/version confirmed");
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true };
            string ordinaryPath = string.Join(';', new[] { Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine), Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) }.Where(v => !string.IsNullOrWhiteSpace(v)));
            start.Environment["PATH"] = ordinaryPath; ProgramScoopGit.Configure(start, git);
            foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference='Stop'; $git=(Get-Command git.exe -CommandType Application -ErrorAction Stop).Source; [Console]::WriteLine($git); & $git --version; exit $LASTEXITCODE" }) start.ArgumentList.Add(argument);
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var result = await WinGetService.QueryAsync(start, limit.Token);
            File.WriteAllText(Path.Combine(output, "Git-Kindprozess-Privat.json"), JsonSerializer.Serialize(new { Git = git, result.ExitCode, result.Output, OrdinaryPathWithoutCodexRuntime = ordinaryPath }, new JsonSerializerOptions { WriteIndented = true }));
            Assert(result.ExitCode == 0 && result.Output.Contains(git, StringComparison.OrdinalIgnoreCase) && result.Output.Contains("git version "), "Real hidden PowerShell resolves verified Git without Codex PATH and runs only --version");
            Assert(Environment.GetEnvironmentVariable("PATH") == parentPath, "Native child probe leaves parent PATH unchanged");
            Assert(!Directory.Exists(Path.Combine(root.Directory, "apps", "git", "current")) && !File.Exists(Path.Combine(root.Directory, "shims", "git.exe")), "Native probe reproduces missing current and shim without repairing them");
            Console.WriteLine($"PASS {checks} local Git identity/child-process/version checks; no Scoop command, install or network operation."); return 0;
        }
        string directory = Path.Combine(output, "Git with spaces", "cmd"); Directory.CreateDirectory(directory);
        string fake = Path.Combine(directory, "git.exe"); File.Copy(Path.Combine(AppContext.BaseDirectory, "DesktopSmoke.exe"), fake, true);
        Assert(!ProgramScoopGit.ConfirmFile(fake), "Unrelated executable renamed git.exe is rejected");
        Assert(ProgramScoopGit.FirstConfirmed([fake]) is null, "Unrelated candidate does not become a helper");
        string? selected = ProgramScoopGit.FirstConfirmed([Path.Combine(output, "missing.exe"), fake], path => path == fake);
        Assert(selected == fake, "Candidate resolver retains confirmed full path with spaces");
        int inspections = 0;
        Assert(ProgramScoopGit.FirstConfirmed(["git.exe", "../git.exe", @"\\invalid\share\git.exe", @"I:\invalid;other\git.exe"], _ => { inspections++; return true; }) is null && inspections == 0,
            "Relative, UNC and PATH-separator candidates never reach executable validation");
        Assert(ProgramScoopGit.FirstConfirmed([fake, fake], _ => { inspections++; return false; }) is null && inspections == 1, "Duplicate candidates are validated once");
        var startInfo = new ProcessStartInfo("fixture-only-never-run") { UseShellExecute = false, CreateNoWindow = true };
        startInfo.Environment["PATH"] = @"C:\Windows\System32;C:\Other folder";
        string original = startInfo.Environment["PATH"]!; ProgramScoopGit.Configure(startInfo, selected);
        Assert(startInfo.Environment["PATH"] == directory + ";" + original, "Only child PATH is prepended and inherited entries preserved exactly");
        Assert(Environment.GetEnvironmentVariable("PATH") == parentPath, "Configuring child environment does not change parent PATH");
        var empty = new ProcessStartInfo("fixture-only-never-run"); empty.Environment["PATH"] = ""; ProgramScoopGit.Configure(empty, fake);
        Assert(empty.Environment["PATH"] == directory, "Empty child PATH has no spurious empty search component");
        foreach (string? missing in new[] { null, Path.Combine(output, "absent", "git.exe"), @"I:\bad;path\git.exe" })
        {
            string before = startInfo.Environment["PATH"]!; string? message = null;
            try { ProgramScoopGit.Configure(startInfo, missing); } catch (IOException error) { message = error.Message; }
            Assert(message == ProgramScoopGit.Missing && startInfo.Environment["PATH"] == before, "Missing/unusable helper fails before modifying child command");
        }
        File.WriteAllText(fake, "not a PE"); Assert(!ProgramScoopGit.ConfirmFile(fake), "Text file cannot masquerade as Git");
        foreach (string message in new[] { ProgramScoopGit.Missing, ProgramScoopGit.Unusable, ProgramScoopGit.PreparationFailed })
        {
            Assert(UiText.Error(new IOException(message), false) == message, "German error remains in German");
            Assert(UiText.Error(new IOException(message), true) != message && !UiText.Error(new IOException(message), true).Contains(" / "), "English error is localized without bilingual concatenation");
        }
        Console.WriteLine($"PASS {checks} Git prerequisite/path/identity/localization checks; fixture commands never executed."); return 0;
    }
}
