using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ProcareDownloader.Services;

namespace ProcareDownloader;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        AppLog.Info($"Application started. Log file: {AppLog.LogPath}");
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Unhandled UI exception.", e.Exception);
    }

    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        AppLog.Error("Unhandled AppDomain exception.", e.ExceptionObject as Exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Error("Unobserved task exception.", e.Exception);
    }
}
