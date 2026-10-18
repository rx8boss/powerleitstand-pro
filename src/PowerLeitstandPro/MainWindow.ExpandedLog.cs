using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using ListBox = System.Windows.Controls.ListBox;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;

namespace PowerControlProWpf;

public partial class MainWindow
{
    private Border _expandedLogPanel = null!;
    private ListBox _expandedLogList = null!;
    private TextBlock _expandedLogTitle = null!;
    private TextBlock _expandedLogCount = null!;
    private TextBlock _expandedLogEmpty = null!;
    private TextBlock _expandedLogHint = null!;
    private bool _expandedLogOpen;
    private int _expandedLogAnimationVersion;
    private string? _expandedLogTheme;

    private void InitializeExpandedLog()
    {
        _expandedLogPanel = (Border)XamlReader.Parse("""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    HorizontalAlignment="Center" VerticalAlignment="Center"
                    BorderBrush="{DynamicResource AccentBrush}" BorderThickness="1.2"
                    CornerRadius="10" Padding="15,9" Visibility="Collapsed">
              <Grid>
                <Grid.RowDefinitions><RowDefinition Height="31"/><RowDefinition Height="25"/><RowDefinition Height="*"/><RowDefinition Height="24"/></Grid.RowDefinitions>
                <Grid Width="110" Height="30" HorizontalAlignment="Right" VerticalAlignment="Top" Margin="0,0,38,0" IsHitTestVisible="False" ClipToBounds="True">
                  <Path Data="M 86,0 L 110,0 L 80,30 L 56,30 Z" Fill="{DynamicResource AccentBrush}" Opacity="0.10"/>
                  <Path Data="M 76,0 L 46,30 M 94,0 L 64,30" Stroke="{DynamicResource AccentBrush}" StrokeThickness="0.8" Opacity="0.55"/>
                </Grid>
                <Grid><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                  <TextBlock x:Name="Title" FontSize="18" FontWeight="SemiBold" VerticalAlignment="Center" Foreground="{DynamicResource TextBrush}"/>
                  <Button x:Name="Close" Grid.Column="1" Content="×" FontSize="23" Width="28" Height="27" Padding="0" ToolTip="Schließen"/>
                </Grid>
                <TextBlock x:Name="Count" Grid.Row="1" FontSize="11" VerticalAlignment="Center" Foreground="{DynamicResource SubtleTextBrush}"/>
                <Border Grid.Row="2" Background="{DynamicResource InputBrush}" BorderBrush="{DynamicResource InputBorderBrush}" BorderThickness="1" CornerRadius="5">
                  <Grid>
                    <ListBox x:Name="Entries" Background="Transparent" BorderThickness="0" Padding="0"
                             HorizontalContentAlignment="Stretch" Foreground="{DynamicResource TextBrush}"
                             ScrollViewer.VerticalScrollBarVisibility="Hidden" ScrollViewer.HorizontalScrollBarVisibility="Disabled"
                             ScrollViewer.CanContentScroll="True" VirtualizingPanel.ScrollUnit="Pixel">
                      <ListBox.ItemContainerStyle><Style TargetType="ListBoxItem">
                        <Setter Property="Padding" Value="0"/><Setter Property="HorizontalContentAlignment" Value="Stretch"/>
                        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ListBoxItem">
                          <Border BorderBrush="{DynamicResource InputBorderBrush}" BorderThickness="0,0,0,1"><ContentPresenter/></Border>
                        </ControlTemplate></Setter.Value></Setter>
                      </Style></ListBox.ItemContainerStyle>
                      <ListBox.ItemTemplate><DataTemplate>
                        <Grid Margin="10,8"><Grid.ColumnDefinitions><ColumnDefinition Width="75"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                          <TextBlock Text="{Binding DisplayTime}" FontSize="12" Foreground="{DynamicResource SubtleTextBrush}" ToolTip="{Binding Tooltip}"/>
                          <StackPanel Grid.Column="1"><TextBlock Text="{Binding Category}" FontSize="10" FontWeight="SemiBold" Foreground="{DynamicResource AccentBrush}"/>
                            <TextBlock Text="{Binding Message}" FontSize="13" TextWrapping="Wrap" Margin="0,3,0,0" Foreground="{DynamicResource TextBrush}" ToolTip="{Binding Tooltip}"/>
                          </StackPanel>
                        </Grid>
                      </DataTemplate></ListBox.ItemTemplate>
                    </ListBox>
                    <TextBlock x:Name="Empty" FontSize="13" Margin="12" TextWrapping="Wrap" Foreground="{DynamicResource SubtleTextBrush}"/>
                  </Grid>
                </Border>
                <TextBlock x:Name="Hint" Grid.Row="3" FontSize="10" VerticalAlignment="Center" Foreground="{DynamicResource SubtleTextBrush}"/>
              </Grid>
            </Border>
            """);
        _expandedLogList = (ListBox)_expandedLogPanel.FindName("Entries");
        _expandedLogTitle = (TextBlock)_expandedLogPanel.FindName("Title");
        _expandedLogCount = (TextBlock)_expandedLogPanel.FindName("Count");
        _expandedLogEmpty = (TextBlock)_expandedLogPanel.FindName("Empty");
        _expandedLogHint = (TextBlock)_expandedLogPanel.FindName("Hint");
        var close = (Button)_expandedLogPanel.FindName("Close");
        close.Style = (Style)Resources["OptionsCloseButton"];
        close.Click += (_, _) => SetExpandedLogOpen(false);
        System.Windows.Automation.AutomationProperties.SetName(close, "Protokollansicht schließen");
        RegisterName("ExpandedLogPanel", _expandedLogPanel);
        RegisterName("ExpandedLogList", _expandedLogList);
        RegisterName("ExpandedLogCloseButton", close);
        _expandedLogPanel.RenderTransform = new TranslateTransform();
        Grid.SetRowSpan(_expandedLogPanel, Root.RowDefinitions.Count);
        Panel.SetZIndex(_expandedLogPanel, 3);
        Root.Children.Add(_expandedLogPanel);
        foreach (var log in new[] { StandardLogPreview, LogBox, UserLogBox, RepairLogBox })
        {
            log.PreviewMouseLeftButtonUp += ToggleExpandedLogFromEntry;
            log.PreviewKeyDown += (_, e) =>
            {
                if (e.Key is Key.Enter or Key.Space) { SetExpandedLogOpen(!_expandedLogOpen); e.Handled = true; }
            };
        }
        StandardLogEmptyState.MouseLeftButtonUp += (_, e) => { SetExpandedLogOpen(!_expandedLogOpen); e.Handled = true; };
        _eventLogEntries.CollectionChanged += ExpandedLogEntriesChanged;
        _repairLogEntries.CollectionChanged += ExpandedLogEntriesChanged;
        LayoutUpdated += (_, _) => { if (_expandedLogOpen) UpdateExpandedLogLayout(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _expandedLogOpen) { SetExpandedLogOpen(false); e.Handled = true; }
        };
        Closed += (_, _) => SetExpandedLogOpen(false, false);
    }

