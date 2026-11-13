using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PowerControlProWpf;

internal sealed partial class ProgramInstallService
{
    private async Task<Dictionary<string, string>> CompleteGithubNoticesAsync(ProgramEntry[] entries,
        IReadOnlyDictionary<string, string?> earlyInstalled, IReadOnlyDictionary<string, string?> installed,
        Task<Dictionary<string, string>> early, CancellationToken token, Action<string>? failed = null)
    {
        var result = await early.ConfigureAwait(false);
        if (!_scanGitHub) return result;
        var changed = entries.Where(e => installed.ContainsKey(e.Id)
            && (!earlyInstalled.ContainsKey(e.Id) || earlyInstalled[e.Id] != installed[e.Id])).ToArray();
        foreach (var e in changed) result.Remove(e.Id);
        if (changed.Length > 0) foreach (var p in await ReadGithubNoticesAsync(changed, installed, token, failed).ConfigureAwait(false)) result[p.Key] = p.Value;
        return result;
    }
    private async Task<(string Id, GitHubProgramUpdate? Direct, string? WinGet)[]> ConfirmGithubRoutesAsync(
        ProgramEntry[] entries, IReadOnlyDictionary<string, ProgramInstallation> evidence,
        IReadOnlyDictionary<string, string> versions, CancellationToken token, IReadOnlySet<string>? alreadyConfirmed = null)
    {
        using var gate = new SemaphoreSlim(3);
        using var showGate = new SemaphoreSlim(1);
        return await Task.WhenAll(entries.Where(e => versions.ContainsKey(e.Id) && evidence.ContainsKey(e.Id)).Select(async entry =>
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(12));
                var proof = evidence[entry.Id];
                if (ProgramGitHubUpdates.Eligible(entry.Id, proof))
                {
                    var direct = await ProgramGitHubUpdates.ResolveAsync(entry, proof, limit.Token).ConfigureAwait(false);
                    if (direct?.Version == versions[entry.Id]) return (entry.Id, Direct: direct, WinGet: (string?)null);
                }
                if (entry.PackageId is not null && proof.WinGetCompatible && alreadyConfirmed?.Contains(entry.Id) != true)
                {
                    // Query only: ResolveAsync also mutates service metadata and must not be called in parallel here.
                    await showGate.WaitAsync(token).ConfigureAwait(false);
                    WinGetResult response;
                    try
                    {
                        using var queryLimit = CancellationTokenSource.CreateLinkedTokenSource(token); queryLimit.CancelAfter(TimeSpan.FromSeconds(12));
                        response = await _query(Command(entry, false), queryLimit.Token).ConfigureAwait(false);
                    }
                    finally { showGate.Release(); }
                    var match = Regex.Match(response.Output, @"(?im)^\s*Version\s*:\s*([A-Za-z0-9._+\-]+)\s*$");
                    if (response.ExitCode == 0 && response.Output.Contains("[" + PackageId(entry) + "]", StringComparison.Ordinal)
                        && match.Success && ProgramPortableUpdates.SameVersion(match.Groups[1].Value, versions[entry.Id]))
                        return (entry.Id, Direct: (GitHubProgramUpdate?)null, WinGet: match.Groups[1].Value);
                }
            }
            catch (Exception e) when (e is IOException or HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException)
            { token.ThrowIfCancellationRequested(); }
            finally { gate.Release(); }
            return (entry.Id, Direct: (GitHubProgramUpdate?)null, WinGet: (string?)null);
        })).ConfigureAwait(false);
    }
}
