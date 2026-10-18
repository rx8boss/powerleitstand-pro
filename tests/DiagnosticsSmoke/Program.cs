using PowerControlProWpf;
using System.Text.Json;

static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
Require(SystemDiagnosticsSampler.Rate(0, 1_000_000, 1) == 8, "Byte-to-MBit conversion failed.");
foreach (var seconds in new[] { 0d, -1, double.NaN, double.PositiveInfinity, 11 })
    Require(SystemDiagnosticsSampler.Rate(0, 100, seconds) is null, "Invalid or suspended interval produced a rate.");
Require(SystemDiagnosticsSampler.Rate(100, 20, 1) is null, "Counter rollback produced a rate.");
Require(SystemDiagnosticsSampler.Rate(100, 100, 3) == 0, "Idle interface did not produce zero.");
Require(new DriveDiagnostic("C:", "", 500, 320).Percent == 36, "Drive usage math failed.");
foreach (var drive in new[] { new DriveDiagnostic("C:", "", 0, 0), new DriveDiagnostic("C:", "", 500, 501), new DriveDiagnostic("C:", "", null, null) })
    Require(drive.Percent is null && drive.Used is null, "Unavailable drive produced invented usage.");
var empty = DiagnosticsSnapshot.Empty;
var strings = System.Text.Encoding.ASCII.GetBytes("DIMM_A1\0Vendor\0RAM-Model\0\0"); var firmware = new byte[8 + 34 + strings.Length];
BitConverter.GetBytes((uint)(firmware.Length - 8)).CopyTo(firmware, 4); firmware[8] = 17; firmware[9] = 34;
BitConverter.GetBytes((ushort)8192).CopyTo(firmware, 20); firmware[24] = 1; firmware[26] = 24; firmware[31] = 2; firmware[34] = 3;
BitConverter.GetBytes((ushort)1600).CopyTo(firmware, 40); strings.CopyTo(firmware, 42);
var ramFixture = RamHardwareInfo.Parse(firmware);
Require(ramFixture.Slots == 1 && ramFixture.Modules.Single() is { Type: "DDR3", Manufacturer: "Vendor", PartNumber: "RAM-Model", ConfiguredSpeed: 1600, Bytes: 8589934592UL }, "SMBIOS module decoding failed.");
Require(RamHardwareInfo.Parse(firmware[..^1]).Modules.Count == 0, "Truncated firmware parsed as valid.");
Console.WriteLine("PASS SMBIOS RAM type, size, strings, speed and truncation");
var gpuFixture = GpuDiagnosticsSampler.ParseNvidiaOutput("GPU-1, \"NVIDIA Test, Card\", 42, 38, 4096, 1024\nGPU-2, Secondary, N/A, N/A, N/A, N/A\n", "NVIDIA Test, Card");
Require(gpuFixture.Length == 2 && gpuFixture[0].IsPrimary && gpuFixture[0].Name == "NVIDIA Test, Card" && gpuFixture[0].MemoryPercent == 25 && gpuFixture[0].TotalMemory == 4294967296UL, "GPU identity/CSV/VRAM conversion failed.");
Require(gpuFixture[1].Usage is null && gpuFixture[1].Temperature is null && gpuFixture[1].TotalMemory is null, "Unavailable GPU fields invented measurements.");
var invalidGpu = GpuDiagnosticsSampler.ParseNvidiaOutput("GPU-bad, Broken, NaN, -1, 1024, 2048\nGPU-dup, Twin, 101, 151, 0, 0\nGPU-dup, Twin, 0, 0, 0, 0\nmalformed", "Twin");
Require(invalidGpu.Length == 2 && invalidGpu.All(g => g.Usage is null && g.Temperature is null && g.MemoryPercent is null), "Invalid GPU data accepted or duplicate retained.");
var twins = GpuDiagnosticsSampler.ParseNvidiaOutput("GPU-a, Twin, 0, 30, 1024, 20\nGPU-b, Twin, 10, 40, 1024, 30", "Twin");
Require(twins.All(g => !g.IsPrimary), "Identical GPU names incorrectly assigned to the main display.");
Console.WriteLine("PASS GPU parsing: multiple GPUs, quoted names, MiB conversion, missing values, invalid ranges, duplicates and ambiguous main display");
Require(PcHardwareInfo.WindowsName("Windows 10 Pro", 19045) == "Windows 10", "Windows 10 detection failed.");
Require(PcHardwareInfo.WindowsName("Windows 10 Pro", 26100) == "Windows 11", "Windows 11 legacy registry name misclassified.");
Require(PcHardwareInfo.WindowsName("Windows Server 2025", 26100).Contains("Server"), "Server mislabeled as client Windows.");
var explorer = DrivePathLauncher.CreateStartInfo("C:");
Require(explorer.ArgumentList.Single() == @"C:\" && !explorer.UseShellExecute, "Drive path/Explorer launch contract failed.");
Require(DrivePathLauncher.CreateStartInfo(@"C:\Volumes\USB & Backup\").ArgumentList.Single() == @"C:\Volumes\USB & Backup\", "Mounted-folder path altered.");
foreach (var invalid in new[] { "relative", "C:", "shell:AppsFolder", "C:\"bad", "C:\n", @"\\.\PhysicalDrive0" }.Where(p => p != "C:"))
{
    try { DrivePathLauncher.CreateStartInfo(invalid); throw new InvalidOperationException("Unsafe path accepted: " + invalid); }
    catch (ArgumentException) { }
}
Console.WriteLine("PASS Windows 10/11 classification and safe Explorer arguments, including folder mount");
Require(empty.NetworkTotal is null, "Unknown network shown as zero.");
Require((empty with { NetworkAvailable = true }).NetworkTotal == 0, "Disconnected network is not zero.");
Require((empty with { NetworkAvailable = true, Networks = [new("1", "LAN", "LAN", null, 1.2, .3), new("2", "WiFi", "WLAN", "Test", 2, 1)] }).NetworkTotal == 4.5, "Multi-interface total failed.");
Require((empty with { NetworkAvailable = true, Networks = [new("1", "LAN", "LAN", null, 1, 1), new("2", "VPN", "Other", null, 1, 1)] }).NetworkTotal == 2, "VPN traffic counted twice.");
Console.WriteLine("PASS calculations: drive bounds, MBit/s conversion, counter reset, standby interval, unknown/disconnected network and multiple connections");
var sampler = new SystemDiagnosticsSampler();
try
{
    var first = sampler.Sample();
    Require(first.Networks.All(n => n.Receive is null && n.Send is null), "First sample invents network rates.");
    Thread.Sleep(1300);
    var second = sampler.Sample();
    Console.WriteLine("RAM hardware: " + JsonSerializer.Serialize(second.RamHardware));
    Require(second.Pc is { Packages: > 0, Cores: > 0, LogicalProcessors: > 0 } && second.Pc.CpuModel.Length > 0, "Native CPU topology/model missing.");
    Require(second.Processors.Count == second.Pc!.LogicalProcessors && second.Processors.All(p => p.Usage is >= 0 and <= 100), "Per-logical-processor measurements missing or out of range.");
    Require(second.Drives.Select(d => d.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == second.Drives.Count, "Duplicate volume mount.");
    Require(System.IO.DriveInfo.GetDrives().All(d => second.Drives.Any(v => v.Name.Equals(d.Name.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))), "Windows-mounted drive omitted.");
    Require(second.Ram is >= 0 and <= 100 && second.TotalMemory > 0, "Live physical memory unavailable.");
    Require(second.Cpu is null or >= 0 and <= 100, "CPU out of range.");
    Require(second.Disk is null or >= 0 and <= 100, "Disk activity out of range.");
    Require(second.Networks.Select(n => n.Id).Distinct().Count() == second.Networks.Count, "Duplicate network adapters.");
    Require(second.Networks.All(n => !n.Name.Contains("-WFP", StringComparison.OrdinalIgnoreCase) && !n.Name.Contains("-QoS", StringComparison.OrdinalIgnoreCase)), "Filter adapters leak into display.");
    Require(second.Networks.All(n => n.Receive is null or >= 0 && n.Send is null or >= 0), "Negative network rate.");
    Require(second.Drives.All(d => d.Percent is null or >= 0 and <= 100), "Drive percentage out of range.");
    Console.WriteLine("PASS native live sampling: " + JsonSerializer.Serialize(second));
    Console.WriteLine($"COUNTERS cpu={(second.Cpu.HasValue ? "available" : "unavailable")} disk={(second.Disk.HasValue ? "available" : "unavailable")} drives={second.Drives.Count} network={second.Networks.Count}");
    var gpuSample = second;
    for (var i = 0; i < 40 && !gpuSample.Gpus.Any(g => g.Source == "NVIDIA"); i++) { Thread.Sleep(150); gpuSample = sampler.Sample(); }
    Require(gpuSample.Gpus.Any(g => g.Source == "NVIDIA" && g.Usage is >= 0 and <= 100 && g.Temperature is >= 0 and <= 150 && g.TotalMemory > 0 && g.UsedMemory <= g.TotalMemory), "Local NVIDIA telemetry unavailable.");
    Console.WriteLine("PASS actual GPU telemetry: " + JsonSerializer.Serialize(gpuSample.Gpus));
}
finally { sampler.Dispose(); }
try { sampler.Sample(); throw new InvalidOperationException("Sampling after disposal succeeded."); }
catch (ObjectDisposedException) { Console.WriteLine("PASS native resources disposed; sampling after shutdown rejected"); }
var closingGpuSampler = new GpuDiagnosticsSampler();
closingGpuSampler.Sample(System.Diagnostics.Stopwatch.GetTimestamp());
var pendingGpuRead = (Task?)typeof(GpuDiagnosticsSampler).GetField("_read", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(closingGpuSampler);
closingGpuSampler.Dispose();
Require(pendingGpuRead is not null && pendingGpuRead.Wait(TimeSpan.FromSeconds(5)), "GPU driver helper did not finish after cancellation.");
try { closingGpuSampler.Sample(0); throw new InvalidOperationException("GPU sampling after disposal succeeded."); }
catch (ObjectDisposedException) { Console.WriteLine("PASS GPU cancellation completes pending read and rejects further sampling"); }
