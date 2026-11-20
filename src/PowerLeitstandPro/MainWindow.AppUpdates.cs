using System.Windows;
using System.Windows.Controls;

namespace PowerControlProWpf;

public partial class MainWindow
{
    private IReadOnlyList<UpdateRelease> _lastAppReleases = [];

    private void ApplyAppReleases(IReadOnlyList<UpdateRelease> releases)
    {
        _lastAppReleases = releases;
        _availableUpdate = UpdateReleaseCatalog.Visible(releases, AppVersion.Build, _settings.UpdatePreference)
            .FirstOrDefault(r => r.Update.Channel == UpdateChannel.Stable && r.Update.Build.CompareTo(AppVersion.Build) > 0)?.Update;
        _updateCheckFailedConnection = false;
        SetUpdateCheckState(_availableUpdate is null ? UpdateCheckState.Current : UpdateCheckState.Available);
    }

    private IReadOnlyList<UpdateRelease> OptionalUpdates() => UpdateReleaseCatalog.Visible(_lastAppReleases, AppVersion.Build, _settings.UpdatePreference)
        .Where(r => r.Update.Channel != UpdateChannel.Stable).ToArray();

    internal void SaveUpdatePreference(UpdatePreference value)
    {
        _settings.UpdatePreference = value;
        try { if (!SkipSavedPlacementForTests) _settings.Save(); }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        { LogError((_english ? "Could not save update preference: " : "Update-Auswahl konnte nicht gespeichert werden: ") + e.Message); }
        if (_updateCheckState is UpdateCheckState.Current or UpdateCheckState.Available)
            ApplyAppReleases(_lastAppReleases);
        else RefreshOptionalUpdateBadge();
    }

    private void RefreshOptionalUpdateBadge()
    {
        if (UpdateOptionalBadge is null) return;
        int count = _updateCheckState is UpdateCheckState.Current or UpdateCheckState.Available ? OptionalUpdates().Count : 0;
        UpdateOptionalBadge.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateOptionalCount.Text = count.ToString();
        string label = _english ? "Check for app updates" : "Nach App-Updates suchen";
        if (count > 0) label += (_english ? " · Optional updates: " : " · Optionale Updates: ") + count;
        System.Windows.Automation.AutomationProperties.SetName(UpdateNoticeButton, label);
    }

    private void ShowOptionalUpdateNotice()
    {
        string? stable = _lastAppReleases.Where(r => r.Update.Channel == UpdateChannel.Stable).MaxBy(r => r.Update.Version)?.Update.Version.ToString();
        OptionalUpdateStatus.Text = _settings.UpdatePreference == UpdatePreference.Beta
            ? (_english ? "Beta previews" : "Beta-Vorabversionen")
            : _availableUpdate is not null
            ? (_english ? "Stable update available · " : "Stabiles Update verfügbar · ") + _availableUpdate.Version
            : AppVersion.Build.Channel == UpdateChannel.Beta
                ? (_english ? "Stable release on GitHub · " : "Stabile Version auf GitHub · ") + (stable ?? "–")
                : (_english ? "Stable version is up to date · " : "Stabile Version ist aktuell · ") + AppVersion.Installed;
        OptionalUpdateItems.Children.Clear();
        foreach (var release in OptionalUpdates())
        {
            var title = new TextBlock
            {
                Text = release.Update.Build.Display + (_english ? " available" : " verfügbar"),
                FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            var summary = AppReleaseNotes.Select(release.Notes, _english, includeFallbackNotice: false).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => !line.StartsWith('#'))?.TrimStart('-', '•', ' ');
            if (string.IsNullOrWhiteSpace(summary))
                summary = release.Update.Channel == UpdateChannel.Beta ? (_english ? "Preview for testing." : "Vorabversion zum Testen.")
                    : (_english ? "Fixes for this version." : "Fehlerkorrekturen für diese Version.");
            var info = new TextBlock { Text = summary.Length > 120 ? summary[..117] + " …" : summary, FontSize = 11, TextWrapping = TextWrapping.Wrap };
            info.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var item = new StackPanel { Margin = new Thickness(0, 0, 0, 7) };
            item.Children.Add(title);
            item.Children.Add(info);
            OptionalUpdateItems.Children.Add(item);
        }
        OptionalUpdatePopup.IsOpen = true;
    }

    private void OpenOptionalUpdateDetails(object sender, RoutedEventArgs e)
    {
        OptionalUpdatePopup.IsOpen = false;
        ShowAppUpdateDialog(_lastAppReleases);
    }
}
