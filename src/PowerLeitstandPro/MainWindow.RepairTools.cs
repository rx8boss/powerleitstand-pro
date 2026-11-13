using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using FontFamily = System.Windows.Media.FontFamily;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Control = System.Windows.Controls.Control;

namespace PowerControlProWpf;

public partial class MainWindow
{
    private readonly record struct RepairTile(Button Button, TextBlock Title, TextBlock Description, TextBlock Caption);

    private static readonly (string De, string En, string DescriptionDe, string DescriptionEn, string Icon)[] RepairCategories =
    [
        ("Diagnose", "Diagnostics", "Systemdateien, Laufwerke und Windows-Komponenten",
            "System files, drives and Windows components", "\uE721"),
        ("Reparatur", "Repair", "Windows-Abbild, Systemdateien und Update-Cache",
            "Windows image, system files and update cache", "\uE90F"),
        ("Windows-Werkzeuge", "Windows tools", "Wiederherstellung, DirectX und Verwaltung",
            "System Restore, DirectX and administrative tools", "\uE71D"),
        ("Netzwerk", "Network", "Netzwerk und DNS", "Network and DNS", "\uE839")
    ];

    private RepairTool? _selectedRepairTool;
    private int? _repairCategory;
    private RepairTile[] _repairCategoryTiles = [];
    private readonly Dictionary<RepairTool, RepairTile> _repairToolTiles = [];
    private IEnumerable<Button> RepairToolButtons => _repairToolTiles.Values.Select(tile => tile.Button);
    private IEnumerable<Button> RepairNavigationButtons => _repairCategoryTiles.Select(tile => tile.Button)
        .Append(RepairBackButton).Concat(RepairToolButtons);

    private void BuildRepairToolGroups()
    {
        if (_repairCategoryTiles.Length == 0)
        {
            _repairCategoryTiles = RepairCategories.Select((category, index) =>
            {
                var tile = CreateRepairTile(category.Icon, 18, index);
                tile.Button.Tag = index;
                tile.Button.Click += (_, _) => ShowRepairCategory(index, focus: true);
                return tile;
            }).ToArray();

            foreach (var tool in RepairTool.All)
            {
                var icon = tool.Id switch
                {
                    "check" => "\uE73E",
                    "verify" or "sfc" => "\uE8A5",
                    "scan" => "\uE721",
                    "disk" or "storage-health" => "\uEDA2",
                    "driver-packages" => "\uE7F8",
                    "update-reset" or "dns" or "dhcp-renew" or "adapter-restart" => "\uE72C",
                    "dns-servers" => "\uE713",
                    "network-services" => "\uE713",
                    "update-dism" => "\uE895",
                    "restore" => "\uE777",
                    "dxdiag" => "\uE7F4",
                    "admin-tools" => "\uE713",
                    "device-problems" => "\uE7F8",
                    "system-events" => "\uE9D9",
                    "reliability" => "\uE9D9",
                    _ => RepairCategories[tool.Group].Icon
                };
                var tile = CreateRepairTile(icon, 14, tool.Group);
                tile.Button.Tag = tool.Id;
                tile.Button.Click += async (_, _) => await StartRepairAsync(tool);
                _repairToolTiles.Add(tool, tile);
            }
            ShowRepairCategory(null);
        }

        for (var index = 0; index < RepairCategories.Length; index++)
        {
            var category = RepairCategories[index];
            var count = RepairTool.All.Count(tool => tool.Group == index);
            SetRepairTileText(_repairCategoryTiles[index], _english ? category.En : category.De,
                _english ? category.DescriptionEn : category.DescriptionDe,
                _english ? count == 1 ? "1 tool" : $"{count} tools" : count == 1 ? "1 Werkzeug" : $"{count} Werkzeuge");
        }
        foreach (var (tool, tile) in _repairToolTiles)
            SetRepairTileText(tile, tool.Title(_english), RepairTileDescription(tool), tool.Caption);

        UpdateRepairCategoryHeader();
        if (_repairCategory == 3 && _networkViewEnglish != _english) BuildNetworkView();
        RepairAdminNoticeText.Text = _english
            ? "Start PowerLeitstand as administrator to run these tools. Use the administrator button at the top."
            : "Starte PowerLeitstand als Administrator, um diese Werkzeuge auszuführen. Nutze dafür die Admin-Schaltfläche oben.";
        UpdateRepairTileAvailability();
        UpdateRepairRunLabels();
    }

