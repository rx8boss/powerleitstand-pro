using System.Windows;
using PowerControlProWpf;

// Pumping the dispatcher must not start the production App.OnStartup workflow
// (single-instance activation, autostart repair or update checks).
internal sealed class IsolatedTestApp : App
{
    protected override void OnStartup(StartupEventArgs e) { }
}
