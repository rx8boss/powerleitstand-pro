using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using ColorConverter = System.Windows.Media.ColorConverter;
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;

namespace PowerControlProWpf;

internal sealed record TraySnapshot(bool Busy, bool TimerActive, bool RepairActive, string Remaining, string Action);

public partial class TrayQuickWindow : Window
{
    private readonly Func<TraySnapshot> _state;
    private readonly Action<string> _execute;
    private readonly bool _english;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _closingOrClosed;
    private bool _dismissQueued;
    private bool _actionQueued;

    internal TrayQuickWindow(string theme, bool english, Func<TraySnapshot> state, Action<string> execute)
    {
        InitializeComponent();
        CrossButtonAppearance.PreserveCustomSurface(ExitButton);
        _state = state; _execute = execute; _english = english;
        var palette = ThemePalette.For(theme);
        Brush Solid(string value) => new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(value));
        Resources["TrayText"] = Solid(palette.Colors[5]);
        Resources["TraySubtle"] = Solid(palette.Colors[6]);
        Resources["TrayAccent"] = Solid(palette.Colors[7]);
        Resources["TrayLine"] = Solid(palette.Colors[8]);
        Resources["TraySurface"] = new LinearGradientBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(palette.Colors[4]), (System.Windows.Media.Color)ColorConverter.ConvertFromString(palette.Colors[3]), 35);
        Resources["TrayButton"] = new LinearGradientBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(palette.Colors[9]), (System.Windows.Media.Color)ColorConverter.ConvertFromString(palette.Colors[3]), 90);
        Resources["TrayHover"] = Solid(palette.Colors[10]);
        Resources["TrayInset"] = Solid(palette.Colors[4]);
        Resources["TrayDangerBackground"] = Solid(palette.Actions[0]);
        Foreground = Solid(palette.Colors[5]); FontFamily = new FontFamily(palette.FontFamily);
        StateDot.Fill = Solid(palette.Ready);
        foreach (var (image, name) in new[] { (ShutdownIcon, "shutdown"), (RestartIcon, "restart"), (LogoutIcon, "logout"), (LockIcon, "lock") })
            image.Source = new BitmapImage(new Uri($"pack://application:,,,/Assets/icons/{palette.IconSet}/{name}.png"));
        if (palette.IsNeon) MenuFrame.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = (System.Windows.Media.Color)ColorConverter.ConvertFromString(palette.Glow!), BlurRadius = 9, ShadowDepth = 0, Opacity = 0.4 };
        Subtitle.Text = english ? "Quick access" : "Schnellzugriff";
        ActionsHeading.Text = english ? "System actions" : "Systemaktionen";
        TimerHeading.Text = english ? "Timer running" : "Timer läuft";
        OpenCaption.Text = english ? "Open window" : "Fenster öffnen";
        CancelTimerCaption.Text = english ? "Cancel timer" : "Timer abbrechen";
        ShutdownCaption.Text = english ? "Shut down" : "Herunterfahren";
        RestartCaption.Text = english ? "Restart" : "Neustart";
        LogoutCaption.Text = english ? "Sign out" : "Abmelden";
        LockCaption.Text = english ? "Lock" : "Sperren";
        ExitCaption.Text = english ? "Exit program" : "Programm beenden";
        CenterButton.ToolTip = english ? "Center window" : "Fenster zentrieren";
        ExitButton.MouseEnter += (_, _) => ExitCross.Foreground = System.Windows.Media.Brushes.White;
        ExitButton.MouseLeave += (_, _) => ExitCross.Foreground = Solid("#FF6076");
        _refresh.Tick += (_, _) => RefreshState();
        Loaded += (_, _) => { RefreshState(); _refresh.Start(); };
        Closed += (_, _) => _refresh.Stop();
        Deactivated += (_, _) =>
        {
            // Focus can be lost inside Window.Close's native message processing.
            // Defer dismissal and never re-enter Close while that transition is running.
            if (_closingOrClosed || _dismissQueued) return;
            _dismissQueued = true;
            Dispatcher.BeginInvoke(() =>
            {
                _dismissQueued = false;
                if (!IsActive) Dismiss();
            }, DispatcherPriority.Background);
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Dismiss(); e.Handled = true; } };
        RefreshState();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _closingOrClosed = true;
        _refresh.Stop();
        base.OnClosing(e);
        if (e.Cancel) { _closingOrClosed = false; _refresh.Start(); }
    }

    internal void Dismiss()
    {
        if (!_closingOrClosed) Close();
    }

    internal void RefreshState()
    {
        var state = _state();
        ShutdownButton.IsEnabled = RestartButton.IsEnabled = LogoutButton.IsEnabled = LockButton.IsEnabled = ExitButton.IsEnabled = !state.Busy;
        CancelTimerButton.IsEnabled = state.TimerActive;
        TimerCard.Visibility = state.TimerActive ? Visibility.Visible : Visibility.Collapsed;
        RemainingText.Text = state.Remaining; ActionText.Text = state.Action;
        StateText.Text = state.TimerActive ? (_english ? "Timer active" : "Timer aktiv")
            : state.RepairActive ? (_english ? "Repair running" : "Reparatur läuft")
            : state.Busy ? (_english ? "Update running" : "Update läuft") : (_english ? "Program ready" : "Programm bereit");
        GuardHint.Text = state.TimerActive ? (_english ? "Please cancel the timer first." : "Bitte zuerst den Timer abbrechen.")
            : state.Busy ? (_english ? "Please finish the active operation first." : "Bitte zuerst den laufenden Vorgang beenden.")
            : (_english ? "System actions require confirmation." : "Systemaktionen mit Bestätigung.");
        foreach (var button in new[] { ShutdownButton, RestartButton, LogoutButton, LockButton, ExitButton })
        { button.ToolTip = GuardHint.Text; System.Windows.Controls.ToolTipService.SetShowOnDisabled(button, true); }
    }

    internal void ShowAtCursor()
    {
        var area = WindowPlacementService.GetCursorWorkAreaInDips(this);
        MenuScroll.MaxHeight = Math.Max(120, area.Height - 48);
        Width = Math.Min(384, area.Width - 16);
        Show(); UpdateLayout();
        // Keep the popup inside the work area even with a top/left taskbar or a small monitor.
        var cursor = System.Windows.Forms.Cursor.Position;
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = Math.Clamp(cursor.X / dpi.DpiScaleX - ActualWidth, area.Left + 4, Math.Max(area.Left + 4, area.Right - ActualWidth - 4));
        Top = Math.Clamp(cursor.Y / dpi.DpiScaleY - ActualHeight - 8, area.Top + 4, Math.Max(area.Top + 4, area.Bottom - ActualHeight - 4));
        Activate();
    }

    private void Invoke(string action, bool requireIdle = false)
    {
        if (_closingOrClosed || _actionQueued) return;
        // Re-read immediately before dispatch so stale visual state cannot bypass the guard.
        if (requireIdle && _state().Busy) { RefreshState(); return; }
        _actionQueued = true;
        Dismiss();
        // Restore/center/confirm only after the popup has left its native input/close stack.
        Dispatcher.BeginInvoke(() =>
        {
            if (requireIdle && _state().Busy) return;
            _execute(action);
        }, DispatcherPriority.Background);
    }
    private void OpenWindow(object s, RoutedEventArgs e) => Invoke("open");
    private void CenterWindow(object s, RoutedEventArgs e) => Invoke("center");
    private void CancelTimer(object s, RoutedEventArgs e) { if (_state().TimerActive) Invoke("cancel"); }
    private void ExitProgram(object s, RoutedEventArgs e) => Invoke("exit", true);
    private void SystemAction(object s, RoutedEventArgs e) => Invoke((string)((Button)s).Tag, true);
}