    private void ShowRepairCategory(int? category, bool focus = false)
    {
        var previousCategory = _repairCategory;
        _repairCategory = category;
        RepairToolGroups.Children.Clear();
        RepairToolGroups.Visibility = category == 3 ? Visibility.Collapsed : Visibility.Visible;
        RepairNetworkPanel.Visibility = category == 3 ? Visibility.Visible : Visibility.Collapsed;
        var tiles = category == 3 ? Enumerable.Empty<RepairTile>() : category is int selected
            ? _repairToolTiles.Where(entry => entry.Key.Group == selected).Select(entry => entry.Value)
            : _repairCategoryTiles;
        foreach (var tile in tiles)
            RepairToolGroups.Children.Add(tile.Button);
        if (category == 3) BuildNetworkView();
        UpdateRepairCategoryHeader();
        UpdateRepairTileLayout();
        RepairToolScroll.ScrollToTop();
        if (focus)
        {
            if (category is not null) RepairBackButton.Focus();
            else if (previousCategory is int previous) _repairCategoryTiles[previous].Button.Focus();
        }
    }

    private void BackToRepairOverview(object sender, RoutedEventArgs e) => ShowRepairCategory(null, focus: true);

    private void UpdateRepairCategoryHeader()
    {
        RepairAdminNotice.Visibility = IsAdmin() || _repairCategory == 3 ? Visibility.Collapsed : Visibility.Visible;
        RepairNetworkAdminHint.Visibility = _repairCategory == 3 && !IsAdmin() ? Visibility.Visible : Visibility.Collapsed;
        RepairNetworkAdminHint.Text = NetworkText("◆ Admin zum Ausführen", "◆ Admin required");
        RepairBackButton.Visibility = _repairCategory is null ? Visibility.Collapsed : Visibility.Visible;
        RepairHeaderIcon.Visibility = _repairCategory is null ? Visibility.Visible : Visibility.Collapsed;
        RepairBackButton.ToolTip = _english ? "Back to repair overview" : "Zurück zur Reparaturübersicht";
        System.Windows.Automation.AutomationProperties.SetName(RepairBackButton, (string)RepairBackButton.ToolTip);
        if (_repairCategory is int index)
        {
            var category = RepairCategories[index];
            RepairTitle.Text = _english ? category.En : category.De;
            RepairHeaderSub.Text = index == 3 ? NetworkText("LAN, WLAN, DNS und Verbindungen prüfen", "Check Ethernet, Wi-Fi, DNS and connections")
                : _english ? category.DescriptionEn : category.DescriptionDe;
        }
        else
        {
            RepairTitle.Text = _english ? "Windows repair" : "Windows-Reparatur";
            RepairHeaderSub.Text = _english ? "Diagnostics, repair and Windows tools" : "Diagnose, Reparaturen und Windows-Werkzeuge";
        }
    }

    private void UpdateRepairTileLayout()
    {
        var compact = _effectiveSizeIndex == 0;
        RepairToolGroups.Columns = _repairCategory is null || compact ? 2 : 3;
        var dense = compact && _repairCategory == 0;
        foreach (var tile in RepairToolGroups.Children.OfType<Button>())
        {
            tile.MinHeight = dense ? 69 : compact ? 100 : _repairCategory is null ? 108 : 116;
            tile.Padding = dense ? new Thickness(10, 2, 10, 2) : new Thickness(13, 12, 13, 12);
            tile.Margin = dense ? new Thickness(4, 2, 4, 2) : new Thickness(4);
        }
    }

    private void UpdateRepairTileAvailability()
    {
        foreach (var button in RepairToolButtons) button.IsEnabled = !IsBusy;
        UpdateNetworkActions();
        RepairBackButton.IsEnabled = !_repairRunning;
    }

