using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Win32;

namespace PowerControlProWpf;

internal sealed record NetworkDnsBackup(Guid AdapterId, string? StaticServers, DateTimeOffset SavedAt, string? StaticServersV6 = null)
{
    internal static NetworkDnsBackup Capture(RepairNetworkAdapter adapter)
    {
        return new(adapter.Id,
            adapter.Index > 0 ? ReadStatic("Tcpip", AddressFamily.InterNetwork) : null,
            DateTimeOffset.Now,
            adapter.Ipv6Index > 0 ? ReadStatic("Tcpip6", AddressFamily.InterNetworkV6) : null);

        string ReadStatic(string service, AddressFamily family)
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{service}\Parameters\Interfaces\{adapter.Id:B}");
            var addresses = key?.GetValue("NameServer") switch
            {
                null => "",
                string value => value.Trim(),
                string[] values => string.Join(" ", values).Trim(),
                _ => throw new InvalidDataException("Invalid Windows DNS server list.")
            };
            return string.Join(", ", RepairNetworkRequest.Parse(addresses, family));
        }
    }

    private static string PathFor(string folder, Guid adapterId) => Path.Combine(folder, $"{adapterId:N}.json");
    internal static bool Exists(string folder, Guid adapterId) => File.Exists(PathFor(folder, adapterId));

    internal static NetworkDnsBackup? Read(string folder, Guid adapterId)
    {
        var path = PathFor(folder, adapterId);
        if (!File.Exists(path)) return null;
        var backup = JsonSerializer.Deserialize<NetworkDnsBackup>(File.ReadAllText(path));
        if (backup is null || backup.AdapterId != adapterId || (backup.StaticServers is null && backup.StaticServersV6 is null))
            throw new InvalidDataException("Invalid DNS backup.");
        if (backup.StaticServers is not null) _ = RepairNetworkRequest.Parse(backup.StaticServers, AddressFamily.InterNetwork);
        if (backup.StaticServersV6 is not null) _ = RepairNetworkRequest.Parse(backup.StaticServersV6, AddressFamily.InterNetworkV6);
        return backup;
    }

    internal void SaveBefore(string folder, RepairNetworkRequest request)
    {
        var saved = Read(folder, AdapterId);
        var next = this with
        {
            StaticServers = request.Dns is not null ? StaticServers : saved?.StaticServers,
            StaticServersV6 = request.DnsV6 is not null ? StaticServersV6 : saved?.StaticServersV6
        };
        next.Save(folder);
    }

    internal void Save(string folder)
    {
        Directory.CreateDirectory(folder);
        var path = PathFor(folder, AdapterId);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

}
