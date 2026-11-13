using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PowerControlProWpf;

internal sealed record ProgramDescriptionText(string Text, string? Locale);
internal sealed record ProgramDescriptionMetadata(ProgramDescriptionText Original, ProgramDescriptionText? Localized = null);
internal enum ProgramDescriptionState { Native, Translated, Missing, UnknownLanguage, Unavailable, Limit, TooLong, Timeout }
internal sealed record ProgramDescriptionResult(string Text, string Original, string? Locale, ProgramDescriptionState State);

// Display-only service. Never supplies an install/update proof or changes package metadata.
internal sealed class ProgramDescriptions
{
    internal static ProgramDescriptions Shared { get; } = new();
    private static readonly HttpClient Client = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
    private readonly Func<string, string, string, CancellationToken, Task<ProgramDescriptionMetadata?>> _metadata;
    private readonly Func<Uri, CancellationToken, Task<string>> _request;
    private readonly TimeSpan _timeout;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _gate = new(1);
    private readonly Dictionary<string, (ProgramDescriptionResult Result, DateTime Until)> _cache = [];
    private readonly Dictionary<string, Flight> _flights = [];
    private DateOnly _day;
    private int _characters;
    private bool _quota;
    private sealed class Flight
    {
        internal readonly CancellationTokenSource Stop = new();
        internal Task<ProgramDescriptionResult> Work = null!;
        internal int Readers;
    }
    internal ProgramDescriptions(Func<string, string, string, CancellationToken, Task<ProgramDescriptionMetadata?>>? metadata = null,
        Func<Uri, CancellationToken, Task<string>>? request = null, TimeSpan? timeout = null)
    { _metadata = metadata ?? ProgramDescriptionMetadataReader.ReadAsync; _request = request ?? RequestAsync; _timeout = timeout ?? TimeSpan.FromSeconds(10); }

