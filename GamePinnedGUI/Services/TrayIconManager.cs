using System;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;
using GamePinned.GUI.Helpers;

namespace GamePinned.GUI.Services
{
    public class TrayIconManager : IDisposable
    {
        private NotifyIcon? _notifyIcon;
        public static TrayIconManager? Instance { get; private set; }

        public void Initialize()
        {
            Instance = this;

            // Load custom icon for better visibility
            var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
            Icon customIcon;
            try
            {
                customIcon = new Icon(iconPath);
            }
            catch
            {
                // Fallback to system shield icon if custom icon fails to load
                customIcon = SystemIcons.Shield;
            }

            _notifyIcon = new NotifyIcon
            {
                Icon = customIcon,
                Visible = true,
                Text = StringConstants.ProtectionActiveMessage
            };

            var contextMenu = new ContextMenuStrip();

            var openItem = new ToolStripMenuItem("Open GamePinned", null, (s, e) => ShowMainWindow());
            openItem.Font = new System.Drawing.Font(openItem.Font, System.Drawing.FontStyle.Bold);
            contextMenu.Items.Add(openItem);

            var statusItem = new ToolStripMenuItem("Service Status: Checking...")
            {
                Enabled = false
            };
            contextMenu.Items.Add(statusItem);

            contextMenu.Items.Add(new ToolStripSeparator());

            var exitItem = new ToolStripMenuItem("Exit", null, (s, e) => ExitApplication());
            contextMenu.Items.Add(exitItem);

            _notifyIcon.ContextMenuStrip = contextMenu;
            _notifyIcon.DoubleClick += (s, e) => ShowMainWindow();
            _notifyIcon.Tag = statusItem;
        }

        public void UpdateServiceStatus(bool isRunning)
        {
            if (_notifyIcon?.Tag is ToolStripMenuItem statusItem)
                statusItem.Text = isRunning ? "Service Status: Running" : "Service Status: Stopped";

            if (_notifyIcon != null)
                _notifyIcon.Text = isRunning
                    ? StringConstants.ProtectionActiveMessage
                    : StringConstants.ProtectionInactiveMessage;
        }

        /// <summary>
        /// Show a non-intrusive Windows tray balloon tip. Used by UninstallBlocked
        /// notifications so the user understands why Steam appeared to "crash" or why
        /// Epic/Riot showed a generic "file access error" message. Native Windows
        /// notification: shows in the system tray notification area, doesn't escape
        /// the GamePinned window context, doesn't steal focus.
        /// </summary>
        public void ShowBalloonTip(string title, string message, int timeoutMs = 5000)
        {
            if (_notifyIcon == null) return;
            try
            {
                _notifyIcon.BalloonTipTitle = title;
                _notifyIcon.BalloonTipText = message;
                _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
                _notifyIcon.ShowBalloonTip(timeoutMs);
            }
            catch { /* tray icon may have been disposed during shutdown — harmless */ }
        }

        private void ShowMainWindow()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                // Find existing MainWindow or show if hidden
                var windows = Application.Current.Windows.OfType<MainWindow>().ToList();
                var window = windows.FirstOrDefault();

                if (window == null)
                {
                    // Should not happen if ShutdownMode is OnExplicitShutdown and we keep window reference
                    // But if it was closed, recreate it (rare)
                    window = new MainWindow();
                    window.Show();
                }
                else
                {
                    bool wasHidden = !window.IsVisible;

                    if (!window.IsVisible)
                        window.Show();

                    if (window.WindowState == WindowState.Minimized)
                        window.WindowState = WindowState.Normal;

                    window.Activate();
                    window.Topmost = true;  // Brief topmost to bring to front
                    window.Topmost = false;
                    window.Focus();

                    if (wasHidden)
                    {
                        _ = window.RefreshAllData();
                    }
                }
            });
        }

        private void ExitApplication()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var mainWindow = Application.Current.MainWindow as MainWindow;
                if (mainWindow != null)
                {
                    // Show the window first so dialog has an owner
                    if (!mainWindow.IsVisible)
                        mainWindow.Show();

                    mainWindow.RequestExit();
                }
                else
                {
                    Application.Current.Shutdown();
                }
            });
        }

        public void Dispose()
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
            Instance = null;
        }
    }
}
