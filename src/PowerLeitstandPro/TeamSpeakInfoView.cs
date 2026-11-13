using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;

namespace PowerControlProWpf;

internal sealed class TeamSpeakInfoView : StackPanel, IDisposable
{
    private readonly ProgramCatalogView _view;
    private readonly Window _owner;
    private readonly ITeamSpeakInfoService _service;
    private readonly Action _close;
    private readonly CancellationTokenSource _cancel = new();
    private readonly StackPanel _rows = new();
    private readonly TextBlock _hint;
    private readonly Button _refresh;
    private readonly Dictionary<int, CheckBox> _checks = [];
    private readonly Dictionary<int, State> _states = [];
    private readonly Dictionary<int, Task> _requests = [];
    private readonly HashSet<int> _selected = [];
    private TeamSpeakSnapshot _snapshot = new([], false);
    private Task<IReadOnlyDictionary<int, string>>? _online;
    private DateTime _onlineSince;
    private bool _initializing = true, _closed;
    private int? _expanded;
    private sealed class State { internal IReadOnlyList<TeamSpeakCopy> Copies = []; internal bool Complete, Checking; internal string? Available, Error; }
    internal IReadOnlyCollection<int> SelectedEditions => _selected;
    internal Task Pending => Task.WhenAll(_requests.Values.ToArray());
    internal void RefreshLayout() => Render();
    internal void ReportError(Exception error) { if (!_closed) { _hint.Text = UiText.Error(error, _view.English); _hint.Visibility = Visibility.Visible; } }
    private string L(string de, string en) => _view.English ? en : de;
    internal TeamSpeakInfoView(ProgramCatalogView view, Window owner, Action close)
    {
        _view = view; _owner = owner; _service = view.TeamSpeakService; _close = close;
        var what = Text(L("Was ist TeamSpeak?", "What is TeamSpeak?"), 12.5); what.FontWeight = FontWeights.SemiBold; Children.Add(what);
        var description = Text(L("Sprachchat für Gaming und Teams: miteinander sprechen, Kanäle nutzen und auf eigenen Servern kommunizieren.", "Voice chat for gaming and teams: talk to each other, use channels and communicate on your own servers."), 12); description.Margin = new(0, 3, 0, 7); Children.Add(description);
        var choices = new Grid { Margin = new(0, 0, 0, 7) };
        choices.ColumnDefinitions.Add(new() { Width = new GridLength(113) }); choices.ColumnDefinitions.Add(new() { Width = new GridLength(113) }); choices.ColumnDefinitions.Add(new());
        foreach (int edition in new[] { 3, 6 })
        {
            var cell = new Grid(); cell.ColumnDefinitions.Add(new() { Width = new GridLength(29) }); cell.ColumnDefinitions.Add(new());
            var check = new CheckBox { Name = "TeamSpeakEdition" + edition, Style = (Style)view.FindResource("UpdateSelection"), Width = 24, Height = 24, IsEnabled = false, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(check, L("Ausgabe prüfen: TeamSpeak ", "Check edition: TeamSpeak ") + edition);
            _checks[edition] = check; _states[edition] = new(); cell.Children.Add(check);
            var label = Text("TeamSpeak " + edition, 11.5); label.VerticalAlignment = VerticalAlignment.Center;
            label.MouseLeftButtonDown += (_, e) => { if (check.IsEnabled) { check.IsChecked = check.IsChecked != true; e.Handled = true; } };
            Grid.SetColumn(label, 1); cell.Children.Add(label); Grid.SetColumn(cell, edition == 3 ? 0 : 1); choices.Children.Add(cell);
            check.Checked += (_, _) => Changed(edition, true); check.Unchecked += (_, _) => Changed(edition, false);
        }
        _refresh = Action("⟳  " + L("Auf Updates prüfen", "Check for updates"), 27); _refresh.Name = "TeamSpeakRefresh"; _refresh.FontSize = 10.5; _refresh.Padding = new(2, 0, 2, 0); _refresh.Margin = new(2, 0, 0, 0); _refresh.IsEnabled = false;
        _refresh.Click += async (_, _) => await RefreshAsync(); Grid.SetColumn(_refresh, 2); choices.Children.Add(_refresh); Children.Add(choices);
        _hint = Text(L("Installationen werden geprüft …", "Checking installed editions …"), 10.5, true); _hint.Margin = new(0, 0, 0, 6); Children.Add(_hint); Children.Add(_rows);
    }
    private void SetLocal(TeamSpeakSnapshot snapshot)
    {
        _snapshot = snapshot;
        foreach (int edition in new[] { 3, 6 }) { _states[edition].Copies = snapshot.Copies.Where(x => x.Edition == edition).ToArray(); _states[edition].Complete = snapshot.Complete; }
    }
    internal async Task InitializeAsync()
    {
        try
        {
            var local = await _service.ReadLocalAsync(_cancel.Token); if (_closed) return; SetLocal(local);
            foreach (int edition in new[] { 3, 6 })
            {
                if (_states[edition].Copies.Count > 0) { _selected.Add(edition); _checks[edition].IsChecked = true; }
                _checks[edition].IsEnabled = true;
            }
            if (_selected.Count == 0 || local.Copies.Any(x => x.Edition == 5)) { _selected.Add(6); _checks[6].IsChecked = true; }
            _expanded = _selected.Contains(6) ? 6 : 3;
            _initializing = false; _hint.Visibility = Visibility.Collapsed; Render();
            foreach (int edition in _selected.ToArray()) StartCheck(edition, false);
            await Pending;
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception error)
        {
            if (_closed) return; _initializing = false;
            foreach (var check in _checks.Values) check.IsEnabled = true;
            ReportError(error); Render();
        }
    }
    private void Changed(int edition, bool selected)
    {
        if (_initializing || _closed) return;
        if (selected) { _selected.Add(edition); _expanded = edition; StartCheck(edition, true); }
        else { _selected.Remove(edition); if (_expanded == edition) _expanded = _selected.OrderDescending().Cast<int?>().FirstOrDefault(); }
        Render();
    }
    internal async Task RefreshAsync()
    {
        if (_closed || _initializing || _requests.Values.Any(t => !t.IsCompleted)) return;
        _online = null; foreach (int edition in _selected.ToArray()) StartCheck(edition, true); await Pending;
    }
    private void StartCheck(int edition, bool refreshLocal)
    { if (_requests.TryGetValue(edition, out var pending) && !pending.IsCompleted) return; _requests[edition] = CheckAsync(edition, refreshLocal); }
    private Task<IReadOnlyDictionary<int, string>> OnlineAsync()
    {
        if (_online is null || _online.IsFaulted || _online.IsCanceled || _online.IsCompleted && DateTime.UtcNow - _onlineSince > TimeSpan.FromMinutes(2))
        { _onlineSince = DateTime.UtcNow; _online = _service.ReadAvailableAsync(_cancel.Token); }
        return _online;
    }
    private async Task CheckAsync(int edition, bool refreshLocal)
    {
        var state = _states[edition]; state.Checking = true; state.Error = null; if (!_closed && _selected.Contains(edition)) Render();
        try
        {
            if (refreshLocal) { var local = await _service.ReadLocalAsync(_cancel.Token); if (_closed) return; SetLocal(local); }
            var available = await OnlineAsync(); if (_closed) return; state.Available = available.GetValueOrDefault(edition);
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception error) { if (!_closed) { state.Available = null; state.Error = UiText.Error(error, _view.English); } }
        finally { state.Checking = false; if (!_closed && _selected.Contains(edition)) Render(); }
    }
    private TextBlock Text(string content, double size, bool muted = false)
    { var text = new TextBlock { Text = content, FontSize = size, TextWrapping = TextWrapping.Wrap }; text.SetResourceReference(TextBlock.ForegroundProperty, muted ? "UnifiedMuted" : "UnifiedText"); return text; }
    private Button Action(string caption, double height)
    { var button = new Button { Content = caption, FontSize = 11.5, Height = height, MinHeight = height, Padding = new(7, 0, 7, 0) }; UnifiedDialogAppearance.Button(_owner, button, DialogButtonKind.Primary); return button; }
    private void Render()
    {
        if (_closed) return; _rows.Children.Clear(); _refresh.IsEnabled = !_initializing && !_states.Values.Any(x => x.Checking) && _view.CanChooseProgramFile && _selected.Count > 0;
        foreach (int edition in new[] { 3, 6 }.Where(_selected.Contains))
        {
            var state = _states[edition];
            if (state.Copies.Count == 0) _rows.Children.Add(Card(edition, null, state, 0, 0));
            else for (int index = 0; index < state.Copies.Count; index++) _rows.Children.Add(Card(edition, state.Copies[index], state, index + 1, state.Copies.Count));
        }
        if (_selected.Count == 0) _rows.Children.Add(Text(L("Bitte mindestens eine Ausgabe auswählen.", "Select at least one edition."), 12, true));
    }
    private Border Card(int edition, TeamSpeakCopy? copy, State state, int index, int count)
    {
        bool newer = !state.Checking && state.Error is null && copy is not null && TeamSpeakInfoService.Newer(copy.Version, state.Available);
        bool beta = state.Available?.Contains("-beta", StringComparison.OrdinalIgnoreCase) == true;
        bool current = TeamSpeakInstallPolicy.Equal(copy?.Version, state.Available);
        string caption = state.Checking ? L("Wird geprüft …", "Checking …") : copy is null ? state.Complete ? L("Nicht installiert", "Not installed") : L("Nicht sicher ermittelbar", "Not reliably determined")
            : newer ? L("Update verfügbar", "Update available") : state.Error is not null || state.Available is null ? L("Installiert · Onlineversion offen", "Installed · online version unknown")
            : copy.Version is null ? L("Installiert · Version unbekannt", "Installed · version unknown") : current ? L("Installiert · Aktuell", "Installed · Up to date") : L("Installiert · Kanal prüfen", "Installed · check channel");
        if (beta && !state.Checking) caption += " · Beta";
        var status = Text(caption, 10.5, true); status.Name = "TeamSpeakStatus" + edition; status.VerticalAlignment = VerticalAlignment.Center;
        if (newer) status.Foreground = new SolidColorBrush(Color.FromRgb(255, 180, 67)); else if (current && !state.Checking) status.SetResourceReference(TextBlock.ForegroundProperty, "ReadyBrush");
        var header = new Grid(); header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = new GridLength(15) });
        var title = Text("TeamSpeak " + edition + (count > 1 ? " · " + index + "/" + count : ""), 12.5); title.FontWeight = FontWeights.SemiBold; header.Children.Add(title); status.MaxWidth = _view.Compact ? 215 : 340; Grid.SetColumn(status, 1); header.Children.Add(status);
        var chevron = new System.Windows.Shapes.Path { Data = Geometry.Parse(_expanded == edition ? "M0,0 L4,4 L8,0" : "M0,0 L4,4 L0,8"), Width = 8, Height = 8, StrokeThickness = 1.5, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Right }; chevron.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "UnifiedAccent"); Grid.SetColumn(chevron, 2); header.Children.Add(chevron);
        var toggle = new Button { Name = "TeamSpeakExpand" + edition, Content = header, Padding = new(0), Background = Brushes.Transparent, HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch };
        toggle.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
              <Border x:Name="Frame" Background="{TemplateBinding Background}" CornerRadius="4"><ContentPresenter HorizontalAlignment="Stretch"/></Border>
              <ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Frame" Property="Background" Value="{DynamicResource UnifiedHover}"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource UnifiedAccent}"/><Setter TargetName="Frame" Property="BorderThickness" Value="1"/></Trigger></ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        AutomationProperties.SetName(toggle, "TeamSpeak " + edition + ": " + L("Details ein-/ausklappen", "Expand/collapse details"));
        toggle.Click += (_, _) => { _expanded = _expanded == edition ? null : edition; Render(); };
        var content = new StackPanel(); content.Children.Add(toggle);
        var details = new StackPanel { Name = "TeamSpeakDetails" + edition, Margin = new(0, 5, 0, 0), Visibility = _expanded == edition ? Visibility.Visible : Visibility.Collapsed };
        var kind = TeamSpeakInstallPolicy.Decide(edition, state.Available, _snapshot, out string? reason);
        foreach (var (label, value, row) in new[] { (L("Installierte Version", "Installed version"), copy?.Version ?? (copy is null && state.Complete ? L("Nicht installiert", "Not installed") : L("Nicht ermittelbar", "Unavailable")), 0),
            (L("Verfügbare Version", "Available version"), state.Checking ? L("Wird geprüft …", "Checking …") : state.Available ?? L("Nicht bestätigt", "Not verified"), 1) })
        {
            var field = new Grid { Margin = new(0, 0, 0, 3) }; field.ColumnDefinitions.Add(new() { Width = new GridLength(145) }); field.ColumnDefinitions.Add(new()); field.Children.Add(Text(label, 11.5));
            var valueText = Text(value, 11.5); valueText.Name = "TeamSpeak" + (row == 0 ? "Installed" : "Available") + edition;
            if (row == 0 && newer) valueText.SetResourceReference(TextBlock.ForegroundProperty, "UnifiedDangerBorder"); if (row == 1 && !state.Checking && state.Available is not null) valueText.SetResourceReference(TextBlock.ForegroundProperty, "ReadyBrush");
            Grid.SetColumn(valueText, 1); field.Children.Add(valueText);
            if (row == 0 && kind == TeamSpeakInstallKind.Update)
            {
                field.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                var update = Action("Update", 29); update.Name = "TeamSpeakUpdate" + edition; update.Width = 78; update.Margin = new(5, 0, 0, 0);
                update.Content = new TextBlock { Text = "Update", Foreground = Brushes.White };
                update.Background = new SolidColorBrush(Color.FromRgb(7, 85, 69)); update.BorderBrush = new SolidColorBrush(Color.FromRgb(25, 216, 174)); update.Foreground = Brushes.White;
                update.Resources["UnifiedHover"] = new SolidColorBrush(Color.FromRgb(10, 118, 88));
                update.IsEnabled = !state.Checking && state.Error is null && _view.CanChooseProgramFile;
                AutomationProperties.SetName(update, L("Update installieren: TeamSpeak ", "Install update: TeamSpeak ") + edition);
                update.Click += async (_, _) => { if (!_closed && _view.CanChooseProgramFile && state.Available is { } version) await _view.InstallTeamSpeakAsync(new(edition, version, TeamSpeakInstallKind.Update, _snapshot, copy)); };
                Grid.SetColumn(update, 2); field.Children.Add(update);
            }
            details.Children.Add(field);
        }
        if (edition == 6 && copy is null && _snapshot.Copies.FirstOrDefault(x => x.Edition == 5) is { } legacy)
        { var note = Text("TeamSpeak " + (legacy.Version ?? "5") + L(" erkannt. Installation von 6 ist ein Ausgabewechsel, kein gewöhnliches Update.", " detected. Installing 6 is an edition switch, not a regular update."), 10.5); note.Foreground = new SolidColorBrush(Color.FromRgb(255, 180, 67)); note.Margin = new(0, 5, 0, 3); details.Children.Add(note); }
        string? directory = ProgramLocation.DirectoryOf(copy?.Path);
        var path = new Grid { Margin = new(0, 4, 0, 0) }; path.ColumnDefinitions.Add(new()); path.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        string locationText = directory is not null ? L("Speicherort: ", "Location: ") + ProgramLocation.Shorten(directory)
            : copy is null ? edition == 6 ? L("Standardpfad: ", "Default path: ") + ProgramLocation.Shorten(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "TeamSpeak")) : L("Standardpfad des Herstellers", "Publisher default location") : L("Speicherort nicht bestätigt", "Location not verified");
        var location = Text(locationText, 11); location.TextWrapping = TextWrapping.NoWrap; location.TextTrimming = TextTrimming.CharacterEllipsis; location.VerticalAlignment = VerticalAlignment.Center; location.ToolTip = copy?.Path ?? locationText; path.Children.Add(location);
        var folder = Action("", 24); folder.Width = 29; folder.Padding = new(0); folder.Margin = new(6, 0, 0, 0); folder.IsEnabled = directory is not null;
        folder.Content = new System.Windows.Shapes.Path { Data = Geometry.Parse("M1,3 L1,1 L6,1 L8,3 L14,3 L14,11 L1,11 Z M1,4 L14,4"), Width = 12, Height = 10, Stretch = Stretch.Uniform, StrokeThickness = 1.2, Stroke = (Brush)_owner.FindResource("UnifiedText") };
        AutomationProperties.SetName(folder, L("Ordner öffnen: TeamSpeak ", "Open folder: TeamSpeak ") + edition);
        folder.Click += (_, _) => { try { var currentPath = ProgramLocation.DirectoryOf(copy?.Path) ?? throw new DirectoryNotFoundException(); System.Diagnostics.Process.Start(DrivePathLauncher.CreateStartInfo(currentPath)); } catch (Exception error) { state.Error = UiText.Error(error, _view.English); Render(); } }; Grid.SetColumn(folder, 1); path.Children.Add(folder); details.Children.Add(path);
        string actionLabel = copy is not null ? L("⟳  Neu installieren", "⟳  Reinstall") : L("↓  TeamSpeak ", "↓  Install TeamSpeak ") + edition + (_view.English ? "" : " installieren");
        var action = Action(actionLabel, 29); action.Name = "TeamSpeakAction" + edition; action.Margin = new(0, 4, 0, 0); action.IsEnabled = kind is not null && !state.Checking && state.Error is null && count <= 1 && _view.CanChooseProgramFile;
        action.ToolTip = actionLabel; AutomationProperties.SetName(action, actionLabel);
        action.Click += async (_, _) => { if (_closed || !_view.CanChooseProgramFile) return; var decision = TeamSpeakInstallPolicy.Decide(edition, state.Available, _snapshot, out _); if (decision is { } selected && state.Available is { } version) await _view.InstallTeamSpeakAsync(new(edition, version, selected, _snapshot, copy, ReinstallRequested: copy is not null)); };
        details.Children.Add(action);
        if (copy is not null)
        {
            var remove = Action(L("Deinstallieren …", "Uninstall …"), 29); remove.Name = "TeamSpeakUninstall" + edition;
            UnifiedDialogAppearance.Button(_owner, remove, DialogButtonKind.Danger); remove.Margin = new(0, 4, 0, 0);
            remove.IsEnabled = _view.CanChooseProgramFile; AutomationProperties.SetName(remove, L("Deinstallieren: TeamSpeak ", "Uninstall: TeamSpeak ") + edition);
            remove.Click += async (_, _) =>
            {
                if (_closed || !_view.CanChooseProgramFile) return;
                // A missing path cannot select one edition safely. Use Windows instead of
                // exposing all TeamSpeak editions under a single deletion action.
                try { _close(); await ProgramRemovalDialog.ShowAsync(_view, ProgramCatalog.Entries.Single(e => e.Id == "teamspeak"), copy.Path ?? ""); await _view.RefreshAsync(); }
                catch (Exception error) { _view.WriteLog?.Invoke(UiText.Error(error, _view.English)); }
            };
            details.Children.Add(remove);
        }
        string? noteText = state.Error is not null ? L("Prüfung nicht vollständig: ", "Check incomplete: ") + state.Error : !state.Checking && reason is not null ? reason switch
        { "source" => L("Hersteller-Download nicht bestätigt. Erneut prüfen.", "Publisher download not verified. Check again."), "multiple" => L("Mehrere Installationen: Ziel zuerst eindeutig zuordnen.", "Multiple installations: first identify the target."), "incomplete" => L("Lokale Erkennung unvollständig. Erneut prüfen.", "Local detection incomplete. Check again."), "older" => L("Kein passendes Update ohne Downgrade bestätigt.", "No matching update without downgrade verified."), _ => L("Installationsvariante oder Programmdatei nicht bestätigt.", "Installation variant or executable not verified.") } : null;
        if (noteText is not null) { var note = Text(noteText, 10.5, true); note.Margin = new(0, 4, 0, 0); details.Children.Add(note); }
        content.Children.Add(details);
        var card = new Border { Name = "TeamSpeakResult" + edition, Child = content, Padding = new(10, 4, 10, 4), BorderThickness = new(1), CornerRadius = new(8), Margin = new(0, 0, 0, 4) }; card.SetResourceReference(Border.BorderBrushProperty, "UnifiedAccent"); return card;
    }
    public void Dispose() { if (_closed) return; _closed = true; _cancel.Cancel(); }
}
