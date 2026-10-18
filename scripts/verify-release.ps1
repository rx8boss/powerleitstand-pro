param(
    [string]$Exe,
    [string]$GitHubReleaseJson,
    [switch]$PrepareNotes
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'src/PowerLeitstandPro/PowerControlProWpf.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version muss drei Zahlen enthalten / Version must contain three numbers.' }
$source = Get-Content -LiteralPath (Join-Path $projectRoot 'src/PowerLeitstandPro/AppReleaseNotes.cs') -Raw
function Read-Language([string]$name) {
    $match = [regex]::Match($source, '(?s)const string ' + $name + ' = """\r?\n(.*?)\r?\n        """;')
    if (!$match.Success) { throw "Changelog fehlt / Missing changelog: $name" }
    return [regex]::Replace($match.Groups[1].Value, '(?m)^        ', '').Replace("`r", '').Trim()
}
$german = Read-Language 'German'
$english = Read-Language 'English'
$deCount = ([regex]::Matches($german, '(?m)^• ')).Count
$enCount = ([regex]::Matches($english, '(?m)^• ')).Count
if ($deCount -eq 0 -or $deCount -ne $enCount) { throw 'Änderungslisten sind unvollständig / Incomplete change lists.' }
$expected = "# PowerLeitstand PRO $version`n`n## Deutsch`n$german`n`n## English`n$english"
$notesPath = Join-Path $projectRoot "docs/releases/v$version.md"
if ($PrepareNotes) {
    New-Item -ItemType Directory -Force (Split-Path -Parent $notesPath) | Out-Null
    Set-Content -LiteralPath $notesPath -Value $expected -Encoding utf8
}
if (!(Test-Path -LiteralPath $notesPath)) { throw 'Release-Text fehlt / Release text missing. Run with -PrepareNotes.' }
$actual = (Get-Content -LiteralPath $notesPath -Raw).Replace("`r", '').Trim()
if ($actual -cne $expected.Trim()) { throw 'App- und Release-Changelog weichen ab / App and release changelogs differ.' }
if (!$Exe) { throw 'EXE für vollständige Versionsprüfung angeben / Supply -Exe for the complete version check.' }
$binary = Get-Item -LiteralPath $Exe
$fileVersion = [version]$binary.VersionInfo.FileVersion
$productVersion = ($binary.VersionInfo.ProductVersion -split '\+')[0]
if ($fileVersion.ToString(3) -ne $version -or $productVersion -ne $version) { throw 'EXE- und Projektversion weichen ab / EXE and project versions differ.' }
Write-Output "PASS RELEASE $version : project, EXE file/product version, DE/EN changelogs ($deCount changes each), app/release text agreement."
Write-Output "Release text: $notesPath"
if ($GitHubReleaseJson) {
    $release = Get-Content -LiteralPath $GitHubReleaseJson -Raw | ConvertFrom-Json
    $asset = @($release.assets | Where-Object name -eq 'PowerLeitstand-PRO.exe')
    if ($release.tag_name -cne "v$version" -or $release.draft -or $release.prerelease -or $asset.Count -ne 1) { throw 'GitHub-Version oder EXE-Auswahl falsch / GitHub version or EXE selection mismatch.' }
    if ($release.body.Replace("`r", '').Trim() -cne $expected.Trim()) { throw 'GitHub-Changelog weicht ab / GitHub changelog mismatch.' }
    $digest = 'sha256:' + (Get-FileHash -LiteralPath $binary.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($asset[0].digest -cne $digest) { throw 'GitHub-EXE stimmt nicht überein / GitHub EXE digest mismatch.' }
    Write-Output 'PASS GITHUB: release tag, full DE/EN body and uploaded EXE digest match.'
} else {
    Write-Output 'GitHub not checked: supply fresh release JSON with -GitHubReleaseJson after publishing. This script does not publish.'
}
