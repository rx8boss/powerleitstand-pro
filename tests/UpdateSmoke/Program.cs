using System.Net;
using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using PowerControlProWpf;

var root = Path.Combine(Path.GetTempPath(), "PowerLeitstand-UpdateSmoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var bytes = RandomNumberGenerator.GetBytes(1024 * 1024);
var update = new AvailableUpdate(new Version(1, 1, 2), "https://example.invalid/update.exe", Convert.ToHexString(SHA256.HashData(bytes)));
int count = 0;
void Assert(bool condition, string label) { if (!condition) throw new Exception(label); Console.WriteLine("PASS " + label); count++; }
async Task Run(string name, bool known, Func<UpdateProgress, bool>? cancelAt = null, bool badHash = false, bool shortResponse = false, bool serverError = false)
{
    var path = Path.Combine(root, name + ".new");
    using var cancellation = new CancellationTokenSource();
    using var client = new HttpClient(new FixtureHandler(bytes, known, shortResponse, serverError));
    var seen = new List<UpdateProgress>();
    var progress = new ImmediateProgress(p => { seen.Add(p); if (cancelAt?.Invoke(p) == true) cancellation.Cancel(); });
    bool cancelled = false, rejected = false;
    try { await UpdateService.DownloadAndVerifyAsync(client, badHash ? update with { Sha256 = new string('0', 64) } : update, path, progress, cancellation.Token); }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { cancelled = true; }
    catch (InvalidDataException) when (badHash || shortResponse) { rejected = true; }
    catch (HttpRequestException) when (serverError) { rejected = true; }
    if (cancelAt is not null)
    {
        Assert(cancelled && !File.Exists(path), name + ": cancellation honoured and temporary file removed");
        return;
    }
    if (badHash || shortResponse || serverError)
    {
        Assert(rejected && !File.Exists(path), name + ": rejected without leaving temporary file");
        return;
    }
    Assert(File.ReadAllBytes(path).SequenceEqual(bytes), name + ": exact downloaded bytes");
    Assert(seen.Count(p => p.Phase == UpdatePhase.Downloading) > 2, name + ": intermediate byte progress reported");
    Assert(seen.Where(p => p.Phase == UpdatePhase.Downloading).Select(p => p.ReceivedBytes).SequenceEqual(seen.Where(p => p.Phase == UpdatePhase.Downloading).Select(p => p.ReceivedBytes).Order()), name + ": byte progress monotonic");
    Assert(seen.All(p => known ? p.TotalBytes == bytes.Length : p.TotalBytes is null), name + ": correct known/unknown total");
    Assert(seen[^1].Phase == UpdatePhase.Verifying && seen[^1].ReceivedBytes == bytes.Length, name + ": verification follows complete download");
}
try
{
    await Run("known length", true);
    await Run("unknown length", false);
    await Run("cancel before data", true, p => p.ReceivedBytes == 0);
    await Run("cancel mid-download", true, p => p.ReceivedBytes >= 65536);
    await Run("cancel verification", true, p => p.Phase == UpdatePhase.Verifying);
    await Run("bad checksum", true, badHash: true);
    await Run("truncated body", true, shortResponse: true);
    await Run("HTTP failure", true, serverError: true);
    var existing = Path.Combine(root, "unrelated.new");File.WriteAllText(existing,"preserve");
    using var client = new HttpClient(new FixtureHandler(bytes, true, false, false));
    try { await UpdateService.DownloadAndVerifyAsync(client, update, existing, null, CancellationToken.None); throw new Exception("existing file overwritten"); }
    catch (IOException) { Assert(File.ReadAllText(existing) == "preserve", "pre-existing file preserved"); }
    Console.WriteLine($"PASS ALL {count} update checks; local HTTP fixture only; no program replacement or system action.");
}
catch (Exception e) { Console.Error.WriteLine(e); Environment.ExitCode=1; }
finally
{
    var resolved=Path.GetFullPath(root);
    if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("PowerLeitstand-UpdateSmoke-")) throw new Exception("Unsafe cleanup path");
    Directory.Delete(resolved, true);
}
sealed class ImmediateProgress(Action<UpdateProgress> report) : IProgress<UpdateProgress> { public void Report(UpdateProgress value) => report(value); }
sealed class FixtureHandler(byte[] bytes, bool known, bool truncated, bool error) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = truncated ? bytes[..(bytes.Length/2)] : bytes;
        var response=new HttpResponseMessage(error ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content=new StreamContent(new SlowStream(body)) };
        if(known) response.Content.Headers.ContentLength=bytes.Length;
        return Task.FromResult(response);
    }
}
sealed class SlowStream(byte[] bytes) : MemoryStream(bytes)
{
    public override bool CanSeek => false;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token=default)
    {
        await Task.Delay(30, token);
        return await base.ReadAsync(buffer[..Math.Min(buffer.Length,32768)],token);
    }
}
