namespace PowerControlProWpf;

public partial class MainWindow
{
    private bool _repairHasMeasuredProgress;

    private void UpdateRepairProgressAnimation()
    {
        // A measured zero is different from waiting for the first engine measurement.
        RepairProgress.ShowPercentage = _repairHasMeasuredProgress;
        RepairProgress.IsIndeterminate = _repairRunning && !_repairHasMeasuredProgress;
        RepairProgress.IsRunning = _repairRunning;
    }

    private void StopRepairProgressAnimation() => RepairProgress.IsRunning = false;
}
