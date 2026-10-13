using ManageUsers.Services;

namespace ManageUsers.App;

/// <summary>
/// Entry point. With --apply-settings the app is the elevated, headless writer Save starts:
/// it reads the pending settings from the named pipe, writes them to machine settings and
/// exits without opening a window.
/// Otherwise it starts the WinUI app as the generated entry point would.
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (PrefsElevation.IsApplySettings(args, out var pipeName))
        {
            var json = pipeName == null ? null : PrefsElevation.ReceivePayload(pipeName, PrefsElevation.ConnectTimeout);
            return SettingsPayload.Apply(json, RegistrySettingsSource.Policy(),
                values => MachineSettingsStore.Save(values, RegistrySettingsSource.Policy()));
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }
}
