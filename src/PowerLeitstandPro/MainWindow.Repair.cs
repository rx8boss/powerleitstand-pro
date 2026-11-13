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
    private string _repairErrorSession = Guid.NewGuid().ToString("N");
    private readonly RepairTimeEstimator _repairTimeEstimator = new();
    private readonly DispatcherTimer _repairClock = new() { Interval = TimeSpan.FromSeconds(1) };
    private RepairResultSummary? _pendingRepairSummary;
    private RepairResultSummary? _lastRepairSummary;
    private bool _repairResultDialogOpen;
    private RepairNetworkRequest? _selectedNetworkRequest;

    private void LoadPersistedRepairLog()
    {
        try
        {
            if (!File.Exists(RepairLogFilePath)) return;
            foreach (var line in File.ReadLines(RepairLogFilePath)
                         .Where(line => !string.IsNullOrWhiteSpace(line) &&
                                        !RepairProgressParser.TryReadPercent(line, out _)).TakeLast(500).Reverse())
            {
                var entry = RepairLogEntry.Parse(line).WithLanguage(_english);
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
            _selectedRepairTool?.Executable switch
            {
                "dism.exe" => "DISM", "sfc.exe" => "SFC", "chkdsk.exe" => "CHKDSK",
                "ipconfig.exe" => "DNS", "pnputil.exe" => "PnPUtil",
                "powershell.exe" when _selectedRepairTool?.ShowsDiagnosticOutput == true => _selectedRepairTool.Caption,
                "powershell.exe" => "Windows Update", _ => "SYSTEM"
            },
            level, line.Replace('\r', ' ').Replace('\n', ' '));
        AddRepairLogEntry(entry);
        if (level == "FEHLER") RecordError(new(entry.Phase, "Windows-Reparatur", line, OperationId: _repairErrorSession));
        try
        {
            Directory.CreateDirectory(_logDirectory);
            File.AppendAllText(RepairLogFilePath, entry.ToLine() + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static CompactLogEntry ToCompactRepairLogEntry(RepairLogEntry entry) =>
        new(entry.Timestamp.Length >= 16 ? entry.Timestamp.Substring(11, 5) : entry.Timestamp, entry.DisplayMessage)
        {
            Detail = entry.ToLine(),
            FullTime = entry.Timestamp.Length >= 19 ? entry.Timestamp.Substring(11, 8) : entry.Timestamp,
            Category = $"{entry.Phase} · {entry.DisplayLevel}"
        };

    private void AddRepairLogEntry(RepairLogEntry entry)
    {
        entry = entry.WithLanguage(_english);
        RepairLogBox.Items.Insert(0, entry);
        _repairLogEntries.Insert(0, ToCompactRepairLogEntry(entry));
        while (RepairLogBox.Items.Count > 500) RepairLogBox.Items.RemoveAt(RepairLogBox.Items.Count - 1);
        while (_repairLogEntries.Count > 500) _repairLogEntries.RemoveAt(_repairLogEntries.Count - 1);
    }

    private async Task StartRepairAsync(RepairTool tool, RepairNetworkAdapter? adapter = null, RepairNetworkRequest? request = null)
    {
        if (IsBusy) return;
        string? networkConfiguration = null;
        if (request is null)
        {
            var confirmation = new RepairConfirmWindow(tool, ThemePalette.For(SelectedTheme()), _english,
                reports: DiagnosticReportsFor(tool), networkAdapters: adapter is null ? null : [adapter], selectAdapter: adapter is not null) { Owner = this };
            if (confirmation.ShowDialog() != true) return;
            request = confirmation.NetworkRequest;
            networkConfiguration = confirmation.NetworkConfiguration;
        }
        if (IsBusy || !IsAdmin()) return;

        if (tool.OpensWindow)
        {
            var launch = Stopwatch.StartNew();
            try
            {
                var executable = Path.Combine(Environment.SystemDirectory, tool.Executable);
                var startInfo = new ProcessStartInfo(executable, tool.Arguments)
                {
                    WorkingDirectory = Environment.SystemDirectory,
                    UseShellExecute = false
                };
                using var process = Process.Start(startInfo);
                SaveDiagnosticReport(tool, DiagnosticReportStatus.Opened, launch.Elapsed,
                    _english ? "Windows displays the results in its own window." : "Windows zeigt die Ergebnisse im eigenen Fenster.", "");
            }
            catch (Win32Exception ex)
            {
                var message = UiText.Error(ex, _english);
                SaveDiagnosticReport(tool, DiagnosticReportStatus.Failed, launch.Elapsed, message, "");
                MessageBox.Show(this, message, tool.Title(_english), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return;
        }

        var mode = tool.Id;
        var command = tool.Caption;
        _selectedRepairTool = tool;
        _selectedNetworkRequest = request;
        UpdateRepairRunLabels();
        RepairLiveOutput.Clear();
        RepairToolScroll.Visibility = Visibility.Collapsed;
        RepairRunningPanel.Visibility = Visibility.Visible;
        _repairCancellation = new CancellationTokenSource();
        _repairRunning = true;
        _repairErrorSession = Guid.NewGuid().ToString("N");
        UpdateCompactLogActionState();
        _repairPercent = 0;
        _repairHasMeasuredProgress = false;
        _repairCommand = command;
        _repairOutput.Clear();
        if (networkConfiguration is not null)
        {
            _repairOutput.AppendLine(_english ? "IP and DNS configuration before the reset" : "IP- und DNS-Konfiguration vor dem Reset");
            _repairOutput.AppendLine(networkConfiguration);
            _repairOutput.AppendLine();
        }
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

        int? exitCode = null;
        string? failure = null;
        var cancelled = false;
        var driverInventory = mode == "driver-packages";
        var outputLimit = driverInventory ? RepairDriverQuery.OutputLimit : 40_000;
        var outputTruncated = false;
        void AppendResultOutput(string line)
        {
            if (_repairOutput.Length < outputLimit)
                _repairOutput.AppendLine(line);
            else if (driverInventory && !outputTruncated)
            {
                _repairOutput.AppendLine(_english ? "Output limit reached. Further packages are omitted."
                    : "Ausgabegrenze erreicht. Weitere Pakete werden ausgelassen.");
                outputTruncated = true;
            }
        }

        try
        {
            await RepairEngine.RunElevatedAsync(mode, message =>
            {
                switch (message.Kind)
                {
                    case "start":
                        if (tool.ReportFolder is null) Log((_english ? "Repair started: " : "Reparatur gestartet: ") + command);
                        RepairCancelButton.IsEnabled = !tool.IsNetworkAction && _repairCancellation?.IsCancellationRequested != true;
                        AppendRepairLog(message.Text);
                        if (_repairCancellation?.IsCancellationRequested != true)
                            RepairProgressLabel.Text = _english ? "Other repair actions are locked during this operation."
                                : "Weitere Reparaturaktionen sind während des Vorgangs gesperrt.";
                        break;
                    case "output":
                        if (!tool.ShowsDiagnosticOutput && RepairProgressParser.TryReadPercent(message.Text, out var percent))
                        {
                            if (!_repairHasMeasuredProgress || percent > _repairPercent) AppendRepairLiveOutput(message.Text);
                            _repairHasMeasuredProgress = true;
                            if (percent > _repairPercent) _repairTimeEstimator.Add(_repairWatch.Elapsed, percent);
                            _repairPercent = Math.Max(_repairPercent, percent);
                            RepairProgress.Value = _repairPercent;
                            UpdateRepairProgressAnimation();

                            UpdateRepairTime();
                            UpdateRepairStatus();
                        }
                        else if (!string.IsNullOrWhiteSpace(message.Text))
                        {
                            AppendRepairLiveOutput(message.Text);
                            if (tool.ReportFolder is null) AppendRepairLog(message.Text);
                            AppendResultOutput(message.Text);
                        }
                        break;
                    case "details":
                        if (!string.IsNullOrWhiteSpace(message.Text))
                        {
                            AppendRepairLiveOutput(message.Text);
                            if (tool.ReportFolder is null) AppendRepairLog(message.Text);
                            AppendResultOutput(message.Text);
                        }
                        break;
                    case "error":
                        failure = message.Text;
                        AppendRepairLiveOutput(message.Text);
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
            }, _repairCancellation.Token, _english, _selectedNetworkRequest);

            if (cancelled)
            {
                _lastRepairCancelled = true;
                RepairProgressLabel.Text = _english ? "Repair cancelled." : "Reparatur abgebrochen.";
                RepairResult.Text = _english ? "No completion notification is sent for a manual cancellation."
                    : "Bei manuellem Abbruch wird keine Abschluss-Benachrichtigung gesendet.";
                if (tool.ReportFolder is null) Log(RepairProgressLabel.Text);
            }
            else
            {
                _lastRepairExitCode = exitCode ?? -1;
                _lastRepairSummary = RepairResultSummary.Create(mode, _lastRepairExitCode, false,
                    _repairOutput.ToString(), GetRepairFailureHint(failure), _repairWatch.Elapsed, _english);
                var success = _lastRepairSummary.IsSuccess;
                if (exitCode == 0) { _repairHasMeasuredProgress = true; _repairPercent = 100; RepairProgress.Value = 100; }
                RepairProgressLabel.Text = _lastRepairSummary.Title;
                RepairResult.Text = _lastRepairSummary.Message;
                AppendRepairLog(RepairProgressLabel.Text, success ? "ERFOLG" : "FEHLER");
                if (!success) AppendRepairLog(RepairResult.Text, "HINWEIS");
                if (tool.ReportFolder is null) Log(RepairProgressLabel.Text);
                NotifyRepairCompletion(success, RepairProgressLabel.Text);
            }
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
            RepairResult.Text = UiText.Error(ex, _english);
            AppendRepairLog($"{RepairProgressLabel.Text} {UiText.Error(ex, _english)}", "FEHLER");
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
            RepairRunningPanel.Visibility = Visibility.Collapsed;
            RepairToolScroll.Visibility = Visibility.Visible;
            UpdateRepairProgressAnimation();
            UpdateCompactLogActionState();
            UpdateRepairTime();
            ClearRepairLogButton.IsEnabled = true;
            _repairCancellation?.Dispose();
            _repairCancellation = null;
            _lastRepairSummary ??= RepairResultSummary.Create(mode, _lastRepairExitCode,
                _lastRepairCancelled, _repairOutput.ToString(), RepairResult.Text, _repairWatch.Elapsed, _english);
            SetTimerState(_timer.IsEnabled);
            if (_repairCategory == 3 && tool.IsNetworkAction) BuildNetworkView();
            UpdateRepairStatus();
            if (tool.ReportFolder is not null)
            {
                var reportStatus = _lastRepairSummary.WasCancelled ? DiagnosticReportStatus.Cancelled
                    : !_lastRepairSummary.IsSuccess ? DiagnosticReportStatus.Failed
                    : _lastRepairSummary.Output is "" ? DiagnosticReportStatus.Empty : DiagnosticReportStatus.Completed;
                SaveDiagnosticReport(tool, reportStatus, _repairWatch.Elapsed, _lastRepairSummary.Message, _repairOutput.ToString());
            }
            _pendingRepairSummary = _lastRepairSummary;
            ShowPendingRepairSummary();
        }
    }

    private DiagnosticReportStore? DiagnosticReportsFor(RepairTool? tool) =>
        tool?.ReportFolder is { } folder ? new DiagnosticReportStore(_logDirectory, folder) : null;

    private void SaveDiagnosticReport(RepairTool tool, DiagnosticReportStatus status, TimeSpan elapsed, string message, string output)
    {
        var store = DiagnosticReportsFor(tool);
        if (store is null) return;
        try
        {
            var date = DateTimeOffset.Now;
            var result = status switch
            {
                DiagnosticReportStatus.Opened => _english ? "Windows tool opened" : "Windows-Werkzeug geöffnet",
                DiagnosticReportStatus.Completed => _english ? "Completed" : "Abgeschlossen",
                DiagnosticReportStatus.Empty => _english ? "No matches" : "Keine Treffer",
                DiagnosticReportStatus.Failed => _english ? "Failed" : "Fehlgeschlagen",
                _ => _english ? "Cancelled" : "Abgebrochen"
            };
            var duration = elapsed.ToString(@"hh\:mm\:ss");
            var contents = string.Join(Environment.NewLine, tool.Title(_english), date.ToString("O"),
                "Status: " + result, (_english ? "Duration: " : "Laufzeit: ") + duration,
                "", _english ? "Command" : "Befehl", tool.CommandFor(_english, tool.NeedsAdapter ? _selectedNetworkRequest : null),
                "", _english ? "Result" : "Ergebnis", message, "", output);
            var (report, cleanupFailures) = store.Save(status, contents);
            Log($"{tool.Title(_english)} · {result} · {Path.Combine(store.RelativeFolder, report.FileName)}");
            if (cleanupFailures > 0)
                LogError(_english ? "Older diagnostic reports could not all be removed." : "Ältere Diagnoseprotokolle konnten nicht vollständig entfernt werden.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            LogError((_english ? "The diagnostic report could not be saved. " : "Das Diagnoseprotokoll konnte nicht gespeichert werden. ") +
                UiText.Error(ex, _english));
        }
    }


    private void RepairClockTick(object? sender, EventArgs e) => UpdateRepairTime();

    private void UpdateRepairTime()
    {
        var elapsed = _repairWatch.Elapsed;
        var clock = elapsed.ToString(elapsed.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss");
        var prefix = (_english ? "Elapsed " : "Laufzeit ") + clock;
        if (_repairRunning && _selectedRepairTool?.Executable == "sfc.exe")
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
            var dialog = new RepairResultWindow(summary, ThemePalette.For(SelectedTheme()), _english,
                DiagnosticReportsFor(_selectedRepairTool), _selectedRepairTool?.Title(_english)) { Owner = this };
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
            MessageBox.Show(this, UiText.Error(ex, _english), "PowerLeitstand PRO", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string GetRepairFailureHint(string? failure)
    {
        if (_selectedRepairTool?.ShowsDiagnosticOutput == true)
            return failure ?? (_english ? "Check the diagnostic output." : "Prüfe die Diagnoseausgabe.");
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
        if (!_repairRunning || _selectedRepairTool?.IsNetworkAction == true ||
            _repairCancellation is not { IsCancellationRequested: false } operation) return;
        var question = _selectedRepairTool?.IsReset == true
            ? (_english ? "Stop the operation? A cache reset already underway will finish restoring service states before stopping."
                : "Vorgang beenden? Beim laufenden Cache-Reset werden zuerst die Dienste wiederhergestellt.")
            : _selectedRepairTool?.IsRepair == true
                ? (_english ? "Stop this running Windows repair? It may be left incomplete."
                    : "Laufende Windows-Reparatur beenden? Der Vorgang kann unvollständig bleiben.")
                : (_english ? "Stop this operation?" : "Vorgang beenden?");
        if (MessageBox.Show(this, question, "PowerLeitstand PRO", MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (!_repairRunning || !ReferenceEquals(_repairCancellation, operation)) return;
        RepairCancelButton.IsEnabled = false;
        RepairProgressLabel.Text = _english ? "Stopping operation…" : "Vorgang wird beendet…";
        AppendRepairLog(RepairProgressLabel.Text);
        operation.Cancel();
        UpdateActivityIndicator();
        (System.Windows.Application.Current as App)?.UpdateTrayTimerState(true);
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
        UpdateActivityIndicator();
        if (_timer.IsEnabled) return;
        string title, detail, symbol, brush;
        if (_repairRunning)
        {
            title = _selectedRepairTool?.Title(_english) ?? "DISM";
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
