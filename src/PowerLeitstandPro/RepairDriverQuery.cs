namespace PowerControlProWpf;

internal static class RepairDriverQuery
{
    internal const int OutputLimit = 128_000;

    internal const string Script = """
        function DriverField($value) {
            if ([string]::IsNullOrWhiteSpace([string]$value)) { Label 'nicht verfügbar' 'unavailable' }
            else { [string]$value }
        }

        $drivers = @(Get-WindowsDriver -Online -ErrorAction Stop | Sort-Object ProviderName, Driver)
        if ($drivers.Count -eq 0) { return }
        '{0}: {1}' -f (Label 'Drittanbieter-Treiberpakete' 'Third-party driver packages'), $drivers.Count
        Label 'Veröffentlichte INF | Original-INF | Anbieter | Version' 'Published INF | Original INF | Provider | Version'
        foreach ($driver in $drivers) {
            $original = [System.IO.Path]::GetFileName([string]$driver.OriginalFileName)
            $version = $null
            if ($null -ne $driver.MajorVersion -and $null -ne $driver.MinorVersion -and
                $null -ne $driver.Build -and $null -ne $driver.Revision) {
                $version = '{0}.{1}.{2}.{3}' -f $driver.MajorVersion, $driver.MinorVersion, $driver.Build, $driver.Revision
            }
            '{0} | {1} | {2} | {3}' -f (DriverField $driver.Driver), (DriverField $original),
                (DriverField $driver.ProviderName), (DriverField $version)
        }
        """;
}
