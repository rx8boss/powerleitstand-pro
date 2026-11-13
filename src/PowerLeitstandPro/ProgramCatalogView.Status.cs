using System.Windows.Media;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace PowerControlProWpf;

internal sealed partial class ProgramCatalogView
{
    private enum CheckStage { Idle, Checking, Partial, Complete, Stopped, Failed }
    private CheckStage _checkStage;
    private bool _checkHasFreshResults;

    private void BeginCheckStatus()
    {
        _checkHasFreshResults = false;
        _checkStage = CheckStage.Checking;
    }
    private void ObserveCheckStatus(ProgramInventory inventory)
    {
        if (inventory.IsStale) return;
        if (inventory.Installed.Count == 0 && inventory.Updates?.Count is not > 0 && !inventory.UpdateCheckComplete && !inventory.Complete) return;
        _checkHasFreshResults = true;
        _checkStage = CheckStage.Partial;
    }
    private void FinishCheckStatus(ProgramInventory inventory)
    {
        ObserveCheckStatus(inventory);
        _checkStage = inventory.Complete && inventory.UpdateCheckComplete && inventory.StoreCheckComplete && inventory.UpdateSourcesComplete && !inventory.IsPartial && !inventory.IsStale
            && inventory.Error is null && inventory.OnlineError is null && inventory.StoreError is null
            ? CheckStage.Complete : _checkHasFreshResults ? CheckStage.Partial : CheckStage.Failed;
    }
    private void InterruptCheckStatus(bool failed) => _checkStage = failed ? CheckStage.Failed : CheckStage.Stopped;

    private void ApplyCheckStatusAppearance()
    {
        if (Inventory.IsStale && !IsWorking && !_refreshing && !_searching)
        {
            _status.Text = SavedStatus();
            _status.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "SubtleTextBrush"); return;
        }
        if (IsWorking || _checkStage == CheckStage.Idle)
        {
            _status.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "SubtleTextBrush");
            return;
        }
        bool partial = _checkStage == CheckStage.Partial || _checkHasFreshResults && (_checkStage is CheckStage.Stopped or CheckStage.Failed);
        bool complete = _checkStage == CheckStage.Complete;
        if (_checkStage == CheckStage.Checking)
            _status.Text = _searching ? L("WinGet-Suche und Installationsprüfung …", "WinGet search and installation check …")
                : L("Installierte Programme werden geprüft …", "Checking installed programs …");
        else if (_checkStage == CheckStage.Partial && (_refreshing || _searching))
            _status.Text = L("Teilweise geprüft · weitere Programme werden geprüft …", "Partially checked · checking more programs …");
        else if (_checkStage == CheckStage.Stopped)
            _status.Text = L("Prüfung beendet · nicht vollständig geprüft", "Check stopped · verification incomplete");
        else if (_checkStage == CheckStage.Failed)
            _status.Text = L("Prüfung fehlgeschlagen · nicht vollständig geprüft", "Check failed · verification incomplete");

        string bright = complete ? "#00D7A0" : partial ? "#FFB347" : "#FF5A6B";
        string dark = complete ? "#075B37" : partial ? "#794100" : "#991B2F";
        Color[] backgrounds = TryFindResource("StandardReferenceCardBrush") switch
        {
            SolidColorBrush solid => [solid.Color],
            GradientBrush gradient when gradient.GradientStops.Count > 0 => gradient.GradientStops.Select(stop => stop.Color).ToArray(),
            _ => [(Color)ColorConverter.ConvertFromString(Palette.Colors[2])]
        };
        var lightColor = (Color)ColorConverter.ConvertFromString(bright);
        var darkColor = (Color)ColorConverter.ConvertFromString(dark);
        double Luminance(Color c)
        {
            double Channel(byte v) => v / 255d <= .04045 ? v / 255d / 12.92 : Math.Pow((v / 255d + .055) / 1.055, 2.4);
            return .2126 * Channel(c.R) + .7152 * Channel(c.G) + .0722 * Channel(c.B);
        }
        double Contrast(Color c) => backgrounds.Min(background =>
            (Math.Max(Luminance(c), Luminance(background)) + .05) / (Math.Min(Luminance(c), Luminance(background)) + .05));
        Color Better(Color first, Color second) => Contrast(first) >= Contrast(second) ? first : second;
        Color Blend(Color color, byte destination, double amount) => Color.FromRgb(
            (byte)Math.Round(color.R + (destination - color.R) * amount),
            (byte)Math.Round(color.G + (destination - color.G) * amount),
            (byte)Math.Round(color.B + (destination - color.B) * amount));
        var chosen = Better(lightColor, darkColor);
        // Preserve the approved base colors where readable. Adjust only for theme contrast.
        for (int tint = 1; Contrast(chosen) < 4.5 && tint <= 20; tint++)
            chosen = Better(chosen, Better(Blend(lightColor, 255, tint / 20d), Blend(darkColor, 0, tint / 20d)));
        var brush = new SolidColorBrush(chosen);
        brush.Freeze(); _status.Foreground = brush; UpdateCheckProgressOverlay();
    }
}
