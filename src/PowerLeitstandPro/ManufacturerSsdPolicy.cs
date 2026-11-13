using System.Text.RegularExpressions;

namespace PowerControlProWpf;

internal static class ManufacturerSsdPolicy
{
    // Conservative subset of the official support lists checked on 2026-10-19.
    // Unknown OEM identities, generic USB bridges, RAID and virtual disks are not approvals.
    internal static bool Supports(string id, ManufacturerDisk disk)
    {
        var model = Regex.Replace(disk.Model.ToUpperInvariant(), @"[^A-Z0-9]", "");
        var native = disk.Bus is 3 or 11 or 17; // ATA, SATA, NVMe
        return id switch
        {
            "kingston" => disk.Bus == 7
                ? Regex.IsMatch(model, @"^KINGSTON(?:XS1000|XS2000|DTMAX)(?:\d|$)")
                : native && Regex.IsMatch(model, @"^KINGSTON(?:S)?(?:KC2000|KC2500|A2000|DC1000M|DC1500M|NV2S|NV3S|DC1000B|KC3000[SD]?|DC2000B|FYR[SD]?|A400|Q500|KC600|DC450|DC500|DC600)(?:[A-Z]?\d|$)"),
            "samsung" => native && Regex.IsMatch(model, @"^SAMSUNGSSD(?:9100PRO|990EVOPLUS|990EVO|990PRO|980PRO|980|970EVOPLUS|970EVO|970PRO|960EVO|960PRO|950PRO|870EVO|870QVO|860EVO|860PRO|860QVO|850EVO|850PRO)(?:\d|WITHHEATSINK|$)")
                || disk.Bus == 7 && Regex.IsMatch(model, @"^SAMSUNG(?:PSSD|PORTABLESSD)?(?:T5EVO|T5|T7SHIELD|T7TOUCH|T7|T9)(?:\d|$)"),
            // Known retail SSD families. No WDC HDD, OEM SN7xx, flash-card or generic brand match.
            "sandisk" => native && Regex.IsMatch(model, @"^(?:WD(?:BLACK|BLUE)?|WDCWD(?:BLACK|BLUE)?)(?:SN570|SN580|SN770M?|SN850X?|SN5000)(?:\d|$)")
                || native && Regex.IsMatch(model, @"^(?:SANDISK)?(?:SDSSDA|SDSSDH3)\d")
                || disk.Bus == 7 && Regex.IsMatch(model, @"^WD(?:BLACK)?(?:P40|P50|D50)(?:GAMEDRIVE|\d|$)"),
            _ => false
        };
    }
}
