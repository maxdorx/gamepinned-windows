using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media.Animation;
using GamePinned.GUI.Services;
using MessageBox = System.Windows.MessageBox;

namespace GamePinned.GUI
{
    public partial class App : System.Windows.Application
    {
        private static Mutex? _mutex;
        private TrayIconManager? _trayManager;
        private static readonly string CrashLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GamePinned", "gui-crash.log");

        public App()
        {
            // Set up crash logging as early as possible
            DispatcherUnhandledException += (s, args) =>
            {
                LogCrash("DispatcherUnhandledException", args.Exception);
                args.Handled = true; // Prevent silent crash, show a plain-language dialog instead
                MessageBox.Show(
                    $"The settings window ran into a problem and needs to close.\n\nYour protected games are not affected, they stay locked either way.\n\nDetails were saved to:\n{CrashLogPath}",
                    "GamePinned",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                LogCrash("UnhandledException", args.ExceptionObject as Exception);
            };
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                base.OnStartup(e);

                // Ensure single instance
                const string mutexName = "GamePinnedGUI_SingleInstance";
                _mutex = new Mutex(true, mutexName, out bool createdNew);

                if (!createdNew)
                {
                    // Another instance is already running - just exit silently
                    // No need to notify user (first instance is already in tray)
                    Shutdown();
                    return;
                }

                // Keep app running even when main window is hidden (tray mode)
                ShutdownMode = ShutdownMode.OnExplicitShutdown;

                // Initialize system tray
                try
                {
                    _trayManager = new TrayIconManager();
                    _trayManager.Initialize();
                }
                catch (Exception trayEx)
                {
                    LogCrash("TrayInit", trayEx);
                    // Continue anyway, tray might fail but UI might work
                }

                // Show Main Window (unless minimized to tray)
                try
                {
                    bool startMinimized = false;
                    foreach (var arg in e.Args)
                    {
                        if (arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase))
                        {
                            startMinimized = true;
                            break;
                        }
                    }

                    var mainWindow = new MainWindow();

                    if (!startMinimized)
                    {
                        mainWindow.Opacity = 0;
                        mainWindow.Show();
                        mainWindow.WindowState = WindowState.Normal;
                        mainWindow.Topmost = true;
                        mainWindow.Activate();
                        mainWindow.Topmost = false;
                        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300));
                        mainWindow.BeginAnimation(Window.OpacityProperty, fadeIn);
                    }
                    // else: app starts hidden in tray
                }
                catch (Exception winEx)
                {
                    LogCrash("MainWindowInit", winEx);
                    MessageBox.Show(
                        $"The settings window could not open.\n\nYour protected games are not affected, they stay locked either way.\n\nDetails were saved to:\n{CrashLogPath}",
                        "GamePinned",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    Shutdown();
                    return;
                }
                
            }
            catch (Exception ex)
            {
                LogCrash("OnStartup", ex);
                MessageBox.Show(
                    $"The settings window could not start.\n\nYour protected games are not affected, they stay locked either way.\n\nDetails were saved to:\n{CrashLogPath}",
                    "GamePinned",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Shutdown();
            }
        }

        private static void LogCrash(string source, Exception? ex)
        {
            try
            {
                var dir = Path.GetDirectoryName(CrashLogPath);
                if (dir != null) Directory.CreateDirectory(dir);
                File.AppendAllText(CrashLogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex}\n\n");
            }
            catch { }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                _trayManager?.Dispose();
                _mutex?.ReleaseMutex();
                _mutex?.Dispose();
            }
            catch { }
            base.OnExit(e);
        }
    }
}
