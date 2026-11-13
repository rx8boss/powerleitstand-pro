using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PowerControlProWpf;

internal sealed partial class ProgramCatalogView
{
    private void ConfigureSearchClear()
    {
        CrossButtonAppearance.PreserveCustomSurface(ClearSearchButton);
        // The host excludes Close controls from its general neon button glow.
        ClearSearchButton.Name = "ProgramSearchClearClose";
        ClearSearchButton.Width = ClearSearchButton.Height = ClearSearchButton.MinHeight = ClearSearchButton.MinWidth = 20;
        ClearSearchButton.Padding = new(0); ClearSearchButton.Margin = new(0, 0, 4, 0);
        ClearSearchButton.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
              <Border Name="SmallClearFrame" CornerRadius="4" Background="Transparent">
                <Path Name="SmallClearGlyph" Data="M0,0 L10,10 M10,0 L0,10" Width="9" Height="9" Stretch="Uniform" Stroke="#FF4B4B" StrokeThickness="1.4" HorizontalAlignment="Center" VerticalAlignment="Center"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="SmallClearFrame" Property="Background" Value="#E92749"/><Setter TargetName="SmallClearGlyph" Property="Stroke" Value="White"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="SmallClearFrame" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/><Setter TargetName="SmallClearFrame" Property="BorderThickness" Value="1"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """);
    }
    private void ResetSearchScroll()
    {
        var query = _search.Text;
        Dispatcher.BeginInvoke(() => { if (_search.Text == query) _scroll.ScrollToTop(); }, DispatcherPriority.Loaded);
    }
    private CancellationTokenSource? _iconCancellation;
    private Task _iconWork = Task.CompletedTask;
    private void StopIconWork() => _iconCancellation?.Cancel();
    private void StartVisibleIconWork() { StopIconWork(); _iconWork = LoadVisibleIconsAsync(); }
    private async Task LeaveProgramAssetsAsync()
    {
        StopIconWork(); await _iconWork; await Assets.CleanupAsync();
    }
    private async Task LoadVisibleIconsAsync()
    {
        // All results can render immediately; asset failures must never keep the loading screen open.
        var entries = _visible.ToArray();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30)); _iconCancellation = limit;
        using var concurrency = new SemaphoreSlim(2);
        await Task.WhenAll(entries.Select(async entry =>
        {
            try
            {
                await concurrency.WaitAsync(limit.Token);
                try
                {
                    if (entry.Id.StartsWith("winget:", StringComparison.Ordinal) && Assets.Icon(entry.Id) is null)
                    { var package = await Service.ResolveAsync(entry, limit.Token); Assets.RegisterIcon(entry, package?.IconUrl, package?.IconWebsite); }
                    limit.Token.ThrowIfCancellationRequested();
                    await Assets.EnsureIconAsync(entry);
                }
                finally { concurrency.Release(); }
            }
            catch (Exception e) when (e is System.IO.IOException or System.Net.Http.HttpRequestException or OperationCanceledException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or InvalidOperationException)
            { WriteLog?.Invoke(entry.Name + ": " + UiText.Error(e, _english)); }
        }));
        if (_iconCancellation == limit) _iconCancellation = null;
    }
}
