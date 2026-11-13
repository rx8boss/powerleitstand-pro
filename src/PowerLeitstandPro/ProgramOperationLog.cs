using System.Text.RegularExpressions;

namespace PowerControlProWpf;

// Console output is data. Only explicit severities and verified lifecycle messages become events.
internal sealed class ProgramOperationLog(string source, string operation, bool english, Action<string>? events, Action<AppLogError>? errors)
{
    private readonly string _id = Guid.NewGuid().ToString("N");
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _context = new();
    private bool _reportedError;
    internal void Event(string text)
    { if (_seen.Count < 512 && _seen.Add("E:" + text)) events?.Invoke(text); }
    internal void Error(string message, string? code = null)
    {
        string text = AppLogStore.Clean(message);
        if (code is null && Regex.Match(text, @"\b0x[0-9a-f]{8}\b", RegexOptions.IgnoreCase) is { Success: true } nativeCode) code = nativeCode.Value;
        if (text.Length == 0 || _seen.Count < 512 && !_seen.Add("X:" + code + ":" + text)) return;
        _reportedError = true;
        if (errors is not null) errors(new(source, operation, text, code, _id));
        else events?.Invoke(source + ": ERROR · " + text);
    }
    internal void Output(string output)
    {
        foreach (string raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = AppLogStore.Clean(raw);
            if (line.Length == 0) continue;
            if (Regex.IsMatch(line, @"^(?:ERROR:?\s*)?(?:(?:The )?operation (?:was )?)?cancel(?:led|ed) by (?:the )?user[.!]?$", RegexOptions.IgnoreCase))
            { Event(source + (english ? ": Cancelled by the user." : ": Vom Benutzer abgebrochen.")); continue; }
            if (Regex.IsMatch(line, @"^(?:\[?(?:ERROR|FEHLER|FATAL)\]?|[A-Za-z_][\w.]*Exception)(?:\s|:|$)", RegexOptions.IgnoreCase))
            { Error(line); continue; }
            if (Regex.IsMatch(line, @"^\[?(?:WARN|WARNING|WARNUNG)\]?(?:\s|:|$)", RegexOptions.IgnoreCase))
            { Event(source + ": " + line); continue; }
            if (Regex.IsMatch(line, @"^(?:Updating files:|\*?\s*[a-f0-9]{7,40}\s|remote:|Receiving objects:|Resolving deltas:|\d+(?:[,.]\d+)?\s*%|\d+(?:[,.]\d+)?\s*[KMGT]?B\s*/)", RegexOptions.IgnoreCase)) continue;
            if (line == "Updating Scoop...") Event(english ? "Scoop: Updating the package manager …" : "Scoop: Paketmanager wird aktualisiert …");
            else if (line == "Updating Buckets...") Event(english ? "Scoop: Updating package sources …" : "Scoop: Paketquellen werden aktualisiert …");
            // Keep a small failure context in memory, without persisting commit lists or console dumps.
            else { _context.Enqueue(line.Length > 400 ? line[..400] + " …" : line); while (_context.Count > 6) _context.Dequeue(); }
        }
    }
    internal void ProcessEnded(int code)
    {
        if (code is 0 or 3010 or 1602 or 1223 || code == unchecked((int)0x8A15010C)
            || code == unchecked((int)0x8A150109) || code == unchecked((int)0x8A15010A) || code == unchecked((int)0x8A15010B)) return;
        Error((english ? "Process failed." : "Vorgang fehlgeschlagen.")
            + (_reportedError || _context.Count == 0 ? "" : " " + string.Join(" | ", _context)), "0x" + code.ToString("X8"));
    }
}
