using ManageUsers.Services;

namespace ManageUsers.App;

/// <summary>
/// Entry point. With --apply-settings the app is the elevated, headless writer Save starts:
/// it writes the pending settings to machine settings and exits without opening a window.
/// Otherwise it starts the WinUI app as the generated entry point would.
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (PrefsElevation.ApplySettingsPath(args) is { } payload)
            return SettingsPayload.Apply(payload, RegistrySettingsSource.Policy(),
                values => MachineSettingsStore.Save(values, RegistrySettingsSource.Policy()));

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