    internal async Task<ProgramDescriptionResult> GetAsync(ProgramEntry entry, ProgramPackage? package, bool english, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string original = package?.Description?.Trim() ?? "", target = english ? "en" : "de";
        if (!entry.Id.StartsWith("winget:", StringComparison.Ordinal))
            return new(entry.Description(english), entry.Description(english), target, ProgramDescriptionState.Native);
        string signature = string.Join('\n', entry.Id, entry.Name, entry.Publisher, entry.PackageId, package?.Id, package?.Version, original, target);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)));
        Flight flight;
        lock (_sync)
        {
            if (_cache.TryGetValue(key, out var cached) && cached.Until > DateTime.UtcNow) return cached.Result;
            if (!_flights.TryGetValue(key, out flight!))
            {
                flight = new(); _flights[key] = flight;
                flight.Work = RunAsync(key, flight, entry, package, original, target);
            }
            flight.Readers++;
        }
        try { return await flight.Work.WaitAsync(token).ConfigureAwait(false); }
        finally
        {
            lock (_sync)
            {
                if (--flight.Readers == 0)
                {
                    if (_flights.GetValueOrDefault(key) == flight) _flights.Remove(key);
                    if (!flight.Work.IsCompleted) flight.Stop.Cancel();
                    _ = flight.Work.ContinueWith(done => { _ = done.Exception; flight.Stop.Dispose(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
        }
    }
    private async Task<ProgramDescriptionResult> RunAsync(string key, Flight flight, ProgramEntry entry, ProgramPackage? package, string original, string target)
    {
        await Task.Yield(); // Publish the flight before any synchronous fixture or cache completion.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(flight.Stop.Token);
        limit.CancelAfter(_timeout);
        ProgramDescriptionResult result;
        try
        {
            await _gate.WaitAsync(limit.Token).ConfigureAwait(false);
            try { result = await ResolveAsync(entry, package, original, target, limit.Token).ConfigureAwait(false); }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (!flight.Stop.IsCancellationRequested)
        { result = new(original, original, null, ProgramDescriptionState.Timeout); }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or InvalidOperationException or ArgumentException)
        { result = new(original, original, null, ProgramDescriptionState.Unavailable); }
        lock (_sync)
        {
            flight.Stop.Token.ThrowIfCancellationRequested();
            if (_cache.Count >= 128) _cache.Remove(_cache.Keys.First());
            _cache[key] = (result, result.State is ProgramDescriptionState.Native or ProgramDescriptionState.Translated
                ? DateTime.MaxValue : DateTime.UtcNow.AddMinutes(1));
        }
        return result;
    }
    private async Task<ProgramDescriptionResult> ResolveAsync(ProgramEntry entry, ProgramPackage? package, string fallback, string target, CancellationToken token)
    {
        ProgramDescriptionMetadata? metadata = null;
        if (package?.Version is { Length: > 0 } version && string.Equals(package.Id, entry.PackageId, StringComparison.OrdinalIgnoreCase))
            metadata = await _metadata(package.Id, version, target, token).ConfigureAwait(false);
        var source = metadata?.Original ?? new(fallback, null);
        var native = metadata?.Localized;
        if (native is { Text.Length: > 0 } && Language(native.Locale) == target)
            return new(native.Text, source.Text, native.Locale, ProgramDescriptionState.Native);
        string original = source.Text.Trim();
        ProgramDescriptionResult Original(ProgramDescriptionState state) => new(original, original, source.Locale, state);
        if (original.Length == 0) return Original(ProgramDescriptionState.Missing);
        string? from = Language(source.Locale);
        if (from == target) return Original(ProgramDescriptionState.Native);
        if (from is null) return Original(ProgramDescriptionState.UnknownLanguage);
        if (original.Length > 4000) return Original(ProgramDescriptionState.TooLong);
        (string Text, string Prefix, Dictionary<string, string> Tokens) protectedText;
        try { protectedText = Protect(original, entry); }
        catch (RegexMatchTimeoutException) { return Original(ProgramDescriptionState.Unavailable); }
        var chunks = Split(protectedText.Text);
        if (chunks.Count == 0 || chunks.Count > 8) return Original(ProgramDescriptionState.TooLong);
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (_day != today) { _day = today; _characters = 0; _quota = false; }
        int total = chunks.Sum(c => c.Text.Length);
        if (_quota || _characters + total > 5000) return Original(ProgramDescriptionState.Limit);
        var translated = new StringBuilder();
        try
        {
            foreach (var chunk in chunks)
            {
                token.ThrowIfCancellationRequested();
                _characters += chunk.Text.Length;
                var uri = new Uri("https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(chunk.Text)
                    + "&langpair=" + from + "%7C" + target + "&mt=1");
                string json = await _request(uri, token).WaitAsync(token).ConfigureAwait(false);
                if (json.Length > 65536) return Original(ProgramDescriptionState.Unavailable);
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return Original(ProgramDescriptionState.Unavailable);
                if (root.TryGetProperty("quotaFinished", out var quota) && quota.ValueKind == JsonValueKind.True
                    || root.TryGetProperty("responseStatus", out var status) && status.ToString() == "429")
                { _quota = true; return Original(ProgramDescriptionState.Limit); }
                if (!root.TryGetProperty("responseStatus", out status) || status.ToString() != "200"
                    || !root.TryGetProperty("responseData", out var data) || data.ValueKind != JsonValueKind.Object
                    || !data.TryGetProperty("translatedText", out var text) || text.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(text.GetString())) return Original(ProgramDescriptionState.Unavailable);
                string value = WebUtility.HtmlDecode(text.GetString()!);
                if (value.Length > 16000 || value.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
                    return Original(ProgramDescriptionState.Unavailable);
                translated.Append(value.Trim()).Append(chunk.After);
            }
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.TooManyRequests)
        { _quota = true; return Original(ProgramDescriptionState.Limit); }
        catch (OperationCanceledException) { return Original(ProgramDescriptionState.Timeout); }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or InvalidOperationException or ArgumentException)
        { return Original(ProgramDescriptionState.Unavailable); }
        string answer = translated.ToString().Trim();
        foreach (var pair in protectedText.Tokens)
        {
            if (Regex.Matches(answer, Regex.Escape(pair.Key)).Count != 1) return Original(ProgramDescriptionState.Unavailable);
            answer = answer.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
        }
        if (answer.Contains(protectedText.Prefix, StringComparison.Ordinal)) return Original(ProgramDescriptionState.Unavailable);
        token.ThrowIfCancellationRequested();
        return new(answer, original, source.Locale, ProgramDescriptionState.Translated);
    }
    internal static string? Language(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale) || locale.Length > 64 || !Regex.IsMatch(locale, @"^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*$")) return null;
        try { return System.Globalization.CultureInfo.GetCultureInfo(locale).TwoLetterISOLanguageName is { Length: 2 } language ? language : null; }
        catch (System.Globalization.CultureNotFoundException) { return null; }
    }
    private static (string Text, string Prefix, Dictionary<string, string> Tokens) Protect(string text, ProgramEntry entry)
    {
        string prefix = "ZXQ"; while (text.Contains(prefix, StringComparison.Ordinal)) prefix += "Q";
        var tokens = new Dictionary<string, string>();
        string names = string.Join('|', new[] { entry.Name, entry.PackageId, entry.Publisher }.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => Regex.Escape(n!)).OrderByDescending(n => n.Length));
        string pattern = @"https?://[^\s<>]+|[A-Za-z]:\\[^\r\n;,]+|\b\d+(?:[._+-]\w+)+\b|\b[A-Za-z][\w-]*(?:\.[A-Za-z][\w-]*)+\b" + (names.Length == 0 ? "" : "|" + names);
        string value = Regex.Replace(text, pattern, match => { string key = prefix + tokens.Count + "QXZ"; tokens[key] = match.Value; return key; }, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        return (value, prefix, tokens);
    }
    internal static IReadOnlyList<(string Text, string After)> Split(string text)
    {
        var chunks = new List<(string, string)>();
        while (text.Length > 0)
        {
            if (Encoding.UTF8.GetByteCount(text) <= 500) { chunks.Add((text, "")); break; }
            int length = 0, bytes = 0, boundary = 0;
            foreach (var rune in text.EnumerateRunes())
            {
                if (bytes + rune.Utf8SequenceLength > 500) break;
                bytes += rune.Utf8SequenceLength; length += rune.Utf16SequenceLength;
                if (Rune.IsWhiteSpace(rune)) boundary = length;
            }
            // Do not split identifiers, placeholder tokens or words at an arbitrary byte.
            if (boundary == 0) return [];
            string head = text[..boundary], trimmed = head.TrimEnd();
            if (trimmed.Length > 0) chunks.Add((trimmed, head[trimmed.Length..]));
            text = text[boundary..];
        }
        return chunks;
    }
    private static async Task<string> RequestAsync(Uri uri, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd(AppVersion.UserAgent);
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 65536) throw new IOException("Translation response too large");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var memory = new MemoryStream(); byte[] buffer = new byte[4096]; int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        { if (memory.Length + count > 65536) throw new IOException("Translation response too large"); memory.Write(buffer, 0, count); }
        return Encoding.UTF8.GetString(memory.ToArray());
    }
}
