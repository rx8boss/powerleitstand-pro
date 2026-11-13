using System.Windows;
using System.Windows.Controls;
using Panel = System.Windows.Controls.Panel;
using Button = System.Windows.Controls.Button;

namespace PowerControlProWpf;

internal sealed partial class ProgramCatalogView
{
    internal DeveloperPackagesPane? DeveloperPane { get; private set; }
    private DeveloperPackages? _developerPackages;
    private Button? _packagesButton;
    internal async Task OpenDeveloperPackagesAsync()
    {
        if (IsWorking || HostBusy() || SelectionOpen || _refreshing || _searching) return;
        SelectionOpen = true; CloseInfo(); ClearFinishedProgress(); CloseAdditionalCategories(); RefreshAvailability();
        await PauseBackgroundInspectionAsync();
        if (!IsLoaded || HostBusy()) { SelectionOpen = false; RefreshAvailability(); return; }
        DeveloperPane = new(this, _developerPackages ??= new(), CloseDeveloperPackages, active =>
        { IsWorking = active; _phase = active ? L("Paketinstallation läuft", "Package installation running") : ""; WorkStateChanged?.Invoke(active); RefreshAvailability(); });
        Grid.SetRowSpan(DeveloperPane, 5); Panel.SetZIndex(DeveloperPane, 2100); _root.Children.Add(DeveloperPane);
        await DeveloperPane.LoadAsync();
    }
    internal void CloseDeveloperPackages()
    {
        if (IsWorking || DeveloperPane is null) return;
        DeveloperPane.Stop(); _root.Children.Remove(DeveloperPane); DeveloperPane = null;
        SelectionOpen = false; RefreshAvailability();
    }
}
