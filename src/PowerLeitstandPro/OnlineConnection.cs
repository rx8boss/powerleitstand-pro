using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;

namespace PowerControlProWpf;

internal sealed class OnlineConnectionException(bool offline, Exception? inner = null, bool lost = false) : IOException(offline
    ? lost ? "Internetverbindung verloren. Lokale Informationen bleiben verfügbar." : "Keine Internetverbindung. Lokale Informationen bleiben verfügbar."
    : "Onlinedienst nicht erreichbar. Bitte Verbindung oder Anbieter prüfen.", inner)
{
    internal bool Offline { get; } = offline;
    internal bool Lost { get; } = lost;
}

internal static class OnlineConnection
{
    private static readonly HttpClient Probe = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly SemaphoreSlim Gate = new(1);
    private static long _confirmedTicks;
    internal static void Invalidate() => Interlocked.Exchange(ref _confirmedTicks, 0);
    internal static Func<bool>? NetworkForTests;
    internal static bool Available => NetworkForTests?.Invoke() ?? NetworkInterface.GetIsNetworkAvailable();
    internal static void RequireNetwork() { if (!Available) throw new OnlineConnectionException(true); }
    internal static async Task CheckAsync(CancellationToken token)
    {
        RequireNetwork();
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (DateTime.UtcNow - new DateTime(Interlocked.Read(ref _confirmedTicks), DateTimeKind.Utc) < TimeSpan.FromSeconds(15)) return;
            Exception? last = null;
            foreach (var endpoint in new[] { "https://cdn.winget.microsoft.com/", "https://api.github.com/" })
            {
                try
                {
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(4));
                    using var request = new HttpRequestMessage(HttpMethod.Head, endpoint);
                    request.Headers.UserAgent.ParseAdd(AppVersion.UserAgent);
                    using var response = await Probe.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
                    // An HTTP error proves transport reachability, not that this particular service is usable.
                    Interlocked.Exchange(ref _confirmedTicks, DateTime.UtcNow.Ticks); return;
                }
                catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
                { token.ThrowIfCancellationRequested(); last = e; }
            }
            throw new OnlineConnectionException(!Available, last);
        }
        finally { Gate.Release(); }
    }
    internal static async ValueTask<int> ReadAsync(Stream source, Memory<byte> buffer, CancellationToken token)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token); idle.CancelAfter(TimeSpan.FromSeconds(20));
        try { return await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false); }
        catch (OperationCanceledException e) when (!token.IsCancellationRequested) { throw new OnlineConnectionException(!Available, e); }
    }
    internal static string Message(Exception error, bool english)
    {
        if (error is OnlineConnectionException online) return online.Offline
            ? english ? online.Lost ? "Internet connection lost. Local information remains available." : "No internet connection. Local information remains available." : online.Message
            : english ? "Online service unavailable. Check your connection or the provider." : online.Message;
        if (error is OperationCanceledException && !Available) return english ? "Internet connection lost. Please check again." : "Internetverbindung verloren. Bitte erneut prüfen.";
        if (error is HttpRequestException http) return http.StatusCode is { } code
            ? (english ? "The provider returned an error: HTTP " : "Der Anbieter meldet einen Fehler: HTTP ") + (int)code
            : english ? "Online service unreachable. Check your connection and try again." : "Onlinedienst nicht erreichbar. Verbindung prüfen und erneut versuchen.";
        return UiText.Translate(error.Message, english);
    }
}

// Cancels our own queries/downloads on link loss. Never attached to an already running installer.
internal sealed class OnlineRequest : IDisposable
{
    private readonly CancellationTokenSource _cancel;
    private readonly NetworkAvailabilityChangedEventHandler _changed;
    internal CancellationToken Token => _cancel.Token;
    internal OnlineRequest(CancellationToken token)
    {
        OnlineConnection.RequireNetwork();
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        _changed = (_, e) => { if (!e.IsAvailable) { try { _cancel.Cancel(); } catch (ObjectDisposedException) { } } };
        NetworkChange.NetworkAvailabilityChanged += _changed;
    }
    public void Dispose() { NetworkChange.NetworkAvailabilityChanged -= _changed; _cancel.Dispose(); }
}
