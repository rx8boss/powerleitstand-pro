using System.Windows;
using System.Windows.Controls;

namespace PowerControlProWpf;

internal sealed partial class ProgramInfoCard
{
    private readonly CancellationTokenSource _descriptionCancel = new();
    private readonly TextBlock _descriptionNote;
    private string _descriptionDetails = "";
    internal Task DescriptionLoading { get; private set; } = Task.CompletedTask;
    private async Task LoadDescriptionAsync(ProgramPackage? package)
    {
        if (_closed || !_entry.Id.StartsWith("winget:", StringComparison.Ordinal)) return;
        _descriptionNote.Text = L("Beschreibung wird sprachlich ergänzt …", "Loading description language …");
        _descriptionNote.Visibility = Visibility.Visible;
        try
        {
            var result = await _view.Descriptions.GetAsync(_entry, package, _view.English, _descriptionCancel.Token);
            if (_closed || _descriptionCancel.IsCancellationRequested) return;
            _what.Text = result.Text.Length > 0 ? result.Text
                : L("Keine bestätigte Herstellerbeschreibung verfügbar. Details über die Paketquelle öffnen.", "No confirmed publisher description available. Open the package source for details.");
            _descriptionNote.Text = result.State switch
            {
                ProgramDescriptionState.Translated => L("Automatisch übersetzt · MyMemory", "Automatically translated · MyMemory"),
                ProgramDescriptionState.Native => "",
                ProgramDescriptionState.UnknownLanguage => L("Originaltext · Ausgangssprache nicht bestätigt.", "Original text · source language not confirmed."),
                ProgramDescriptionState.Limit => L("Originaltext · Übersetzungslimit erreicht. Später erneut versuchen.", "Original text · translation limit reached. Try again later."),
                ProgramDescriptionState.TooLong => L("Originaltext · Beschreibung überschreitet die Übersetzungsgrenze.", "Original text · description exceeds the translation limit."),
                ProgramDescriptionState.Timeout => L("Originaltext · Übersetzung hat zu lange gedauert.", "Original text · translation timed out."),
                ProgramDescriptionState.Unavailable => L("Originaltext · Übersetzung derzeit nicht erreichbar. Internetverbindung prüfen.", "Original text · translation currently unavailable. Check your internet connection."),
                _ => ""
            };
            _descriptionNote.Visibility = _descriptionNote.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            _descriptionDetails = result.Original.Length == 0 ? "" : L("\n\nOriginalbeschreibung · WinGet", "\n\nOriginal description · WinGet")
                + (result.Locale is { Length: > 0 } ? " · " + result.Locale : "") + "\n" + result.Original
                + (result.State == ProgramDescriptionState.Translated ? L("\nÜbersetzungsquelle: MyMemory", "\nTranslation source: MyMemory") : "");
            RefreshInventory();
        }
        catch (OperationCanceledException) when (_descriptionCancel.IsCancellationRequested) { }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or System.IO.IOException or InvalidOperationException)
        {
            if (_closed) return;
            _descriptionNote.Text = L("Originaltext · Übersetzung derzeit nicht verfügbar.", "Original text · translation currently unavailable.");
        }
    }
}
