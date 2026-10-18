using System.Runtime.InteropServices;
using System.IO;
using System.Windows;
using System.Drawing;
using System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace PowerControlProWpf;

public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private System.Drawing.Icon? _ownedIcon;
    private readonly List<System.Drawing.Image> _trayImages = new();
    private string? _trayImageSet;
    private Forms.ToolStripMenuItem? _openItem;
    private Forms.ToolStripMenuItem? _centerItem;
    private Forms.ToolStripLabel? _quickItem;
    private Forms.ToolStripMenuItem? _shutdownItem;
    private Forms.ToolStripMenuItem? _restartItem;
    private Forms.ToolStripMenuItem? _lockItem;
    private Forms.ToolStripMenuItem? _exitItem;
    private MainWindow? _mainWindow;
    private bool _english;
    private bool _updateBalloonActive;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 0 && e.Args[0] == "--repair-helper")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await RepairEngine.RunHelperAsync(e.Args); }
            finally { Shutdown(); }
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--update-helper")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await UpdateService.RunHelperAsync(e.Args); }
            finally { Shutdown(); }
            return;
        }
        var trayStart = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        string? elevationError = null;
        if (!trayStart && !e.Args.Contains("--elevation-attempted", StringComparer.OrdinalIgnoreCase) &&
            AppSettings.Load().RequestAdministratorOnLaunch && !ElevationService.IsElevated())
        {
            try
            {
                if (ElevationService.TryRestartAsAdministrator(e.Args.Contains("--page-users", StringComparer.OrdinalIgnoreCase)))
                {
                    Shutdown();
                    return;
                }
            }
            catch (Exception exception) { elevationError = exception.Message; }
        }
        SetCurrentProcessExplicitAppUserModelID("com.powerleitstandpro.desktop.wpf");
        _mainWindow = new MainWindow();
        MainWindow = _mainWindow;
        _mainWindow.StateChanged += (_, _) =>
        {
            if (_mainWindow?.WindowState == WindowState.Minimized)
            {
                _mainWindow.Dispatcher.BeginInvoke(() =>
                {
                    if (_mainWindow?.WindowState != WindowState.Minimized) return;
                    _mainWindow.ShowInTaskbar = false;
                    _mainWindow.Hide();
                    UpdateTrayWindowItem();
                });
            }
            UpdateTrayWindowItem();
        };
        _mainWindow.IsVisibleChanged += (_, _) => UpdateTrayWindowItem();
        CreateTrayIcon();
        _ = _mainWindow.CheckForUpdatesOnStartupAsync();
        if (e.Args.Contains("--page-users", StringComparer.OrdinalIgnoreCase))
            _mainWindow.ShowUsersPage();
        if (trayStart) _mainWindow.ShowInTaskbar = false;
        else _mainWindow.Show();
        if (elevationError is not null)
            ThemedMessageBox.Show(_mainWindow, elevationError, "PowerLeitstand PRO",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        UpdateTrayWindowItem();
    }

    private void CreateTrayIcon()
    {
        _trayMenu = new Forms.ContextMenuStrip
        {
            ShowImageMargin = true,
            Font = new System.Drawing.Font("Segoe UI", 9.5F),
            ImageScalingSize = new System.Drawing.Size(18, 18),
            Padding = new Forms.Padding(7)
        };
        _openItem = CreateMenuItem((_, _) => ToggleWindow());
        _centerItem = CreateMenuItem((_, _) => { RestoreWindow(); _mainWindow?.CenterWindowOnCursorScreen(); });
        _quickItem = new Forms.ToolStripLabel
        {
            Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold),
            Padding = new Forms.Padding(11, 3, 11, 1),
            Margin = new Forms.Padding(2, 2, 2, 0)
        };
        _shutdownItem = CreateMenuItem((_, _) => _mainWindow?.ExecuteTrayAction("shutdown"));
        _restartItem = CreateMenuItem((_, _) => _mainWindow?.ExecuteTrayAction("restart"));
        _lockItem = CreateMenuItem((_, _) => _mainWindow?.ExecuteTrayAction("lock"));
        _exitItem = CreateMenuItem((_, _) => { if (_mainWindow?.IsBusy != true) _mainWindow?.Close(); });
        _exitItem.Tag = "danger";
        _trayMenu.Items.AddRange(new Forms.ToolStripItem[]
        {
            _openItem, new Forms.ToolStripSeparator(), _quickItem,
            _shutdownItem, _restartItem, _lockItem,
            new Forms.ToolStripSeparator(), _exitItem
        });
        _trayMenu.Items.Insert(1, _centerItem);
        _trayMenu.Opening += (_, _) => { UpdateTrayWindowItem(); UpdateTrayTimerState(_mainWindow?.IsBusy == true); };

        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app-icon.ico"));
        if (resource is not null)
        {
            using (resource.Stream)
            using (var icon = new System.Drawing.Icon(resource.Stream))
                _ownedIcon = (System.Drawing.Icon)icon.Clone();
        }
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _ownedIcon ?? System.Drawing.SystemIcons.Application,
            Text = "PowerLeitstand PRO",
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => RestoreWindow();
        _trayIcon.BalloonTipClicked += (_, _) =>
        {
            RestoreWindow();
            if (_updateBalloonActive)
                _mainWindow?.OpenUpdateNotice(this, new RoutedEventArgs());
            else
                _mainWindow?.ShowRepairPage();
            _updateBalloonActive = false;
        };
        UpdateTrayPresentation("Standard", false);
        UpdateTrayTimerState(_mainWindow?.IsBusy == true);
    }

    private static Forms.ToolStripMenuItem CreateMenuItem(EventHandler? click)
    {
        var item = new Forms.ToolStripMenuItem
        {
            Padding = new Forms.Padding(7, 6, 12, 6),
            Margin = new Forms.Padding(2, 2, 2, 2),
            TextAlign = ContentAlignment.MiddleLeft
        };
        if (click is not null) item.Click += click;
        return item;
    }

    internal void UpdateTrayTimerState(bool active)
    {
        if (_exitItem is not null) _exitItem.Enabled = !active;
        if (_shutdownItem is not null) _shutdownItem.Enabled = !active;
        if (_restartItem is not null) _restartItem.Enabled = !active;
        if (_lockItem is not null) _lockItem.Enabled = !active;
    }

    internal void ShowRepairNotification(bool success, string summary)
    {
        if (_trayIcon is null) return;
        _updateBalloonActive = false;
        _trayIcon.ShowBalloonTip(8000, "PowerLeitstand PRO", summary,
            success ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Error);
    }

    internal void ShowUpdateNotification(string version)
    {
        if (_trayIcon is null) return;
        _updateBalloonActive = true;
        _trayIcon.ShowBalloonTip(8000, "PowerLeitstand PRO",
            _english ? $"Version {version} is available. Click to view the update."
                     : $"Version {version} ist verfügbar. Zum Update hier klicken.", Forms.ToolTipIcon.Info);
    }

    internal void UpdateTrayPresentation(string theme, bool english)
    {
        if (_trayMenu is null) return;
        _english = english;
        UpdateTrayWindowItem();
        _centerItem!.Text = english ? "Center window" : "Fenster zentrieren";
        _quickItem!.Text = english ? "Quick actions" : "Schnellaktionen";
        _shutdownItem!.Text = english ? "Shut down" : "Herunterfahren";
        _restartItem!.Text = english ? "Restart" : "Neustart";
        _lockItem!.Text = english ? "Lock Windows" : "Windows sperren";
        _exitItem!.Text = english ? "Exit program" : "Programm beenden";

        var palette = ThemePalette.For(theme);
        var back = System.Drawing.ColorTranslator.FromHtml(palette.Colors[3]);
        var text = System.Drawing.ColorTranslator.FromHtml(palette.Colors[5]);
        var border = System.Drawing.ColorTranslator.FromHtml(palette.Colors[7]);
        var hover = System.Drawing.ColorTranslator.FromHtml(palette.Colors[10]);
        _trayMenu.BackColor = back;
        _trayMenu.ForeColor = text;
        var renderer = new TrayRenderer(new TrayColors(back, border, hover), back, text, border, hover);
        _trayMenu.Renderer = renderer;
        foreach (Forms.ToolStripItem item in _trayMenu.Items) { item.BackColor = back; item.ForeColor = text; }
        UpdateTrayImages(palette.IconSet);
    }

    private void UpdateTrayImages(string set)
    {
        if (_trayImageSet == set) return;
        var replacements = new[]
        {
            (_openItem, "bolt"), (_shutdownItem, "shutdown"), (_restartItem, "restart"),
            (_lockItem, "lock"), (_exitItem, "abort")
        };
        var images = new List<(Forms.ToolStripMenuItem Item, System.Drawing.Image Image)>();
        try
        {
            foreach (var (item, name) in replacements)
            {
                var resource = System.Windows.Application.GetResourceStream(
                    new Uri($"pack://application:,,,/Assets/icons/{set}/{name}.png"))
                    ?? throw new FileNotFoundException(name);
                using var stream = resource.Stream;
                using var source = new Bitmap(stream);
                images.Add((item!, new Bitmap(source, new System.Drawing.Size(18, 18))));
            }
            foreach (var (item, image) in images) item.Image = image;
            foreach (var oldImage in _trayImages) oldImage.Dispose();
            _trayImages.Clear();
            _trayImages.AddRange(images.Select(pair => pair.Image));
            _trayImageSet = set;
        }
        catch
        {
            foreach (var (_, image) in images) image.Dispose();
        }
    }

    private void RestoreWindow()
    {
        if (_mainWindow is null) return;
        _mainWindow.ShowInTaskbar = true;
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
        UpdateTrayWindowItem();
    }

    private void ToggleWindow()
    {
        if (_mainWindow is null) return;
        if (_mainWindow.IsVisible && _mainWindow.WindowState != WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Minimized;
        else RestoreWindow();
        UpdateTrayWindowItem();
    }

    private void UpdateTrayWindowItem()
    {
        if (_openItem is null) return;
        var open = _mainWindow is null || !_mainWindow.IsVisible || _mainWindow.WindowState == WindowState.Minimized;
        _openItem.Text = open ? (_english ? "Open window" : "Fenster öffnen")
            : (_english ? "Minimize window" : "Fenster minimieren");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_trayIcon is not null) { _trayIcon.Visible = false; _trayIcon.Dispose(); }
        _trayMenu?.Dispose();
        foreach (var image in _trayImages) image.Dispose();
        _ownedIcon?.Dispose();
        base.OnExit(e);
    }

    private sealed class TrayColors(System.Drawing.Color background, System.Drawing.Color border, System.Drawing.Color hover) : Forms.ProfessionalColorTable
    {
        public override System.Drawing.Color ToolStripDropDownBackground => background;
        public override System.Drawing.Color MenuBorder => border;
        public override System.Drawing.Color MenuItemBorder => border;
        public override System.Drawing.Color MenuItemSelected => hover;
        public override System.Drawing.Color MenuItemSelectedGradientBegin => hover;
        public override System.Drawing.Color MenuItemSelectedGradientEnd => hover;
        public override System.Drawing.Color ImageMarginGradientBegin => background;
        public override System.Drawing.Color ImageMarginGradientMiddle => background;
        public override System.Drawing.Color ImageMarginGradientEnd => background;
    }

    private sealed class TrayRenderer(TrayColors colors, Color background, Color foreground, Color border, Color hover)
        : Forms.ToolStripProfessionalRenderer(colors)
    {
        private static Color Blend(Color from, Color to, int percent) => Color.FromArgb(
            (from.R * (100 - percent) + to.R * percent) / 100,
            (from.G * (100 - percent) + to.G * percent) / 100,
            (from.B * (100 - percent) + to.B * percent) / 100);

        protected override void OnRenderImageMargin(Forms.ToolStripRenderEventArgs e)
        {
            using var fill = new SolidBrush(background);
            e.Graphics.FillRectangle(fill, e.AffectedBounds);
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            var bounds = new Rectangle(1, 1, e.Item.Width - 3, e.Item.Height - 3);
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = new GraphicsPath();
            var radius = 8;
            path.AddArc(bounds.Left, bounds.Top, radius, radius, 180, 90);
            path.AddArc(bounds.Right - radius, bounds.Top, radius, radius, 270, 90);
            path.AddArc(bounds.Right - radius, bounds.Bottom - radius, radius, radius, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - radius, radius, radius, 90, 90);
            path.CloseFigure();
            var danger = e.Item.Tag?.ToString() == "danger";
            var dangerColor = background.GetBrightness() > 0.55f
                ? Color.FromArgb(244, 181, 181)
                : Color.FromArgb(154, 45, 67);
            var target = danger ? dangerColor : hover;
            var idle = Blend(background, target, danger ? 20 : 12);
            var fillColor = !e.Item.Enabled ? Blend(background, foreground, 6)
                : e.Item.Selected ? target : idle;
            using var fill = new SolidBrush(fillColor);
            e.Graphics.FillPath(fill, path);
            if (e.Item.Selected && e.Item.Enabled)
            {
                using var outline = new Pen(danger ? dangerColor : border);
                e.Graphics.DrawPath(outline, path);
            }
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            var y = e.Item.Height / 2;
            using var line = new Pen(Color.FromArgb(75, border));
            e.Graphics.DrawLine(line, 10, y, e.Item.Width - 10, y);
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item is Forms.ToolStripLabel ? Blend(foreground, border, 30)
                : e.Item.Enabled ? foreground : Blend(background, foreground, 50);
            base.OnRenderItemText(e);
        }
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);
}
