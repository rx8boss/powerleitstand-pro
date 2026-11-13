namespace PowerControlProWpf;

internal sealed record ManufacturerApp(string Id, string Name, string EnglishName, string Description, string EnglishDescription,
    string Website, string Publisher, string[] Aliases, string[] RelativeExecutables, bool MayBePackaged = false, string? AppPage = null)
{
    internal string Title(bool english) => english ? EnglishName : Name;
    internal string Summary(bool english) => english ? EnglishDescription : Description;
    internal string ProductPage => AppPage ?? Website;
}
internal enum ManufacturerInstallState { Missing, Installed, NoLaunchTarget, Unknown }
internal sealed record ManufacturerState(ManufacturerInstallState Installation, string? Executable = null);
internal sealed record ManufacturerDownload(Uri Source, string FileName, string Hardware, string? Sha256 = null);
internal sealed record ManufacturerProgress(string Phase, long Bytes = 0, long? Total = null);

internal static class ManufacturerCatalog
{
    internal static readonly ManufacturerApp[] Apps = [
        new("nvidia", "NVIDIA App", "NVIDIA App", "Software und Treiber für unterstützte NVIDIA-Grafikkarten", "Software and drivers for supported NVIDIA graphics cards",
            "https://www.nvidia.com/en-us/software/nvidia-app/", "NVIDIA Corporation", ["NVIDIA App"], [@"NVIDIA Corporation\NVIDIA app\CEF\NVIDIA App.exe", @"CEF\NVIDIA App.exe"]),
        new("amd", "AMD Software", "AMD Software", "Radeon-Software und Unterstützung für passende AMD-Geräte", "Radeon software and support for compatible AMD devices",
            "https://www.amd.com/en/support/download/drivers.html", "Advanced Micro Devices, Inc.", ["AMD Software"], [@"AMD\CNext\CNext\RadeonSoftware.exe", @"CNext\CNext\RadeonSoftware.exe"], AppPage: "https://www.amd.com/en/products/software/adrenalin.html"),
        new("intel", "Intel Treiber- und Support-Assistent", "Intel Driver & Support Assistant", "Treiberprüfung für unterstützte Intel-Komponenten", "Driver checks for supported Intel components",
            "https://www.intel.com/content/www/us/en/support/detect.html", "Intel Corporation", ["Intel Driver & Support Assistant", "Intel® Driver & Support Assistant"], [@"Intel\Driver and Support Assistant\DSATray.exe", "DSATray.exe"]),
        new("gigabyte", "GIGABYTE Control Center", "GIGABYTE Control Center", "Herstellersoftware für unterstützte GIGABYTE-Geräte", "Manufacturer software for supported GIGABYTE devices",
            "https://www.gigabyte.com/Consumer/Software/GIGABYTE-Control-Center/global/", "GIGA-BYTE TECHNOLOGY CO., LTD.", ["GIGABYTE Control Center", "GIGABYTE CONTROL CENTER"], [@"GIGABYTE\Control Center\GCC.exe", "GCC.exe"]),
        new("kingston", "Kingston SSD Manager", "Kingston SSD Manager", "Laufwerkszustand und Firmware für unterstützte Kingston-SSDs", "Drive health and firmware for supported Kingston SSDs",
            "https://www.kingston.com/en/support/technical/ssdmanager", "Kingston Digital, Inc.", ["Kingston SSD Manager"], [@"Kingston_SSD_Manager\KSM_Gen15.exe", "KSM_Gen15.exe", @"Kingston_SSD_Manager\KSM.exe", "KSM.exe"]),
        new("samsung", "Samsung Magician", "Samsung Magician", "Laufwerksverwaltung für unterstützte Samsung-SSDs", "Drive management for supported Samsung SSDs",
            "https://semiconductor.samsung.com/consumer-storage/support/tools/", "Samsung Electronics Co., Ltd.", ["Samsung Magician"], [@"Samsung\Samsung Magician\SamsungMagician.exe", "SamsungMagician.exe"], AppPage: "https://semiconductor.samsung.com/consumer-storage/magician/"),
        new("sandisk", "SANDISK Dashboard", "SANDISK Dashboard", "Werkzeuge für unterstützte SANDISK-Laufwerke", "Tools for supported SANDISK drives",
            "https://support-en.sandisk.com/app/answers/detailweb/a_id/31759", "SanDisk Corporation", ["SANDISK Dashboard", "SanDisk SSD Dashboard"], [@"SanDisk\Dashboard\Dashboard.exe", @"SanDisk\SSD Dashboard\SanDiskSSDDashboard.exe", "Dashboard.exe", "SanDiskSSDDashboard.exe"]),
        new("asus", "ASUS Armoury Crate", "ASUS Armoury Crate", "Software für unterstützte ASUS- und ROG-Geräte", "Software for supported ASUS and ROG devices",
            "https://www.asus.com/global/support/faq/1041654/", "ASUSTeK COMPUTER INC.", ["ARMOURY CRATE", "Armoury Crate"], [@"ASUS\ARMOURY CRATE Service\ArmouryCrate.exe", "ArmouryCrate.exe"], true, "https://rog.asus.com/content/armoury-crate/"),
        new("msi", "MSI Center", "MSI Center", "Software und Zusatzmodule für unterstützte MSI-Geräte", "Software and modules for supported MSI devices",
            "https://www.msi.com/Landing/msi-center/download", "Micro-Star International Co., Ltd.", ["MSI Center"], [@"MSI\MSI Center\MSI.CentralServer.exe", @"MSI\MSI Center\MSI Center.exe"], true),
        new("asus-displaywidget", "ASUS DisplayWidget Center", "ASUS DisplayWidget Center", "Bildeinstellungen für unterstützte ASUS-Monitore; Modellkompatibilität auf der Produktseite prüfen", "Display settings for supported ASUS monitors; check model compatibility on the product page",
            "https://www.asus.com/content/monitor-software-osd-displaywidgetcenter/", "ASUSTeK COMPUTER INC.", ["ASUS DisplayWidget Center", "DisplayWidget Center"], [@"ASUS\DisplayWidgetCenter\DisplayWidgetCenter.exe", "DisplayWidgetCenter.exe"]),
        new("turtle-beach", "Turtle Beach Swarm II", "Turtle Beach Swarm II", "Gerätesoftware für unterstützte Turtle-Beach-Produkte; passendes Modell beim Anbieter auswählen", "Device software for supported Turtle Beach products; select the matching model with the provider",
            "https://www.turtlebeach.com/swarm2", "Turtle Beach", ["Turtle Beach Swarm II", "Swarm II"], [@"Turtle Beach Swarm II\Turtle Beach Swarm II.exe", "Turtle Beach Swarm II.exe", "Swarm II.exe"])
    ];
}

internal interface IManufacturerService
{
    Task<bool> CheckWinGetHardwareAsync(ManufacturerApp app, CancellationToken token) => Task.FromResult(false);
    Task<IReadOnlyDictionary<string, ManufacturerState>> InspectAsync();
    Task<ManufacturerDownload?> ResolveAsync(ManufacturerApp app, CancellationToken token);
    Task<string> DownloadAsync(ManufacturerApp app, ManufacturerDownload download, IProgress<ManufacturerProgress> progress, CancellationToken token);
    Task LaunchAsync(ManufacturerApp app, string file, bool installer, Action? onStarted = null);
    void OpenWebsite(ManufacturerApp app);
}
