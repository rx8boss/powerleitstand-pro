namespace PowerControlProWpf;

internal sealed record RepairLogEntry(string Timestamp, string Phase, string Level, string Message)
{
    public string DisplayLevel { get; init; } = Level;
    public string DisplayMessage { get; init; } = Message;
    internal RepairLogEntry WithLanguage(bool english) => this with
    {
        DisplayLevel = english ? Level switch { "FEHLER" => "ERROR", "ABBRUCH" => "CANCELLED", "ERFOLG" => "SUCCESS", "HINWEIS" => "NOTICE", _ => Level } : Level,
        DisplayMessage = UiText.Translate(Message, english)
    };
    internal string ToLine() => $"[{Timestamp}] [{Phase}] [{Level}] {Message}";

    internal static RepairLogEntry Parse(string line)
    {
        var parts = line.Split("] [", 3, StringSplitOptions.None);
        if (parts.Length == 3 && parts[0].StartsWith('[') && parts[2].Contains("] "))
        {
            var last = parts[2].Split("] ", 2, StringSplitOptions.None);
            return new(parts[0][1..], parts[1], last[0], last[1]);
        }
        return new("", "ALT", "INFO", line);
    }
}
