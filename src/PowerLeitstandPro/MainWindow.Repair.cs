using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using MessageBox = PowerControlProWpf.ThemedMessageBox;

namespace PowerControlProWpf;

public partial class MainWindow
{
    private readonly StringBuilder _repairOutput = new();
    private int? _lastRepairExitCode;
    private bool _lastRepairCancelled;
    private readonly Stopwatch _repairWatch = new();
    private readonly RepairTimeEstimator _repairTimeEstimator = new();
    private readonly DispatcherTimer _repairClock = new() { Interval = TimeSpan.FromSeconds(1) };
    private RepairResultSummary? _pendingRepairSummary;
    private RepairResultSummary? _lastRepairSummary;
    private bool _repairResultDialogOpen;

    private void LoadPersistedRepairLog()
    {
        try
        {
            if (!File.Exists(RepairLogFilePath)) return;
            foreach (var line in File.ReadLines(RepairLogFilePath)
                         .Where(line => !string.IsNullOrWhiteSpace(line) &&
                                        !RepairProgressParser.TryReadPercent(line, out _)).TakeLast(500).Reverse())
            {
                var entry = RepairLogEntry.Parse(line);
                RepairLogBox.Items.Add(entry);
                _repairLogEntries.Add(ToCompactRepairLogEntry(entry));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void AppendRepairLog(string line, string level = "INFO")
    {
        var entry = new RepairLogEntry(DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"),
            _repairCommand?.StartsWith("DISM", StringComparison.Ordinal) == true ? "DISM" :
            _repairCommand?.StartsWith("SFC", StringComparison.Ordinal) == true ? "SFC" : "SYSTEM",
            level, line.Replace('\r', ' ').Replace('\n', ' '));
        AddRepairLogEntry(entry);
        try
        {
            Directory.CreateDirectory(_logDirectory);
            File.AppendAllText(RepairLogFilePath, entry.ToLine() + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static CompactLogEntry ToCompactRepairLogEntry(RepairLogEntry entry) =>
        new(entry.Timestamp.Length >= 16 ? entry.Timestamp.Substring(11, 5) : entry.Timestamp, entry.Message)
        {
            Detail = entry.ToLine(),
            FullTime = entry.Timestamp.Length >= 19 ? entry.Timestamp.Substring(11, 8) : entry.Timestamp,
            Category = $"{entry.Phase} · {entry.Level}"
        };

    private void AddRepairLogEntry(RepairLogEntry entry)
    {
        RepairLogBox.Items.Insert(0, entry);
        _repairLogEntries.Insert(0, ToCompactRepairLogEntry(entry));
        while (RepairLogBox.Items.Count > 500) RepairLogBox.Items.RemoveAt(RepairLogBox.Items.Count - 1);
        while (_repairLogEntries.Count > 500) _repairLogEntries.RemoveAt(_repairLogEntries.Count - 1);
    }

    private void StartDismRepair(object sender, RoutedEventArgs e) => _ = StartRepairAsync("dism");
    private void StartSfcRepair(object sender, RoutedEventArgs e) => _ = StartRepairAsync("sfc");

    private async Task StartRepairAsync(string mode)
    {
        if (IsBusy) return;
        var command = mode == "dism" ? "DISM /Online /Cleanup-Image /RestoreHealth" : "SFC /scannow";
        var confirmation = new RepairConfirmWindow(command, ThemePalette.For(SelectedTheme()), _english)
        {
            Owner = this
        };
        if (confirmation.ShowDialog() != true) return;

        _repairCancellation = new CancellationTokenSource();
        _repairRunning = true;
        UpdateCompactLogActionState();
        _repairPercent = 0;
        _repairCommand = command;
        _repairOutput.Clear();
        _lastRepairExitCode = null;
        _lastRepairCancelled = false;
        _lastRepairSummary = null;
        _pendingRepairSummary = null;
        ShowRepairProtocolButton.Visibility = Visibility.Collapsed;
        ClearRepairLogButton.IsEnabled = false;
        _repairTimeEstimator.Reset();
        _repairWatch.Restart();
        _repairClock.Tick -= RepairClockTick;
        _repairClock.Tick += RepairClockTick;
        _repairClock.Start();
        UpdateRepairTime();
        RepairProgress.Value = 0;
        UpdateRepairProgressAnimation();
        RepairProgressLabel.Text = _english ? $"{command} is starting…" : $"{command} wird gestartet…";
        RepairResult.Text = "";
        RepairCancelButton.IsEnabled = false; // Erst nach Verbindung mit dem Administrator-Hilfsprozess.
        SetTimerState(_timer.IsEnabled);
        AppendRepairLog((_english ? "Started: " : "Gestartet: ") + command);
        Log((_english ? "Repair started: " : "Reparatur gestartet: ") + command);

        int? exitCode = null;
        string? failure = null;
        var cancelled = false;
        try
        {
            await RepairEngine.RunElevatedAsync(mode, message =>
            {
                switch (message.Kind)
                {
                    case "start":
                        RepairCancelButton.IsEnabled = true;
                        AppendRepairLog(message.Text);
                        break;
                    case "output":
                        if (RepairProgressParser.TryReadPercent(message.Text, out var percent))
                        {
                            if (percent > _repairPercent) _repairTimeEstimator.Add(_repairWatch.Elapsed, percent);
                            _repairPercent = Math.Max(_repairPercent, percent);
                            RepairProgress.Value = _repairPercent;
                            UpdateRepairProgressAnimation();
                            RepairProgressLabel.Text = $"{command}: {_repairPercent:0.#} %";
                            UpdateRepairTime();
                            UpdateRepairStatus();
                        }
                        else if (!string.IsNullOrWhiteSpace(message.Text))
                        {
                            AppendRepairLog(message.Text);
                            if (_repairOutput.Length < 40000) _repairOutput.AppendLine(message.Text);
                        }
                        break;
                    case "details":
                        if (!string.IsNullOrWhiteSpace(message.Text))
                        {
                            AppendRepairLog(message.Text);
                            if (_repairOutput.Length < 40000) _repairOutput.AppendLine(message.Text);
                        }
                        break;
                    case "error":
                        failure = message.Text;
                        AppendRepairLog(message.Text, "FEHLER");
                        break;
                    case "exit":
                        exitCode = message.Code;
                        AppendRepairLog(message.Text);
                        break;
                    case "cancelled":
                        cancelled = true;
                        AppendRepairLog(_english ? "Repair cancelled by user." : "Reparatur vom Benutzer abgebrochen.", "ABBRUCH");
                        break;
                }
            }, _repairCancellation.Token);

            if (cancelled)
            {
                _lastRepairCancelled = true;
                RepairProgressLabel.Text = _english ? "Repair cancelled." : "Reparatur abgebrochen.";
                RepairResult.Text = _english ? "No completion notification is sent for a manual cancellation."
                    : "Bei manuellem Abbruch wird keine Abschluss-Benachrichtigung gesendet.";
                Log(RepairProgressLabel.Text);
            }
            else
            {
                _lastRepairExitCode = exitCode ?? -1;
                _lastRepairSummary = RepairResultSummary.Create(mode, _lastRepairExitCode, false,
                    _repairOutput.ToString(), GetRepairFailureHint(failure), _repairWatch.Elapsed, _english);
                var success = _lastRepairSummary.IsSuccess;
                if (exitCode == 0) { _repairPercent = 100; RepairProgress.Value = 100; }
                RepairProgressLabel.Text = _lastRepairSummary.Title;
                RepairResult.Text = _lastRepairSummary.Message;
                AppendRepairLog(RepairProgressLabel.Text, success ? "ERFOLG" : "FEHLER");
                if (!success) AppendRepairLog(RepairResult.Text, "HINWEIS");
                Log(RepairProgressLabel.Text);
                NotifyRepairCompletion(success, RepairProgressLabel.Text);
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            _lastRepairCancelled = true;
            RepairProgressLabel.Text = _english ? "Administrator request cancelled." : "Administratorabfrage abgebrochen.";
            AppendRepairLog(RepairProgressLabel.Text, "ABBRUCH");
        }
        catch (OperationCanceledException) when (_repairCancellation?.IsCancellationRequested == true)
        {
            _lastRepairCancelled = true;
            RepairProgressLabel.Text = _english ? "Repair cancelled." : "Reparatur abgebrochen.";
            AppendRepairLog(RepairProgressLabel.Text, "ABBRUCH");
        }
        catch (OperationCanceledException)
        {
            _lastRepairExitCode = -1;
            RepairProgressLabel.Text = _english ? "Administrator helper did not respond." : "Administrator-Hilfsprozess antwortet nicht.";
            AppendRepairLog(RepairProgressLabel.Text, "FEHLER");
            NotifyRepairCompletion(false, RepairProgressLabel.Text);
        }
        catch (Exception ex)
        {
            _lastRepairExitCode = -1;
            RepairProgressLabel.Text = _english ? "Repair failed or the connection was lost." : "Reparatur fehlgeschlagen oder Verbindung unterbrochen.";
            RepairResult.Text = ex.Message;
            AppendRepairLog($"{RepairProgressLabel.Text} {ex.Message}", "FEHLER");
            Log(RepairProgressLabel.Text);
            if (_repairCancellation?.IsCancellationRequested != true)
                NotifyRepairCompletion(false, RepairProgressLabel.Text);
        }
        finally
        {
            _repairClock.Stop();
            _repairWatch.Stop();
            RepairCancelButton.IsEnabled = false;
            _repairRunning = false;
            UpdateRepairProgressAnimation();
            UpdateCompactLogActionState();
            UpdateRepairTime();
            ClearRepairLogButton.IsEnabled = true;
            _repairCancellation?.Dispose();
            _repairCancellation = null;
            _lastRepairSummary ??= RepairResultSummary.Create(mode, _lastRepairExitCode,
                _lastRepairCancelled, _repairOutput.ToString(), RepairResult.Text, _repairWatch.Elapsed, _english);
            SetTimerState(_timer.IsEnabled);
            UpdateRepairStatus();
            _pendingRepairSummary = _lastRepairSummary;
            ShowPendingRepairSummary();
        }
    }

    private void RepairClockTick(object? sender, EventArgs e) => UpdateRepairTime();

    private void UpdateRepairTime()
    {
        var elapsed = _repairWatch.Elapsed;
        var clock = elapsed.ToString(elapsed.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss");
        var prefix = (_english ? "Elapsed " : "Laufzeit ") + clock;
        if (_repairRunning && _repairCommand?.StartsWith("SFC", StringComparison.Ordinal) == true)
        {
            RepairTimeText.Text = prefix;
            return;
        }
        var estimate = _repairRunning ? _repairTimeEstimator.Estimate(elapsed) : null;
        var remaining = !_repairRunning && _lastRepairCancelled ? (_english ? "cancelled" : "abgebrochen")
            : !_repairRunning && _lastRepairSummary is { IsSuccess: false } ? (_english ? "failed" : "fehlgeschlagen")
            : !_repairRunning && _lastRepairExitCode == 0 ? (_english ? "completed" : "abgeschlossen")
            : !_repairRunning && _lastRepairExitCode is not null ? (_english ? "failed" : "fehlgeschlagen")
            : estimate is null ? (_english ? "remaining time not yet available" : "Restzeit noch nicht schätzbar")
            : (_english ? $"about {Math.Ceiling(estimate.Value.TotalMinutes)} min remaining" : $"Restzeit ca. {Math.Ceiling(estimate.Value.TotalMinutes)} Min.");
        RepairTimeText.Text = prefix + " · " + remaining;
    }

    private void ShowPendingRepairSummary()
    {
        if (_pendingRepairSummary is null || _repairResultDialogOpen || WindowState == WindowState.Minimized || !IsVisible)
            return;
        var summary = _pendingRepairSummary;
        _pendingRepairSummary = null;
        ShowRepairSummaryDialog(summary);
    }

    private void ShowRepairSummaryDialog(RepairResultSummary summary)
    {
        if (_repairResultDialogOpen) return;
        _repairResultDialogOpen = true;
        try
        {
            var dialog = new RepairResultWindow(summary, ThemePalette.For(SelectedTheme()), _english) { Owner = this };
            if (dialog.ShowDialog() == true && summary.CanRestart)
                ConfirmAndExecute(PowerAction.Restart);
        }
        finally
        {
            _repairResultDialogOpen = false;
            ShowRepairProtocolButton.Visibility = Visibility.Visible;
        }
    }

    private void ClearRepairLog(object sender, RoutedEventArgs e)
    {
        if (_repairRunning) return;
        var question = _english ? "Delete the repair log permanently?" : "Reparaturprotokoll dauerhaft löschen?";
        if (MessageBox.Show(this, question, "PowerLeitstand PRO", MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            if (File.Exists(RepairLogFilePath)) File.Delete(RepairLogFilePath);
            RepairLogBox.Items.Clear();
            _repairLogEntries.Clear();
            Log(_english ? "Repair log deleted." : "Reparaturprotokoll gelöscht.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "PowerLeitstand PRO", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string GetRepairFailureHint(string? failure)
    {
        var output = _repairOutput.ToString();
        if (output.Contains("0x800f081f", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("0x800f0906", StringComparison.OrdinalIgnoreCase))
            return _english ? "Source files are missing. Check Windows Update or use a matching installation source."
                : "Quelldateien fehlen. Windows Update prüfen oder eine passende Installationsquelle verwenden.";
        if (output.Contains("nicht reparieren", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("unable to fix", StringComparison.OrdinalIgnoreCase))
            return _english ? "SFC could not fix all files. Check CBS.log and run DISM before retrying SFC."
                : "SFC konnte nicht alle Dateien reparieren. CBS.log prüfen und vor einem erneuten SFC-Lauf DISM ausführen.";
        return !string.IsNullOrWhiteSpace(failure) ? failure
            : (_english ? "See the repair log for details." : "Details stehen im Reparaturprotokoll.");
    }

    private void CancelRepair(object sender, RoutedEventArgs e)
    {
        if (!_repairRunning || _repairCancellation is null || _repairCancellation.IsCancellationRequested) return;
        var question = _english
            ? "Stop this running Windows repair? Interrupting a repair may leave the operation incomplete."
            : "Laufende Windows-Reparatur abbrechen? Ein Abbruch kann den Reparaturvorgang unvollständig lassen.";
        if (MessageBox.Show(this, question, "PowerLeitstand PRO", MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        RepairCancelButton.IsEnabled = false;
        RepairProgressLabel.Text = _english ? "Stopping repair…" : "Reparatur wird abgebrochen…";
        AppendRepairLog(RepairProgressLabel.Text);
        _repairCancellation.Cancel();
    }

    private void OpenRepairLog(object sender, RoutedEventArgs e)
    {
        OpenLogFolder(RepairLogFilePath);
    }

    private void ShowRepairProtocol(object sender, RoutedEventArgs e)
    {
        if (_repairRunning || _lastRepairSummary is null) return;
        ShowRepairSummaryDialog(_lastRepairSummary);
    }

    private void NotifyRepairCompletion(bool success, string summary)
    {
        if (WindowState != WindowState.Minimized) return;
        (System.Windows.Application.Current as App)?.ShowRepairNotification(success, summary);
    }

    private void UpdateRepairStatus()
    {
        if (_timer.IsEnabled) return;
        string title, detail, symbol, brush;
        if (_repairRunning)
        {
            title = _repairCommand?.StartsWith("SFC", StringComparison.Ordinal) == true ? "SFC" : "DISM";
            title += _english ? " running" : " läuft";
            detail = _repairPercent >= 100 ? (_english ? "100 % · checking result…" : "100 % · Ergebnis wird geprüft…")
                : _repairPercent > 0 ? (_english ? $"{_repairPercent:0.#} % completed" : $"{_repairPercent:0.#} % abgeschlossen")
                : (_english ? "Waiting for progress…" : "Warte auf Fortschritt…");
            symbol = "◷";
            brush = "WarningBrush";
        }
        else if (_lastRepairCancelled)
        {
            title = _english ? "Cancelled" : "Abgebrochen";
            detail = _english ? "Repair stopped by user" : "Reparatur vom Benutzer beendet";
            symbol = "×";
            brush = "WarningBrush";
        }
        else if (_lastRepairSummary is { } summary)
        {
            title = summary.IsSuccess ? (_english ? "Completed" : "Abgeschlossen")
                : (_english ? "Repair error" : "Reparaturfehler");
            detail = summary.IsSuccess ? (_english ? "Check the repair log" : "Reparaturprotokoll prüfen")
                : _lastRepairExitCode == 0 ? (_english ? "Not all files were repaired" : "Nicht alle Dateien repariert")
                : (_english ? $"Error code {_lastRepairExitCode}" : $"Fehlercode {_lastRepairExitCode}");
            symbol = summary.IsSuccess ? "✓" : "!";
            brush = summary.IsSuccess ? "ReadyBrush" : "DangerBorderBrush";
        }
        else return;

        foreach (var (text, sub, badge, icon) in new[]
                 {
                     (StatusText, StatusDetail, StatusBadge, StatusSymbol),
                     (UserStatusText, UserStatusSub, UserStatusBadge, UserStatusSymbol),
                     (RepairStatusText, RepairStatusSub, RepairStatusBadge, RepairStatusSymbol)
                 })
        {
            text.Text = title;
            sub.Text = detail;
            icon.Text = symbol;
            badge.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, brush);
            text.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, brush);
        }
    }
}
