using System.Threading;
using System.Windows;

namespace LootPulse;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    // Named mutex held for the app's lifetime so the installer (Inno Setup AppMutex) can detect a
    // running instance and prompt to close it before upgrading. Must match AppMutex in LootPulse.iss.
    internal const string InstanceMutexName = "LootPulse.Overlay.SingleInstance";
    private static Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName);
        base.OnStartup(e);
    }
}