    private void ToggleExpandedLogFromEntry(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox log || e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(log, source) is not ListBoxItem) return;
        SetExpandedLogOpen(!_expandedLogOpen);
        e.Handled = true;
    }

    private void ExpandedLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _expandedLogList.ItemsSource)) return;
        UpdateExpandedLogLabels();
        // Do not move somebody who is reading older entries back to the top.
        var scroll = FindVisualChildren<ScrollViewer>(_expandedLogList).FirstOrDefault();
        if (_expandedLogOpen && e.Action == NotifyCollectionChangedAction.Add && scroll?.VerticalOffset <= 1)
            Dispatcher.BeginInvoke(() =>
            {
                if (_expandedLogOpen && _expandedLogList.Items.Count > 0)
                    _expandedLogList.ScrollIntoView(_expandedLogList.Items[0]);
            }, DispatcherPriority.Background);
    }

    private void UpdateExpandedLogContext()
    {
        if (_expandedLogPanel is null) return;
        _expandedLogList.ItemsSource = _pageIndex == 2 ? _repairLogEntries : _eventLogEntries;
        UpdateExpandedLogLabels();
        UpdateExpandedLogLayout();
    }

    private void UpdateExpandedLogLabels()
    {
        _expandedLogTitle.Text = _pageIndex == 2 ? (_english ? "Repair log" : "Reparaturprotokoll")
            : (_english ? "Event log" : "Ereignisprotokoll");
        _expandedLogCount.Text = _english ? $"{_expandedLogList.Items.Count} entries · newest first"
            : $"{_expandedLogList.Items.Count} Einträge · neueste zuerst";
        _expandedLogHint.Text = _english ? "Close: click the small log list again, × or Esc."
            : "Schließen: kleine Logliste erneut anklicken, × oder Esc.";
        _expandedLogEmpty.Text = _english ? "No entries available yet." : "Noch keine Protokolleinträge vorhanden.";
        _expandedLogEmpty.Visibility = _expandedLogList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateExpandedLogLayout()
    {
        if (_expandedLogPanel is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        var standard = SelectedTheme() == "Standard";
        var anchor = standard ? (FrameworkElement)StandardStatusLogBar : FooterLayout;
        var top = anchor.TranslatePoint(new Point(), Root).Y;
        if (top <= 100) return;
        var width = Math.Max(280, Math.Min(ActualWidth - 48, 1000));
        var height = Math.Max(120, Math.Min(ActualWidth < 1000 ? 294 : 350, top - Root.RowDefinitions[0].ActualHeight - 20));
        _expandedLogPanel.Width = width;
        _expandedLogPanel.Height = height;
        _expandedLogPanel.Margin = new Thickness(0);
        if (_expandedLogTheme != SelectedTheme())
        {
            _expandedLogTheme = SelectedTheme();
            _expandedLogPanel.Background = standard
                ? new LinearGradientBrush(System.Windows.Media.Color.FromRgb(6, 45, 75), System.Windows.Media.Color.FromRgb(0, 27, 48), 90)
                : (Brush)Resources["CardSurfaceBrush"];
        }
    }

    private void SetExpandedLogOpen(bool open, bool animate = true)
    {
        if (_expandedLogPanel is null) return;
        var version = ++_expandedLogAnimationVersion;
        _expandedLogOpen = open;
        UpdateExpandedLogContext();
        var transform = (TranslateTransform)_expandedLogPanel.RenderTransform;
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        _expandedLogPanel.BeginAnimation(OpacityProperty, null);
        _expandedLogPanel.IsHitTestVisible = open;
        if (open)
        {
            _expandedLogPanel.Visibility = Visibility.Visible;
            _expandedLogPanel.Opacity = 1;
            if (_expandedLogList.Items.Count > 0) _expandedLogList.ScrollIntoView(_expandedLogList.Items[0]);
        }
        if (!animate)
        {
            _expandedLogPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        var duration = TimeSpan.FromMilliseconds(160);
        var slide = new DoubleAnimation(open ? 24 : 0, open ? 0 : 24, duration)
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var fade = new DoubleAnimation(open ? 0 : 1, open ? 1 : 0, duration);
        fade.Completed += (_, _) =>
        {
            if (version != _expandedLogAnimationVersion) return;
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            _expandedLogPanel.BeginAnimation(OpacityProperty, null);
            _expandedLogPanel.Opacity = 1;
            if (!open) _expandedLogPanel.Visibility = Visibility.Collapsed;
        };
        transform.BeginAnimation(TranslateTransform.YProperty, slide);
        _expandedLogPanel.BeginAnimation(OpacityProperty, fade);
    }
}
