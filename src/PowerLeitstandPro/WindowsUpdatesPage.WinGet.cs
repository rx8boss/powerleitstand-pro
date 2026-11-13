using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ToolTip = System.Windows.Controls.ToolTip;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;

namespace PowerControlProWpf;

public partial class WindowsUpdatesPage
{
    internal IWinGetService WinGet { get; set; } = new WinGetService();
    internal WinGetAvailability WinGetState { get; private set; }
    private bool _checkingWinGet;
    private bool _winGetInitialized;
    private void InitializeWinGet()
    {
        WinGetButton.Click += async (_, _) =>
        {
            if (_working || HostBusy() || _checkingWinGet) return;
            if (WinGetState != WinGetAvailability.Ready)
            {
                try { WinGet.OpenSetup(); }
                catch (Exception e) { ThemedMessageBox.Show(Window.GetWindow(this), L("Microsoft App Installer konnte nicht geöffnet werden: ", "Could not open Microsoft App Installer: ") + UiText.Error(e, _english)); }
            }
            await RefreshWinGetAsync();
        };
        Loaded += async (_, _) =>
        {
            if (_winGetInitialized || Window.GetWindow(this) is MainWindow { SkipSavedPlacementForTests: true }) return;
            _winGetInitialized = true;
            if (Window.GetWindow(this) is Window host)
                host.Activated += async (_, _) =>
                {
                    if (IsVisible && WinGetState == WinGetAvailability.Missing && !_checkingWinGet && !_working && !HostBusy()) await RefreshWinGetAsync();
                };
            if (IsVisible) await RefreshWinGetAsync();
        };
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible && _winGetInitialized && WinGetState != WinGetAvailability.Ready && !_checkingWinGet) await RefreshWinGetAsync();
        };
    }
    internal async Task RefreshWinGetAsync()
    {
        if (_checkingWinGet) return;
        _checkingWinGet = true; WinGetState = WinGetAvailability.Checking; UpdateWinGetCaption();
        try { WinGetState = await WinGet.ProbeAsync(CancellationToken.None) ? WinGetAvailability.Ready : WinGetAvailability.Missing; }
        catch (Exception e) { WinGetState = WinGetAvailability.Missing; WriteLog?.Invoke(L("WinGet-Prüfung fehlgeschlagen: ", "WinGet check failed: ") + UiText.Error(e, _english)); }
        finally { _checkingWinGet = false; UpdateWinGetCaption(); _manufacturerView?.UpdateInstallRoutes(); }
    }
    private void UpdateWinGetCaption()
    {
        if (WinGetButton is null) return;
        SetAction(WinGetButton, WinGetState switch
        {
            WinGetAvailability.Ready => L("WinGet bereit", "WinGet ready"),
            WinGetAvailability.Checking => L("WinGet prüfen…", "Checking WinGet…"),
            _ => L("WinGet einrichten", "Set up WinGet")
        }, "M 2,4 H 22 V 21 H 2 Z M 2,9 H 22 M 6,13 L 10,16 L 6,19 M 13,19 H 18");
        foreach (var t in UnifiedDialogAppearance.Children<TextBlock>(WinGetButton)) t.FontSize = 11;
        WinGetButton.Height = IsCompactLayout ? 32 : 34;
        WinGetButton.IsEnabled = !_working && !HostBusy() && !_checkingWinGet;
        var body = WinGetState switch
        {
            WinGetAvailability.Ready => L("WinGet ist verfügbar. Es wird für unterstützte Herstellerprogramme und die Programmauswahl benötigt.", "WinGet is available. It is required for supported manufacturer applications and program selection."),
            WinGetAvailability.Checking => L("Die Verfügbarkeit von WinGet wird geprüft.", "Checking whether WinGet is available."),
            _ => L("WinGet ist noch nicht verfügbar. Es wird für unterstützte Herstellerprogramme und die Programmauswahl benötigt.", "WinGet is not available yet. It is required for supported manufacturer applications and program selection.")
        };
        body += "\n\n" + L("Windows-Updates und Treiber über Windows Update funktionieren unabhängig davon.", "Windows updates and drivers through Windows Update work independently.");
        body += "\n\n" + (WinGetState == WinGetAvailability.Ready
            ? L("Klicken prüft die Verfügbarkeit erneut.", "Click to check availability again.")
            : L("Klicken öffnet den offiziellen Microsoft App Installer. Die Einrichtung bestätigst du selbst. Danach hier erneut prüfen.", "Click to open the official Microsoft App Installer. You confirm setup yourself. Then check again here."));
        var lines = new StackPanel();
        var title = new TextBlock { Text = L("WinGet · Installationshilfe", "WinGet · Installation help"), FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 9) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); lines.Children.Add(title);
        var explanation = new TextBlock { Text = body, FontSize = 11, LineHeight = 16, TextWrapping = TextWrapping.Wrap, MaxWidth = 310 };
        explanation.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush"); lines.Children.Add(explanation);
        var border = new Border { Child = lines, Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "StandardReferenceStatusBrush"); border.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        var tooltip = new ToolTip
        {
            Content = border, Width = 338, PlacementTarget = WinGetButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Custom,
            CustomPopupPlacementCallback = (popup, target, _) =>
                [new System.Windows.Controls.Primitives.CustomPopupPlacement(new Point(target.Width - popup.Width, -popup.Height - 8), System.Windows.Controls.Primitives.PopupPrimaryAxis.Vertical)],
            Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent
        };
        if (WinGetButton.ToolTip is ToolTip previous) previous.IsOpen = false;
        WinGetButton.ToolTip = tooltip;
        WinGetButton.GotKeyboardFocus -= ShowWinGetHelp;
        WinGetButton.GotKeyboardFocus += ShowWinGetHelp;
        WinGetButton.LostKeyboardFocus -= HideWinGetHelp;
        WinGetButton.LostKeyboardFocus += HideWinGetHelp;
    }
    private void ShowWinGetHelp(object sender, RoutedEventArgs e) { if (WinGetButton.ToolTip is ToolTip help) { help.PlacementTarget = WinGetButton; help.IsOpen = true; } }
    private void HideWinGetHelp(object sender, RoutedEventArgs e) { if (WinGetButton.ToolTip is ToolTip help) help.IsOpen = false; }
}
