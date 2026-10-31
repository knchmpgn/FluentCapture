using System.Threading;
using FluentSnip.Core;
using FluentSnip.Services;

namespace FluentSnip;

public partial class App : Application
{
    static Mutex mutex;
    static EventWaitHandle showSettings;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        mutex = new Mutex(true, @"Local\FluentSnip.SingleInstance.6B1A2F10", out bool first);
        showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\FluentSnip.ShowSettings.6B1A2F10");
        if (!first) { showSettings.Set(); Shutdown(); return; }

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

        // a second launch opens the settings window of this instance (lets you recover a hidden tray icon)
        var t = new Thread(() =>
        {
            while (showSettings.WaitOne()) Dispatcher.BeginInvoke(AppController.OpenSettings);
        }) { IsBackground = true };
        t.Start();
    }
}
