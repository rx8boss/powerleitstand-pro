namespace PowerControlProWpf;

internal static class RepairStorageQuery
{
    internal const string Script = """
        function Format-Counter($value, [string]$unit = '') {
            if ($null -eq $value) { Label 'nicht verfügbar' 'unavailable' }
            else { "$value$unit" }
        }

        $disks = @(Get-PhysicalDisk -ErrorAction Stop)
        if ($disks.Count -eq 0) { return }
        '{0}: {1}' -f (Label 'Physische Laufwerke' 'Physical drives'), $disks.Count

        $number = 0
        foreach ($disk in $disks) {
            $number++
            $name = $disk.FriendlyName
            if ([string]::IsNullOrWhiteSpace($name)) { $name = Label 'Unbekanntes Modell' 'Unknown model' }
            $media = switch ([string]$disk.MediaType) {
                { $_ -in 'SSD', '4' } { 'SSD' }
                { $_ -in 'HDD', '3' } { Label 'Festplatte (HDD)' 'Hard disk (HDD)' }
                { $_ -in 'SCM', '5' } { 'SCM' }
                default { Label 'Typ nicht gemeldet' 'Type not reported' }
            }
            $health = switch ([string]$disk.HealthStatus) {
                { $_ -in 'Healthy', '0' } { Label 'Fehlerfrei laut Windows' 'Healthy according to Windows' }
                { $_ -in 'Warning', '1' } { Label 'Warnung' 'Warning' }
                { $_ -in 'Unhealthy', '2' } { Label 'Fehlerhaft' 'Unhealthy' }
                default { Label 'Unbekannt' 'Unknown' }
            }
            '{0}. {1} | {2} | {3}' -f $number, $name, $media, $disk.BusType
            '  {0}: {1}' -f (Label 'Zustand' 'Health'), $health

            $counters = $null
            try { $counters = Get-StorageReliabilityCounter -PhysicalDisk $disk -ErrorAction Stop }
            catch { Label '  Zuverlässigkeitswerte konnten für dieses Laufwerk nicht abgefragt werden.' '  Reliability counters could not be read for this drive.' }

            '  {0}: {1} | {2}: {3}' -f (Label 'Temperatur' 'Temperature'),
                (Format-Counter $counters.Temperature ' °C'), (Label 'Verschleiß' 'Wear'), (Format-Counter $counters.Wear ' %')
            '  {0}: {1} | {2}: {3}' -f (Label 'Lesefehler gesamt' 'Total read errors'),
                (Format-Counter $counters.ReadErrorsTotal), (Label 'nicht korrigiert' 'uncorrected'), (Format-Counter $counters.ReadErrorsUncorrected)
            '  {0}: {1} | {2}: {3}' -f (Label 'Schreibfehler gesamt' 'Total write errors'),
                (Format-Counter $counters.WriteErrorsTotal), (Label 'nicht korrigiert' 'uncorrected'), (Format-Counter $counters.WriteErrorsUncorrected)
        }
        """;
}
