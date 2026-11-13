using System.Text.RegularExpressions;

namespace PowerControlProWpf;

internal enum ProgramStep { Check, Download, Install, Finish }
internal enum ProgramStepState { Waiting, Active, Done, Unknown, Failed, Skipped }
internal sealed record ProgramStepEvent(ProgramStep Step, ProgramStepState State);
internal interface IProgramStepSource { event Action<ProgramStepEvent>? StepChanged; }

// Only documented WinGet reporter milestones, never percentages or elapsed-time guesses.
// Resource strings: microsoft/winget-cli en-us and Localization/Resources/de-DE/winget.resw.
internal sealed class ProgramWinGetSteps(Action<ProgramStepEvent> report, string? packageId = null, Action<ProgramDownload>? download = null)
{
    private bool _dependencies, _hash, _install;
    private bool _downloading;
    private readonly object _gate = new();
    internal bool DownloadConfirmed { get { lock (_gate) return _hash; } }
    private readonly HashSet<ProgramStepEvent> _sent = [];
    private void Send(ProgramStep step, ProgramStepState state)
    { var e = new ProgramStepEvent(step, state); if (_sent.Add(e)) report(e); }
    internal void Line(string input)
    { lock (_gate) Parse(input); }
    private void Parse(string input)
    {
        string line = Regex.Replace(input, @"\x1B\[[0-?]*[ -/]*[@-~]", "").Trim().TrimStart('\uFEFF');
        // Nested dependency reporters have the same milestone strings. Do not attribute
        // their success to the selected application; retain an explicit unknown state.
        if (Regex.IsMatch(line, @"^\(\d+/\d+\)\s"))
        {
            // Resume only on an explicit selected-package identity, never by counting successes.
            _dependencies = packageId is null || !line.Contains("[" + packageId + "]", StringComparison.OrdinalIgnoreCase);
            _hash = _install = _downloading = false;
        }
        else if (_dependencies && packageId is not null && Regex.IsMatch(line, @"^(?:Found|Gefunden) .+\[" + Regex.Escape(packageId) + @"\](?:\s|$)", RegexOptions.IgnoreCase))
        { _dependencies = false; _hash = _install = _downloading = false; }
        if (_dependencies) return;
        if (!_install && Regex.IsMatch(line, @"^(?:Downloading|Download läuft) https?://\S+$"))
        { _downloading = true; Send(ProgramStep.Download, ProgramStepState.Active); }
        else if (line is "Successfully verified installer hash" or "Der Installer-Hash wurde erfolgreich überprüft")
        { _hash = true; _downloading = false; Send(ProgramStep.Download, ProgramStepState.Done); }
        else if (!_install && line is "Starting package install..." or "Paketinstallation wird gestartet...")
        {
            if (!_hash) Send(ProgramStep.Download, ProgramStepState.Unknown);
            _downloading = false; _install = true; Send(ProgramStep.Install, ProgramStepState.Active);
        }
        else if (_install && line is "Successfully installed" or "Erfolgreich installiert")
        { Send(ProgramStep.Install, ProgramStepState.Done); }
        else if (_downloading && TryBytes(line) is { } bytes) download?.Invoke(bytes);
    }
    internal static ProgramDownload? TryBytes(string line)
    {
        var m = Regex.Match(line, @"(?:^|\s)(\d+(?:[.,]\d+)?)\s+(B|KB|MB|GB)\s*/\s*(\d+(?:[.,]\d+)?)\s+(B|KB|MB|GB)\s*$", RegexOptions.CultureInvariant);
        if (!m.Success) return null;
        long Bytes(int number, int unit) => checked((long)(double.Parse(m.Groups[number].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture)
            * (m.Groups[unit].Value switch { "KB" => 1024d, "MB" => 1024d * 1024, "GB" => 1024d * 1024 * 1024, _ => 1d })));
        try { long current = Bytes(1, 2), total = Bytes(3, 4); return total > 0 && current <= total ? new(current, total) : null; }
        catch (Exception e) when (e is OverflowException or FormatException) { return null; }
    }
}
