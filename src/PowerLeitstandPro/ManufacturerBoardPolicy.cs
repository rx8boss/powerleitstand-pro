using System.Text.Json;
using System.Text.RegularExpressions;

namespace PowerControlProWpf;

internal static class ManufacturerBoardPolicy
{
    internal const string AsusDrivers = "https://www.asus.com/support/webapi/ProductV2/GetPDDrivers?website=global&model=armoury+crate&pdhashedid=&pdid=99999&cpu=&osid=52&siteID=www&sitelang=";
    internal static bool Candidate(string id, ManufacturerHardware hardware)
    {
        var vendor = hardware.BoardManufacturer;
        var model = hardware.BoardModel;
        return id switch
        {
            "gigabyte" => Regex.IsMatch(vendor, @"^GIGA-?BYTE(?: TECHNOLOGY CO\.,? LTD\.?)?$", RegexOptions.IgnoreCase) && model.Length >= 5,
            "asus" => Regex.IsMatch(vendor, @"^(ASUSTeK COMPUTER INC\.?|ASUS)$", RegexOptions.IgnoreCase) &&
                Regex.IsMatch(model, @"^(?:ROG (?:STRIX|MAXIMUS|CROSSHAIR)|TUF GAMING|PRIME|ProArt)\s+(?:[A-Z0-9-]+\s+)*[ZXBHA](?:570|590|690|670|790|870|890|550|560|650|660|760|850|860)(?:E|M)?\b", RegexOptions.IgnoreCase),
            // Conservative, officially documented MSI Center/AI Engine motherboard families.
            "msi" => Regex.IsMatch(vendor, @"^(Micro-Star International Co\.,? Ltd\.?|MSI)$", RegexOptions.IgnoreCase) &&
                Regex.IsMatch(model, @"^(?:(?:MEG|MPG|MAG|PRO)\s+)?(?:Z790|B760|Z690|B660|X670E?|B650E?|A620)\b", RegexOptions.IgnoreCase),
            _ => false
        };
    }

    internal static bool ListedGigabyteBoard(string model, string html)
    {
        // Require the exact model in a motherboard row; a chipset/brand mention is insufficient.
        var expected = Normalize(model);
        foreach (Match row in Regex.Matches(html, @"<tr\b[^>]*>(?<row>.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
        {
            var cells = Regex.Matches(row.Groups["row"].Value, @"<td\b[^>]*>(?<cell>.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            if (cells.Count != 3 || ManufacturerDownloadPolicy.PlainText(cells[0].Groups["cell"].Value).Trim() != "Motherboard") continue;
            var cell = ManufacturerDownloadPolicy.PlainText(cells[2].Groups["cell"].Value).Trim();
            // Revision-bearing/multiple-model rows need a separately verified DMI revision rule.
            if (!cell.Contains("rev.", StringComparison.OrdinalIgnoreCase) && Normalize(cell) == expected) return true;
        }
        return false;
    }

    private static string Normalize(string text) => Regex.Replace(text.Trim(), @"\s+", " ").ToUpperInvariant();

    internal static ManufacturerDownload? ParseAsus(string json, string model)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("Result", out var result) || !result.TryGetProperty("Obj", out var groups) || groups.ValueKind != JsonValueKind.Array) return null;
        var matches = new List<ManufacturerDownload>();
        foreach (var group in groups.EnumerateArray())
        {
            if (!group.TryGetProperty("Files", out var files) || files.ValueKind != JsonValueKind.Array) continue;
            foreach (var file in files.EnumerateArray())
            {
                if (!file.TryGetProperty("Title", out var title) || title.GetString() != "Armoury Crate & Aura Creator Installer" ||
                    !file.TryGetProperty("DownloadUrl", out var urls) || !urls.TryGetProperty("Global", out var path) ||
                    !file.TryGetProperty("sha256", out var hash) || !Regex.IsMatch(hash.GetString() ?? "", @"\A[0-9A-Fa-f]{64}\z")) continue;
                var source = new Uri(new Uri("https://dlcdnets.asus.com"), path.GetString() ?? "");
                if (ManufacturerDownloadPolicy.Allows("asus", source)) matches.Add(new(source, "ArmouryCrateInstallTool.zip", model, hash.GetString()));
            }
        }
        return matches.Count == 1 ? matches[0] : null;
    }
}
