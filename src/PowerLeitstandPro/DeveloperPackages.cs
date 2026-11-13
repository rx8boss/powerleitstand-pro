using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace PowerControlProWpf;

internal sealed record DeveloperPackage(string Id, string Version, string Description, string Authors, string License, string Dependencies, string? Location = null);

// These are development packages, not executable desktop applications or project references.
internal sealed class MissingPSResourceGetException()
    : IOException("PSResourceGet fehlt. / PSResourceGet is missing.") { }

internal sealed class BrokenPSResourceGetException(string manifest, string details)
    : IOException("PowerShell/PSResourceGet: " + details)
{
    internal string Manifest { get; } = manifest;
}

internal sealed partial class DeveloperPackages
{
    internal const string NuGetSource = "https://api.nuget.org/v3/index.json";
    internal const string GallerySource = "https://www.powershellgallery.com/api/v2";
    private static readonly ISet<string> Hosts = new HashSet<string>(["api.nuget.org", "azuresearch-usnc.nuget.org", "azuresearch-ussc.nuget.org"], StringComparer.OrdinalIgnoreCase);
    internal string? NuGetExecutable { get; set; }
    internal string? NuGetDirectory { get; set; }
    internal string Shell { get; }
    private readonly Func<Uri, CancellationToken, Task<byte[]>> _read;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<WinGetResult>> _run;
    internal DeveloperPackages(Func<Uri, CancellationToken, Task<byte[]>>? read = null,
        Func<ProcessStartInfo, CancellationToken, Task<WinGetResult>>? run = null, string? shell = null)
    {
        _read = read ?? ((uri, token) => CatalogWeb.ReadAsync(uri, Hosts, 2 * 1024 * 1024, token));
        _run = run ?? WinGetService.QueryAsync;
        var pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        Shell = shell ?? (File.Exists(pwsh) ? pwsh : Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"));
    }
    internal static void Validate(string id, string? version = null)
    {
        if (!Regex.IsMatch(id, @"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,99}\z")
            || version is not null && !Regex.IsMatch(version, @"\A[0-9]+(?:\.[0-9]+){1,3}(?:-[A-Za-z0-9.-]+)?(?:\+[A-Za-z0-9.-]+)?\z"))
            throw new IOException("Paketkennung oder Version ungültig. / Invalid package ID or version.");
    }
    internal static IReadOnlyList<DeveloperPackage> ReadNuGet(string? directory)
    {
        if (directory is null || !Directory.Exists(directory)) return [];
        ValidateDirectory(directory);
        var result = new List<DeveloperPackage>();
        foreach (string folder in Directory.EnumerateDirectories(directory).Take(2000))
        {
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) continue;
            foreach (string file in Directory.EnumerateFiles(folder, "*.nuspec").Take(1))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 || new FileInfo(file).Length > 2 * 1024 * 1024) continue;
                try
                {
                    using var reader = XmlReader.Create(file, new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 });
                    var xml = XDocument.Load(reader); string Field(string name) => xml.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? "";
                    var id = Field("id"); var version = Field("version"); Validate(id, version);
                    result.Add(new(id, version, Field("description"), Field("authors"), Field("license"), string.Join("; ", xml.Descendants().Where(e => e.Name.LocalName == "dependency").Select(e => (string?)e.Attribute("id") + " " + (string?)e.Attribute("version"))), folder));
                }
                catch (Exception e) when (e is XmlException or IOException) { /* An unrelated or malformed folder is not an installed package. */ }
            }
        }
        return result;
    }
    internal static void ValidateDirectory(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)) throw new IOException("Paketordner nicht vorhanden. / Package directory does not exist.");
        for (string? part = Path.GetFullPath(directory); part is not null; part = Path.GetDirectoryName(part))
            if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new IOException("Verknüpfter Paketordner nicht unterstützt. / Linked package directory is not supported.");
    }
    internal async Task<IReadOnlyList<DeveloperPackage>> SearchAsync(bool nuget, string query, CancellationToken token)
    {
        if (query.Length is < 2 or > 80 || !Regex.IsMatch(query, @"\A[A-Za-z0-9_. -]+\z"))
            throw new IOException("Bitte 2–80 Buchstaben, Ziffern, Punkte oder Bindestriche eingeben. / Enter 2–80 letters, digits, dots or hyphens.");
        OnlineConnection.RequireNetwork();
        if (!nuget) return await PowerShellAsync("search", query, null, token);
        using var index = JsonDocument.Parse(await _read(new(NuGetSource), token));
        var endpoint = index.RootElement.GetProperty("resources").EnumerateArray().First(e => e.GetProperty("@type").ToString().StartsWith("SearchQueryService", StringComparison.Ordinal)).GetProperty("@id").GetString()!;
        var uri = new Uri(endpoint + "?q=" + Uri.EscapeDataString(query) + "&take=40&prerelease=false&semVerLevel=2.0.0");
        if (!CatalogWeb.IsAllowed(uri, Hosts)) throw new IOException("NuGet-Quelle nicht bestätigt. / NuGet source not verified.");
        using var data = JsonDocument.Parse(await _read(uri, token));
        return data.RootElement.GetProperty("data").EnumerateArray().Take(40).Select(e =>
        {
            string Text(string key) => e.TryGetProperty(key, out var p) ? p.ToString() : "";
            var id = Text("id"); var version = Text("version"); Validate(id, version);
            return new DeveloperPackage(id, version, Text("description"), Text("authors"), Text("licenseUrl"), "");
        }).ToArray();
    }
    internal Task<IReadOnlyList<DeveloperPackage>> InstalledAsync(bool nuget, CancellationToken token) => nuget
        ? Task.Run(() => ReadNuGet(NuGetDirectory), token) : PowerShellAsync("installed", "", null, token);

    private const string Script = """
        $ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
        try {
          $module = Get-Module -ListAvailable Microsoft.PowerShell.PSResourceGet | Select-Object -First 1
          if (!$module) { exit 42 }
          try {
            Import-Module -Name $module.Path -ErrorAction Stop
          } catch {
            [Console]::Out.WriteLine('PL_MODULE_MANIFEST:' + $module.Path)
            [Console]::Error.WriteLine($_.Exception.Message)
            exit 43
          }
          $prefix='Microsoft.PowerShell.PSResourceGet\'
          if($env:PL_PACKAGE_ACTION -ne 'installed') {
            $repo=Microsoft.PowerShell.PSResourceGet\Get-PSResourceRepository -Name PSGallery -ErrorAction Stop
            if($repo.Uri.AbsoluteUri.TrimEnd('/') -ne 'https://www.powershellgallery.com/api/v2') { throw 'PSGallery source not verified' }
          }
          if($env:PL_PACKAGE_ACTION -eq 'resolve') {
            $items=@(Microsoft.PowerShell.PSResourceGet\Find-PSResource -Name $env:PL_PACKAGE_ID -Type Module -Repository PSGallery -ErrorAction Stop)
          } elseif($env:PL_PACKAGE_ACTION -eq 'search') {
            $items=@(Microsoft.PowerShell.PSResourceGet\Find-PSResource -Name ('*'+$env:PL_PACKAGE_ID+'*') -Type Module -Repository PSGallery -ErrorAction Stop | Select-Object -First 40)
          } elseif($env:PL_PACKAGE_ACTION -eq 'install') {
            Microsoft.PowerShell.PSResourceGet\Install-PSResource -Name $env:PL_PACKAGE_ID -Version $env:PL_PACKAGE_VERSION -Repository PSGallery -Scope CurrentUser -TrustRepository -AcceptLicense -NoClobber -ErrorAction Stop
            $items=@(Microsoft.PowerShell.PSResourceGet\Get-InstalledPSResource -Name $env:PL_PACKAGE_ID -ErrorAction Stop)
          } else { $items=@(Microsoft.PowerShell.PSResourceGet\Get-InstalledPSResource -ErrorAction Stop | Where-Object { $_.Type -eq 'Module' } | Select-Object -First 2000) }
          $result=@($items | ForEach-Object { $v=$_.Version.ToString(); if($_.Prerelease -and !$v.Contains('-')) { $v+='-'+$_.Prerelease }; [pscustomobject]@{ Id=$_.Name; Version=$v; Description=[string]$_.Description; Authors=[string]$_.Author; License=[string]$_.LicenseUri; Dependencies=(@($_.Dependencies | ForEach-Object { $_.Name+' '+$_.VersionRange }) -join '; '); Location=$_.InstalledLocation } })
          Write-Output ('PL_PACKAGES_JSON:'+ (ConvertTo-Json -InputObject $result -Depth 5 -Compress))
        } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
        """;
    internal ProcessStartInfo PowerShellCommand(string action, string id, string? version)
    {
        if (action is not ("installed" or "search" or "resolve" or "install")) throw new ArgumentException("action");
        if (action == "install") Validate(id, version ?? throw new ArgumentNullException(nameof(version)));
        var start = new ProcessStartInfo(Shell) { UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (string arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)) }) start.ArgumentList.Add(arg);
        start.Environment["PL_PACKAGE_ACTION"] = action; start.Environment["PL_PACKAGE_ID"] = id; start.Environment["PL_PACKAGE_VERSION"] = version ?? "";
        return start;
    }
    private async Task<IReadOnlyList<DeveloperPackage>> PowerShellAsync(string action, string id, string? version, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); if (action != "install") limit.CancelAfter(TimeSpan.FromSeconds(40));
        var result = await _run(PowerShellCommand(action, id, version), action == "install" ? token : limit.Token);
        if (result.ExitCode == 42) throw new MissingPSResourceGetException();
        if (result.ExitCode == 43)
        {
            var lines = result.Output.Split('\n', StringSplitOptions.TrimEntries);
            const string marker = "PL_MODULE_MANIFEST:";
            string manifest = lines.FirstOrDefault(line => line.StartsWith(marker, StringComparison.Ordinal))?[marker.Length..] ?? "";
            throw new BrokenPSResourceGetException(manifest, string.Join("\n", lines.Where(line => !line.StartsWith(marker, StringComparison.Ordinal))).Trim());
        }
        if (result.ExitCode != 0) throw new IOException("PowerShell/PSResourceGet: " + result.Output.Trim());
        var json = result.Output.Split('\n').LastOrDefault(l => l.StartsWith("PL_PACKAGES_JSON:", StringComparison.Ordinal));
        if (json is null) throw new IOException("Paketbestand nicht bestätigt. / Package inventory not verified.");
        var packages = JsonSerializer.Deserialize<DeveloperPackage[]>(json[17..]) ?? [];
        foreach (var p in packages) Validate(p.Id, p.Version);
        return packages;
    }
    internal ProcessStartInfo NuGetCommand(DeveloperPackage package, string config)
    {
        Validate(package.Id, package.Version);
        if (NuGetExecutable is null || NuGetDirectory is null) throw new IOException("NuGet.exe und Zielordner auswählen. / Select NuGet.exe and a destination.");
        var start = new ProcessStartInfo(NuGetExecutable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "install", package.Id, "-Version", package.Version, "-Source", NuGetSource, "-ConfigFile", config, "-OutputDirectory", NuGetDirectory, "-NonInteractive", "-DirectDownload", "-ForceEnglishOutput" }) start.ArgumentList.Add(arg);
        return start;
    }
    internal async Task InstallAsync(bool nuget, DeveloperPackage selected, CancellationToken preparation, Action<string> phase)
    {
        Validate(selected.Id, selected.Version);
        phase("check");
        var fresh = (await (nuget ? SearchAsync(true, selected.Id, preparation) : PowerShellAsync("resolve", selected.Id, null, preparation))).SingleOrDefault(p => p.Id.Equals(selected.Id, StringComparison.OrdinalIgnoreCase));
        if (fresh is null || fresh.Version != selected.Version) throw new IOException("Zielversion geändert. Bitte erneut prüfen. / Target version changed. Check again.");
        preparation.ThrowIfCancellationRequested();
        var current = (await InstalledAsync(nuget, preparation)).Where(p => p.Id.Equals(selected.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
        preparation.ThrowIfCancellationRequested();
        if (current.Any(p => ProgramVersions.IsNewer(selected.Version, p.Version)))
            throw new IOException("Eine neuere Version ist bereits installiert. Bitte Bestand erneut prüfen. / A newer version is already installed. Read inventory again.");
        if (current.Any(p => p.Version == selected.Version)) { phase("done"); return; }
        if (nuget)
        {
            if (NuGetExecutable is null || !await Task.Run(() => VerifyNuGet(NuGetExecutable), preparation))
                throw new IOException("Microsoft-Signatur von NuGet.exe nicht bestätigt. / NuGet.exe Microsoft signature not verified.");
            if (NuGetDirectory is null) throw new IOException("Zielordner wählen. / Select a destination.");
            Directory.CreateDirectory(NuGetDirectory);
            ValidateDirectory(NuGetDirectory);
            string stage = Path.Combine(NuGetDirectory, ".powerleitstand-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
            try
            {
                string config = Path.Combine(stage, "NuGet.Config");
                await File.WriteAllTextAsync(config, "<configuration><packageSources><clear/><add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\"/></packageSources><disabledPackageSources><clear/></disabledPackageSources></configuration>", preparation);
                preparation.ThrowIfCancellationRequested();
                phase("install");
                var result = await _run(NuGetCommand(selected, config), CancellationToken.None);
                if (result.ExitCode != 0) throw new IOException("NuGet: " + result.Output.Trim());
            }
            finally { Directory.Delete(stage, true); }
        }
        else { phase("install"); await PowerShellAsync("install", selected.Id, selected.Version, CancellationToken.None); }
        phase("verify");
        if (!(await InstalledAsync(nuget, CancellationToken.None)).Any(p => p.Id.Equals(selected.Id, StringComparison.OrdinalIgnoreCase) && p.Version == selected.Version))
            throw new IOException("Installierte Zielversion nicht bestätigt. / Installed target version not verified.");
        phase("done");
    }
}