    private void ApplyRepairTilePalette(string theme, ThemePalette palette)
    {
        var colors = palette.Colors;
        var card = (Color)ColorConverter.ConvertFromString(colors[3]);
        var input = (Color)ColorConverter.ConvertFromString(colors[4]);
        var themeAccent = (Color)ColorConverter.ConvertFromString(colors[7]);
        var light = theme is "Classic" or "Linux Mint";
        var accents = light ? new[] { "#105B77", "#754810", "#553D85", "#105F4B" }
            : new[] { "#60DCFC", "#F9C281", "#C4B6FF", "#71E3C6" };
        string[] standardStart = ["#082944", "#12283E", "#182B47", "#0A2E42"];
        string[] standardEnd = ["#061D35", "#081E35", "#081D36", "#061F36"];
        string[] standardEdge = ["#397A99", "#77634F", "#626990", "#377B80"];
        for (var category = 0; category < accents.Length; category++)
        {
            var accent = (Color)ColorConverter.ConvertFromString(accents[category]);
            if (palette.IsNeon) accent = BlendColor(accent, themeAccent, .22);
            var start = theme == "Standard" ? (Color)ColorConverter.ConvertFromString(standardStart[category])
                : BlendColor(card, accent, light ? .07 : .13);
            var end = theme == "Standard" ? (Color)ColorConverter.ConvertFromString(standardEnd[category]) : input;
            var edge = theme == "Standard" ? (Color)ColorConverter.ConvertFromString(standardEdge[category])
                : BlendColor(card, accent, light ? .48 : .47);
            var prefix = $"RepairTile{category}";
            var surface = new LinearGradientBrush(start, end, 24);
            surface.Freeze();
            Resources[prefix + "Surface"] = surface;
            Resources[prefix + "Edge"] = new SolidColorBrush(edge);
            Resources[prefix + "Accent"] = new SolidColorBrush(accent);
            Resources[prefix + "Badge"] = new SolidColorBrush(BlendColor(start, accent, light ? .11 : .12));
        }
    }

