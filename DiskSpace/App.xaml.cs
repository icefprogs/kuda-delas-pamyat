namespace DiskSpace;

public partial class App : Application
{
    private Mutex? instance;
    private EventWaitHandle? activate;
    private RegisteredWaitHandle? activationListener;

    protected override void OnStartup(StartupEventArgs args)
    {
        if (!args.Args.Contains("--demo"))
        {
            var account = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            var prefix = @"Local\KudaDelasPamyat." + account;
            activate = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + ".Activate");
            instance = new Mutex(true, prefix + ".Instance", out var ownsInstance);
            if (!ownsInstance)
            {
                activate.Set();
                Shutdown();
                return;
            }
            activationListener = ThreadPool.RegisterWaitForSingleObject(activate,
                (_, _) => Dispatcher.BeginInvoke(() => (MainWindow as DiskSpace.MainWindow)?.ActivateFromLaunch()),
                null, Timeout.Infinite, executeOnlyOnce: false);
        }
        base.OnStartup(args);
    }

    protected override void OnExit(ExitEventArgs args)
    {
        activationListener?.Unregister(null);
        activate?.Dispose();
        instance?.Dispose();
        base.OnExit(args);
    }
}
