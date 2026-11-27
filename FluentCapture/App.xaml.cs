using System.Threading;
using FluentSnip.Core;
using FluentSnip.Services;

namespace FluentSnip;

public partial class App : Application
{
    static Mutex mutex;
    static EventWaitHandle showSettings;
    static EventWaitHandle activateCapture;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool isActivate = e.Args.Any(a => string.Equals(a, "-activate", StringComparison.OrdinalIgnoreCase));
        mutex = new Mutex(true, @"Local\FluentSnip.SingleInstance.6B1A2F10", out bool first);
        showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\FluentSnip.ShowSettings.6B1A2F10");
        activateCapture = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\FluentSnip.Activate.6B1A2F10");
        if (!first)
        {
            // A second launch never shows settings when -activate was requested.
            if (isActivate) activateCapture.Set();
            else showSettings.Set();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, a) => { SettingsService.Log("Unhandled: " + a.Exception); a.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => SettingsService.Log("Fatal: " + a.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, a) => { SettingsService.Log("Task: " + a.Exception); a.SetObserved(); };

        SettingsService.Load();
        ThemeManager.Apply(SettingsService.Current.Theme);
        if (!SettingsService.IsWritable)
            MessageBox.Show("FluentSnip cannot write to its own folder:\n" + SettingsService.Dir +
                "\n\nSettings are stored next to the .exe so the app stays portable. Move FluentSnip to a writable folder " +
                "(not Program Files) or your settings will not be saved.", "FluentSnip", MessageBoxButton.OK, MessageBoxImage.Warning);
        AppController.Start();

        // Command-line: -activate starts the screenshot/recording selection overlay
        if (isActivate)
            Dispatcher.BeginInvoke(() => AppController.StartCapture(Views.OverlayIntent.Capture));

        // a second launch opens the settings window of this instance (lets you recover a hidden tray icon)
        var settingsThread = new Thread(() =>
        {
            while (showSettings.WaitOne()) Dispatcher.BeginInvoke(AppController.OpenSettings);
        }) { IsBackground = true };
        settingsThread.Start();

        // a second launch with -activate starts the capture overlay instead of settings
        var activateThread = new Thread(() =>
        {
            while (activateCapture.WaitOne()) Dispatcher.BeginInvoke(() => AppController.StartCapture(Views.OverlayIntent.Capture));
        }) { IsBackground = true };
        activateThread.Start();
    }
}
