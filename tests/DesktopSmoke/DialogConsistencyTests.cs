using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class DialogConsistencyTests
{
    internal static int Run(App app, string output, bool routesOnly = false)
    {
        UiSoundService.MuteForTests = true;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        int checks = 0, cases = 0;
        var errors = new List<Exception>();
        app.DispatcherUnhandledException += (_, e) => { errors.Add(e.Exception); e.Handled = true; };
        void Assert(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        IEnumerable<DependencyObject> Tree(DependencyObject node)
        {
            yield return node;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) foreach (var child in Tree(VisualTreeHelper.GetChild(node, i))) yield return child;
        }
        void SetState(DependencyObject target, Type type, string keyName, bool value)
        {
            var key = (DependencyPropertyKey?)type.GetField(keyName, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.FlattenHierarchy)?.GetValue(null);
            if (key is null) throw new Exception("State simulation unavailable: " + keyName);
            target.SetValue(key, value);
        }
        void Save(Window w, string name)
        {
            w.UpdateLayout(); var bmp = new RenderTargetBitmap((int)Math.Ceiling(w.ActualWidth), (int)Math.Ceiling(w.ActualHeight), 96, 96, PixelFormats.Pbgra32); bmp.Render(w);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bmp)); using var file = File.Create(Path.Combine(output, name + ".png")); png.Save(file);
        }
        void Audit(Window w, string name, bool capture)
        {
            w.ShowActivated = false; w.Show(); Pump(); w.UpdateLayout();
            try
            {
                Assert(NativeWindowCapture.HasNoNativeTopStrip(w), "native frame " + name);
                var heading = Tree(w).OfType<TextBlock>().FirstOrDefault(t => t.Name == "UnifiedDialogHeading");
                var stripes = Tree(w).OfType<System.Windows.Shapes.Path>().FirstOrDefault(t => t.Name == "UnifiedDialogStripes");
                if (heading is not null && stripes is not null)
                {
                    var h = heading.TransformToAncestor(w).TransformBounds(new Rect(heading.RenderSize));
                    var s = stripes.TransformToAncestor(w).TransformBounds(new Rect(stripes.RenderSize));
                    Assert(h.Right <= s.Left + 1, "heading stays before decorative stripes " + name);
                }
                foreach (var button in Tree(w).OfType<Button>().Where(b => b.IsVisible).ToArray())
                {
                    button.ApplyTemplate();
                    var style = button.Style;
                    Assert(style == w.FindResource("UnifiedDialogAction") || style == w.FindResource("UnifiedDialogDanger") || style == w.FindResource("UnifiedDialogClose"), "unified button style " + name + "/" + button.Name);
                    var bounds = button.TransformToAncestor(w).TransformBounds(new Rect(button.RenderSize));
                    Assert(bounds.Left >= -.5 && bounds.Top >= -.5 && bounds.Right <= w.ActualWidth + .5 && bounds.Bottom <= w.ActualHeight + .5, "button outside dialog " + name + "/" + button.Name);
                    var triggerProperties = button.Template.Triggers.Cast<TriggerBase>().SelectMany(t => t is Trigger p ? new[] { p.Property.Name } : t is MultiTrigger m ? m.Conditions.Select(c => c.Property.Name) : Array.Empty<string>()).ToArray();
                    Assert(new[] { "IsMouseOver", "IsPressed", "IsEnabled", "IsKeyboardFocused" }.All(triggerProperties.Contains), "all button states " + name + "/" + button.Name);
                    var frameName = style == w.FindResource("UnifiedDialogClose") ? "CloseFrame" : style == w.FindResource("UnifiedDialogDanger") ? "RedCancelFrame" : "UnifiedButtonFrame";
                    var frame = (Border)button.Template.FindName(frameName, button);
                    var enabled = button.IsEnabled;
                    button.IsEnabled = true;
                    SetState(button, typeof(UIElement), "IsMouseOverPropertyKey", true); Pump();
                    if (frameName == "UnifiedButtonFrame") Assert(Math.Abs(frame.Opacity - .88) < .001, "hover opacity " + name);
                    else Assert(TextElementColor(frame) == "#FFFFFFFF", "white cross on red hover " + name);
                    if (frameName == "CloseFrame")
                        Assert(Tree(button).OfType<System.Windows.Shapes.Path>().All(p => p.Stroke?.ToString() == "#FFFFFFFF"), "actual close glyph turns white " + name);
                    SetState(button, typeof(ButtonBase), "IsPressedPropertyKey", true); Pump();
                    if (frameName != "CloseFrame") Assert(Math.Abs(frame.Opacity - .72) < .001, "pressed opacity " + name);
                    SetState(button, typeof(ButtonBase), "IsPressedPropertyKey", false);
                    SetState(button, typeof(UIElement), "IsMouseOverPropertyKey", false);
                    button.IsEnabled = false; Pump(); Assert(Math.Abs(frame.Opacity - .45) < .001, "disabled appearance " + name);
                    button.IsEnabled = enabled;
                    SetState(button, typeof(UIElement), "IsKeyboardFocusedPropertyKey", true); Pump();
                    Assert(frame.BorderThickness.Left >= (frameName == "CloseFrame" ? 1 : 2), "keyboard focus appearance " + name);
                    SetState(button, typeof(UIElement), "IsKeyboardFocusedPropertyKey", false);
                }
                if (capture) Save(w, name);
                cases++;
            }
            finally { w.Close(); Pump(); }
        }
        try
        {
            foreach (var theme in new[] { "Standard", "Classic", "Neon Blau", "Neon Pink", "Neon Grün", "Linux Mint", "Dark" })
            foreach (var english in new[] { false, true })
            {
                var suffix = theme.Replace(" ", "-") + (english ? "-EN" : "-DE");
                Console.WriteLine("Checking dialogs " + suffix);
                var palette = ThemePalette.For(theme);
                var main = new MainWindow(Path.Combine(output, "Fixture-" + suffix)) { ShowActivated = false };
                try
                {
                    app.MainWindow = main;
                    typeof(MainWindow).GetField("_english", flags)!.SetValue(main, english);
                    typeof(MainWindow).GetMethod("ApplyLanguage", flags)!.Invoke(main, null);
                    var themes = (ComboBox)main.FindName("ThemeBox"); themes.SelectedItem = themes.Items.OfType<ComboBoxItem>().First(i => (i.Tag?.ToString() ?? i.Content?.ToString()) == theme);
                    main.Show(); ((ComboBox)main.FindName("SizeBox")).SelectedIndex = 1; Pump();
                    var capture = theme == "Standard" && !english || theme == "Classic" && english;
                    for (var page = 0; page < 4; page++)
                    {
                        typeof(MainWindow).GetMethod("ShowPage", flags)!.Invoke(main, new object[] { page }); Pump();
                        ThemedMessageBoxWindow? opened = null;
                        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                        timer.Tick += (_, _) =>
                        {
                            opened = app.Windows.OfType<ThemedMessageBoxWindow>().FirstOrDefault(w => w.IsVisible);
                            if (opened is null) return;
                            timer.Stop();
                            try
                            {
                                Assert(opened.FindResource("UnifiedAccent").ToString() == palette.Colors[7].Insert(1, "FF"), "page log dialog inherits theme " + suffix + "/" + page);
                                Assert(Tree(opened).OfType<TextBlock>().Any(t => t.Name == "UnifiedDialogHeading"), "page log dialog uses unified header " + suffix + "/" + page);
                                if (capture) Save(opened, "Log-Loeschen-Seite-" + (page + 1) + "-" + suffix);
                                ((Button)opened.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            }
                            catch (Exception exception) { errors.Add(exception); opened.Close(); }
                        };
                        timer.Start();
                        try { ((Button)main.FindName("StandardClearIconButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                        finally { timer.Stop(); }
                        Assert(opened is not null && opened.Result == MessageBoxResult.No, "log deletion cancelled through page button " + suffix + "/" + page);
                        cases++;
                    }
                    if (routesOnly) continue;
                    foreach (var target in new[] { "ConfirmButton", "CancelButton", "header" })
                    {
                        var prompt = new ThemedMessageBoxWindow(english ? "Delete this backup?" : "Diese Sicherung löschen?", "PowerLeitstand PRO", MessageBoxButton.YesNo, MessageBoxImage.Warning, palette, english) { Owner = main };
                        prompt.Loaded += (_, _) => app.Dispatcher.BeginInvoke(() =>
                        {
                            var action = target == "header" ? Tree(prompt).OfType<Button>().Single(b => b.Tag as string == "UnifiedHeaderClose") : (Button)prompt.FindName(target);
                            action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        });
                        prompt.ShowDialog(); Assert(prompt.Result == (target == "ConfirmButton" ? MessageBoxResult.Yes : MessageBoxResult.No), "safe confirmation result " + suffix + "/" + target);
                    }
                    foreach (var target in new[] { "StartButton", "CancelButton" })
                    {
                        var prompt = new RepairConfirmWindow("SFC /scannow", palette, english) { Owner = main };
                        prompt.Loaded += (_, _) => app.Dispatcher.BeginInvoke(() => ((Button)prompt.FindName(target)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
                        Assert(prompt.ShowDialog() == (target == "StartButton"), "repair confirmation result only; no command " + suffix);
                    }
                    Audit(new UserEditorDialog(main, null), "Benutzer-Erstellen-" + suffix, capture);
                    Audit(new UserEditorDialog(main, new UserEntry("Beispielkonto", "Standardbenutzer", "Beispieldaten", "Aktiv", true, "Beispielkonto")), "Benutzer-Bearbeiten-" + suffix, capture);
                    foreach (var save in new[] { true, false })
                    {
                        var editor = new UserEditorDialog(main, new UserEntry("Beispielkonto", "Standardbenutzer", "Beispieldaten", "Aktiv", true, "Beispielkonto"));
                        editor.Loaded += (_, _) => app.Dispatcher.BeginInvoke(() =>
                        {
                            var enabled = (CheckBox)typeof(UserEditorDialog).GetField("_enabled", flags)!.GetValue(editor)!;
                            var admin = (CheckBox)typeof(UserEditorDialog).GetField("_administrator", flags)!.GetValue(editor)!;
                            foreach (var state in new[] { true, false })
                            {
                                enabled.IsChecked = state; admin.IsChecked = state; Pump();
                                foreach (var check in new[] { enabled, admin })
                                {
                                    Assert(System.Windows.Automation.AutomationProperties.GetHelpText(check) == (state ? english ? "On" : "An" : english ? "Off" : "Aus"), "status label follows switch " + suffix);
                                    check.ApplyTemplate();
                                    var track = (Border)check.Template.FindName("SwitchTrack", check);
                                    Assert(track.Background.ToString() == (state ? "#FF168A69" : "#FF98283F"), "status switch green/red " + suffix);
                                }
                            }
                            Assert(editor.Values is null, "switches do not commit before Save " + suffix);
                            admin.IsChecked = true;
                            var caption = save ? english ? "Save" : "Speichern" : english ? "Cancel" : "Abbrechen";
                            Tree(editor).OfType<Button>().Single(b => Tree(b).OfType<TextBlock>().Any(t => t.Text == caption)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        });
                        Assert(editor.ShowDialog() == save, "editor Save/Cancel result " + suffix);
                        Assert(save ? editor.Values is { Enabled: false, Administrator: true } : editor.Values is null, "editor returns only confirmed state " + suffix);
                        cases++;
                    }
                    var password = (Window)Activator.CreateInstance(typeof(PasswordResetDialog), flags, null, new object[] { main, "Beispielkonto", english }, null)!;
                    password.ShowActivated = false; password.Show(); Pump();
                    var remove = (CheckBox)typeof(PasswordResetDialog).GetField("_removePassword", flags)!.GetValue(password)!;
                    var fullPasswordHeight = password.ActualHeight;
                    remove.IsChecked = true; Pump(); Assert(password.ActualHeight < fullPasswordHeight, "remove password compacts to its remaining content");
                    remove.IsChecked = false; Pump(); Assert(Math.Abs(password.ActualHeight - fullPasswordHeight) < 1, "password fields restore full content height");
                    password.Hide();
                    Audit(password, "Kennwort-" + suffix, capture);
                    foreach (var command in new[] { "DISM /Online /Cleanup-Image /RestoreHealth", "SFC /scannow" })
                        Audit(new RepairConfirmWindow(command, palette, english) { Owner = main }, "Reparaturstart-" + command.Split(' ')[0] + "-" + suffix, capture);
                    foreach (var (name, code, cancel, text) in new[] { ("Erfolg", 0, false, "successfully repaired"), ("Abbruch", 1, true, ""), ("Fehler", 5, false, "") })
                        Audit(new RepairResultWindow(RepairResultSummary.Create("sfc", code, cancel, text,
                            code == 5 ? string.Join(" ", Enumerable.Repeat(english ? "Example diagnostic detail." : "Beispiel einer Diagnosemeldung.", 30)) : "",
                            TimeSpan.FromSeconds(42), english), palette, english) { Owner = main }, "Reparaturergebnis-" + name + "-" + suffix, capture);
                    Audit(new RepairLogWindow(new[] { new RepairLogEntry("12:00", "DISM", "INFO", english ? "Example log entry." : "Beispieldaten: Protokolleintrag.") }, palette, english, null) { Owner = main }, "Reparaturprotokoll-" + suffix, capture);
                    foreach (var image in new[] { MessageBoxImage.Warning, MessageBoxImage.Error, MessageBoxImage.Information })
                        Audit(new ThemedMessageBoxWindow(english ? "Example message." : "Beispielmeldung.", "PowerLeitstand PRO", MessageBoxButton.OK, image, palette, english) { Owner = main }, "Meldung-" + image + "-" + suffix, capture);
                    foreach (var text in english ? new[] { "Delete this backup?", "Create local user Example?", "Reset password for Example?", "Remove the password for Example?" } : new[] { "Diese Sicherung löschen?", "Lokalen Benutzer Beispiel erstellen?", "Kennwort für Beispiel zurücksetzen?", "Kennwort für Beispiel entfernen?" })
                        Audit(new ThemedMessageBoxWindow(text, "PowerLeitstand PRO", MessageBoxButton.YesNo, MessageBoxImage.Warning, palette, english) { Owner = main }, "Bestaetigung-" + cases + "-" + suffix, false);
                    Audit(new UpdateInstallWindow(palette, english, AppVersion.Installed, new AvailableUpdate(new Version(1, 1, 6), "", new string('a', 64))) { Owner = main }, "Installation-" + suffix, capture);
                }
                finally { main.Close(); Pump(); }
            }
            Assert(errors.Count == 0, "unhandled dialog errors: " + string.Join("; ", errors.Select(e => e.Message)));
            Console.WriteLine($"PASS UNIFIED DIALOGS: {checks} assertions, {cases} dialog states, seven themes, DE/EN, simulated hover/pressed/disabled states. No account, power, repair or download actions executed.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private static string TextElementColor(Border frame) => ((SolidColorBrush)frame.GetValue(System.Windows.Documents.TextElement.ForegroundProperty)).Color.ToString();
}
