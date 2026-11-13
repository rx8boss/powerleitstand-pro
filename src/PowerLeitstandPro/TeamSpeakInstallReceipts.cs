using System.IO;
using System.Text.Json;

namespace PowerControlProWpf;

internal static class TeamSpeakInstallReceipts
{
    private sealed record Receipt(string Path, string Hash, string ProductCode, string Version);
    private static string FilePath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Power Control PRO", "TeamSpeak Installations.json");
    internal static void Save(TeamSpeakCopy copy, string version) => SaveAt(copy, version, FilePath);
    internal static void SaveAt(TeamSpeakCopy copy, string version, string receiptFile)
    {
        if (copy.Edition != 6 || copy.Source != "Registry" || copy.Path is null || !Guid.TryParse(copy.ProductCode, out _) || TeamSpeakInfoService.Edition(version) != 6) return;
        var root = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(receiptFile))!; Directory.CreateDirectory(root);
        if (!TeamSpeakInstaller.PlainDirectory(root)) throw new IOException("Invalid receipt directory.");
        string temporary = receiptFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(new Receipt(copy.Path, ProgramPortableUpdates.Hash(copy.Path), copy.ProductCode!, version))); File.Move(temporary, receiptFile, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static TeamSpeakCopy Apply(TeamSpeakCopy copy) => ApplyAt(copy, FilePath);
    internal static TeamSpeakCopy ApplyAt(TeamSpeakCopy copy, string receiptFile, Func<string, TeamSpeakCopy?>? readExecutable = null)
    {
        if (copy.Edition != 6 || copy.Source != "Registry" || !Guid.TryParse(copy.ProductCode, out _) || !File.Exists(receiptFile)) return copy;
        try
        {
            if (!ProgramPortableUpdates.PlainPath(receiptFile) || new FileInfo(receiptFile).Length > 16384) return copy;
            var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(receiptFile));
            return receipt is { Path.Length: > 0, Hash.Length: 64, ProductCode.Length: > 0 } && TeamSpeakInfoService.Edition(receipt.Version) == 6
                && (copy.Path is null || receipt.Path.Equals(copy.Path, StringComparison.OrdinalIgnoreCase)) && receipt.ProductCode.Equals(copy.ProductCode, StringComparison.OrdinalIgnoreCase)
                && (readExecutable ?? TeamSpeakInfoService.ReadExecutable)(receipt.Path)?.Edition == 6
                && receipt.Hash.Equals(ProgramPortableUpdates.Hash(receipt.Path), StringComparison.OrdinalIgnoreCase) ? copy with { Version = receipt.Version, Path = receipt.Path } : copy;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return copy; }
    }
}