    private RepairTile CreateRepairTile(string glyph, double titleSize, int category)
    {
        var prefix = $"RepairTile{category}";
        var title = new TextBlock { FontSize = titleSize, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap };
        var detail = new TextBlock { FontSize = 12, FontWeight = FontWeights.Normal, TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
        var caption = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center };
        caption.SetResourceReference(TextBlock.ForegroundProperty, prefix + "Accent");
        var icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 18,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.ForegroundProperty, prefix + "Accent");
        var badge = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 11, 0), Child = icon };
        badge.SetResourceReference(Border.BorderBrushProperty, prefix + "Edge");
        badge.SetResourceReference(Border.BackgroundProperty, prefix + "Badge");

        var arrow = new TextBlock { Text = "›", FontSize = 15, Margin = new Thickness(0, -2, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        arrow.SetResourceReference(TextBlock.ForegroundProperty, prefix + "Accent");
        var arrowBadge = new Border { Width = 26, Height = 20, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
            Margin = new Thickness(8, 0, 0, 0), Child = arrow };
        arrowBadge.SetResourceReference(Border.BorderBrushProperty, prefix + "Edge");
        arrowBadge.SetResourceReference(Border.BackgroundProperty, prefix + "Badge");
        DockPanel.SetDock(arrowBadge, Dock.Right);
        var divider = new Border { Height = 1, Margin = new Thickness(0, 5, 34, 3), Opacity = .75 };
        divider.SetResourceReference(Border.BackgroundProperty, prefix + "Edge");

        var footer = new DockPanel { Children = { arrowBadge, caption } };
        var text = new StackPanel { Children = { title, detail } };
        var bottom = new StackPanel { Children = { divider, footer } };
        var copy = new Grid();
        copy.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        copy.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(bottom, 1);
        copy.Children.Add(text);
        copy.Children.Add(bottom);
        var panel = new DockPanel { Children = { badge, copy } };
        var button = new Button
        {
            Style = (Style)FindResource("RepairTileButton"), Content = panel,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch, Margin = new Thickness(4)
        };
        button.SetResourceReference(Control.BackgroundProperty, prefix + "Surface");
        button.SetResourceReference(Control.BorderBrushProperty, prefix + "Edge");
        return new RepairTile(button, title, detail, caption);
    }

    private string RepairTileDescription(RepairTool tool) => (tool.Id, _english) switch
    {
        ("device-problems", false) => "Geräte und ihre Windows-Fehlercodes anzeigen.",
        ("device-problems", true) => "Show devices and their Windows problem codes.",
        ("system-events", false) => "Bis zu 20 kritische Ereignisse und Fehler anzeigen.",
        ("system-events", true) => "Show up to 20 critical events and errors.",
        ("reliability", false) => "Windows-Zeitachse für Abstürze und Installationen öffnen.",
        ("reliability", true) => "Open the Windows timeline for crashes and installations.",
        ("check", false) => "Bekannten Status des Komponentenspeichers abfragen.",
        ("check", true) => "Read the known state of the Windows component store.",
        ("scan", false) => "Komponentenspeicher gründlich auf Fehler prüfen.",
        ("scan", true) => "Scan the Windows component store for corruption.",
        ("verify", false) => "Geschützte Windows-Dateien auf Fehler prüfen.",
        ("verify", true) => "Check protected Windows files for errors.",
        ("disk", false) => "Dateisystem des Windows-Laufwerks prüfen.",
        ("disk", true) => "Check the file system on the Windows drive.",
        ("storage-health", false) => "Zustand, Temperatur und Verschleiß auslesen.",
        ("storage-health", true) => "Read drive health, temperature and wear.",
        ("driver-packages", false) => "Anbieter, Version und INF-Dateien auslesen.",
        ("driver-packages", true) => "Read providers, versions and INF file names.",
        ("dism", false) => "Den Windows-Komponentenspeicher reparieren.",
        ("dism", true) => "Repair the Windows component store.",
        ("sfc", false) => "Beschädigte Windows-Systemdateien reparieren.",
        ("sfc", true) => "Repair corrupted Windows system files.",
        ("update-reset", false) => "Update-Dienste und Cacheordner zurücksetzen.",
        ("update-reset", true) => "Reset update services and cache folders.",
        ("update-dism", false) => "Update-Cache zurücksetzen und danach DISM ausführen.",
        ("update-dism", true) => "Reset the update cache and then run DISM.",
        ("restore", false) => "Den Assistenten für Wiederherstellungspunkte öffnen.",
        ("restore", true) => "Open the Windows System Restore wizard.",
        ("dxdiag", false) => "Grafik-, Audio- und Treiberinformationen anzeigen.",
        ("dxdiag", true) => "View graphics, audio and driver information.",
        ("admin-tools", false) => "Die Windows-Verwaltungswerkzeuge öffnen.",
        ("admin-tools", true) => "Open Windows administrative tools.",
        ("dns", false) => "Zwischengespeicherte DNS-Antworten entfernen.",
        ("dns", true) => "Clear cached DNS responses.",
        ("network-diagnostics", false) => "Adapter, Netzwerkteams, Gateway, Internet und DNS prüfen.",
        ("network-diagnostics", true) => "Check adapters, network teams, gateways, internet and DNS.",
        ("network-services", false) => "Status von DHCP, DNS und WLAN anzeigen.",
        ("network-services", true) => "Show DHCP, DNS and Wi-Fi service status.",
        _ => (_english ? tool.En : tool.De)[0]
    };


    private static void SetRepairTileText(RepairTile tile, string title, string detail, string caption)
    {
        tile.Title.Text = title;
        tile.Description.Text = detail;
        tile.Caption.Text = caption;
        tile.Button.ToolTip = title + Environment.NewLine + detail;
        System.Windows.Automation.AutomationProperties.SetName(tile.Button, title);
    }


    private void UpdateRepairRunLabels()
    {
        if (_selectedRepairTool is not { } tool) return;
        var title = tool.Title(_english);
        RepairRunningTitle.Text = _english ? $"{title} running" : $"{title} läuft";
        RepairRunningCommand.Text = tool.IsReset ? title + (_english ? " · PowerShell script" : " · PowerShell-Skript")
            : tool.UsesLocalizedQuery ? tool.Caption : tool.Command;
        RepairCopyButton.Content = _english ? "Copy command" : "Befehl kopieren";
        RepairLiveCaption.Text = _english ? "Live output" : "Live-Ausgabe";
        RepairCancelLabel.Text = tool.IsRepair ? _english ? "Stop repair" : "Reparatur beenden"
            : tool.IsNetworkAction ? _english ? "Stop operation" : "Vorgang beenden"
            : _english ? "Stop check" : "Prüfung beenden";
    }

    private void CopyRunningRepairCommand(object sender, RoutedEventArgs e)
    {
        if (_selectedRepairTool is not { } tool) return;
        try
        {
            System.Windows.Clipboard.SetText(tool.CommandFor(_english, _selectedNetworkRequest));
            RepairCopyButton.Content = _english ? "Copied" : "Kopiert";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            RepairCopyButton.Content = _english ? "Try again" : "Erneut versuchen";
        }
    }

    private void AppendRepairLiveOutput(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (RepairLiveOutput.Text.Length > 16000)
            RepairLiveOutput.Text = RepairLiveOutput.Text[^12000..];
        RepairLiveOutput.AppendText($"{_repairWatch.Elapsed:mm\\:ss}  {line.Trim()}{Environment.NewLine}");
        RepairLiveOutput.ScrollToEnd();
    }
}

