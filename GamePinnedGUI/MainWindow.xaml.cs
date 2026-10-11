using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GamePinned.GUI.Services;
using GamePinned.GUI.Helpers;
using GamePinned.Shared;
using GamePinned.Shared.Models;
using Microsoft.Win32;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Shapes;

namespace GamePinned.GUI
{
    public partial class MainWindow : Window
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        private static readonly string _debugLogDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GamePinned", "logs");

        private static void DebugLog(string message)
        {
            try
            {
                System.IO.Directory.CreateDirectory(_debugLogDir);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(_debugLogDir, $"debug_{DateTime.Now:yyyyMMdd}.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [GUI] {message}\n");
            }
            catch { }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            int trueValue = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref trueValue, Marshal.SizeOf(typeof(int)));
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref trueValue, Marshal.SizeOf(typeof(int)));
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        }

        // Win32 constants
        private const int WM_GETMINMAXINFO   = 0x0024;
        private const int WM_NCLBUTTONDBLCLK = 0x00A3;
        private const int HTCAPTION          = 2;
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
        [DllImport("user32.dll")] private static extern bool   GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT  { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }

        // MaxHeight/MaxWidth: WPF-level safety net so WindowState.Maximized via button never covers taskbar.
        private void UpdateWorkAreaConstraints()
        {
            MaxWidth  = SystemParameters.WorkArea.Width;
            MaxHeight = SystemParameters.WorkArea.Height;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // WM_GETMINMAXINFO: constrain native maximize (snap-to-maximize, Win+Up) to work area.
            // Uses physical pixel values from MonitorFromWindow — no DPI conversion needed.
            // handled=true prevents WindowChrome from overwriting with wrong DIP-based values.
            // This does NOT affect WM_NCLBUTTONDBLCLK (different message → double-click still works).
            if (msg == WM_GETMINMAXINFO)
            {
                var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (monitor != IntPtr.Zero)
                {
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                    if (GetMonitorInfo(monitor, ref mi))
                    {
                        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                        mmi.ptMaxPosition.x = Math.Abs(mi.rcWork.left   - mi.rcMonitor.left);
                        mmi.ptMaxPosition.y = Math.Abs(mi.rcWork.top    - mi.rcMonitor.top);
                        mmi.ptMaxSize.x     = mi.rcWork.right  - mi.rcWork.left;
                        mmi.ptMaxSize.y     = mi.rcWork.bottom - mi.rcWork.top;
                        Marshal.StructureToPtr(mmi, lParam, false);
                        handled = true;
                    }
                }
                return IntPtr.Zero;
            }

            // WM_NCLBUTTONDBLCLK: double-click on caption area → toggle maximize/restore.
            // WindowChrome's own handler for this is unreliable; handle it explicitly.
            if (msg == WM_NCLBUTTONDBLCLK && wParam.ToInt32() == HTCAPTION)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                handled = true;
                return IntPtr.Zero;
            }

            return IntPtr.Zero;
        }

        private readonly ServiceCommunicator _serviceCommunicator;
        private readonly UpdateChecker _updateChecker;
        private readonly RefreshThrottler _refreshThrottler;
        private ObservableCollection<GameDisplayInfo> _games;
        private ObservableCollection<GameDisplayInfo> _epicGames;
        private ObservableCollection<GameDisplayInfo> _riotGames;
        private ObservableCollection<string> _blockedUsers;
        private string _adminPasswordHash = "";
        private bool _forceClose = false;
        private bool _isAuthenticated = false;
        private string _hardwareId = "";
        private bool _autoUpdateEnabled = true;
        private bool _protocolMismatchNotified = false; // Show mismatch warning once per session
        private bool _blockAllNonAdminByDefault = true;
        private int _loadingGeneration = 0; // Incremented on every ShowLoading/HideLoading; cancels stale InvokeAsync progress callbacks
        private bool _suppressProgressUpdates = false; // Suppresses service ProtectionProgressChanged during user-initiated lock/unlock

        // Search / collection views
        private ICollectionView _gamesView = null!;
        private ICollectionView _epicGamesView = null!;
        private ICollectionView _riotGamesView = null!;
        private string _steamSearch = "";
        private string _epicSearch = "";
        private string _riotSearch = "";

        // Pulse animation
        private Storyboard? _pulseStoryboard;

        public MainWindow()
        {
            InitializeComponent();
            _serviceCommunicator = new ServiceCommunicator();
            _updateChecker = new UpdateChecker();
            _refreshThrottler = new RefreshThrottler(500); // 500ms debounce
            _games = new ObservableCollection<GameDisplayInfo>();
            _epicGames = new ObservableCollection<GameDisplayInfo>();
            _riotGames = new ObservableCollection<GameDisplayInfo>();
            _blockedUsers = new ObservableCollection<string>();

            // Set up filterable collection views for search
            _gamesView = CollectionViewSource.GetDefaultView(_games);
            _gamesView.Filter = o => string.IsNullOrEmpty(_steamSearch) ||
                ((GameDisplayInfo)o).GameName.Contains(_steamSearch, StringComparison.OrdinalIgnoreCase);

            _epicGamesView = CollectionViewSource.GetDefaultView(_epicGames);
            _epicGamesView.Filter = o => string.IsNullOrEmpty(_epicSearch) ||
                ((GameDisplayInfo)o).GameName.Contains(_epicSearch, StringComparison.OrdinalIgnoreCase);

            _riotGamesView = CollectionViewSource.GetDefaultView(_riotGames);
            _riotGamesView.Filter = o => string.IsNullOrEmpty(_riotSearch) ||
                ((GameDisplayInfo)o).GameName.Contains(_riotSearch, StringComparison.OrdinalIgnoreCase);

            GamesDataGrid.ItemsSource = _gamesView;
            EpicGamesDataGrid.ItemsSource = _epicGamesView;
            RiotGamesDataGrid.ItemsSource = _riotGamesView;
            BlockedUsersListBox.ItemsSource = _blockedUsers;

            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var infoVerAttr = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(asm);
            var ver = infoVerAttr?.InformationalVersion?.Split('+')[0]
                      ?? asm.GetName().Version?.ToString()
                      ?? "Unknown";
            CurrentVersionText.Text = "v" + ver;

            Loaded += MainWindow_Loaded;
            
            // Hook restart request from Service (Session 0 fix)
            _serviceCommunicator.OnRestartLauncherRequested += path =>
            {
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        if (System.IO.File.Exists(path))
                        {
                            Process.Start("explorer.exe", $"\"{path}\""); // De-elevate to user session
                        }
                    }
                    catch (Exception ex) { DebugLog($"[PipeNotify] {ex}"); }
                });
            };

            // Hook UninstallBlocked notifications — show a tray balloon tip so the user
            // understands why Steam was closed (we killed it intentionally) or why
            // Epic/Riot showed a generic "file access error" message. Balloon tip is
            // native Windows, non-intrusive, and stays in the tray notification area
            // (doesn't escape the GamePinned window context).
            _serviceCommunicator.UninstallBlocked += (platformId, gameName) =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        // Same message on every platform — keep it short.
                        // "Action logged" tells the cafe owner the event is recorded
                        // (visible in the GUI Logs tab and forwarded to the backend).
                        var message = $"GamePinned blocked the uninstall of {gameName}. Action logged.";

                        TrayIconManager.Instance?.ShowBalloonTip(
                            "Uninstall Blocked",
                            message);
                    }
                    catch (Exception ex) { DebugLog($"[UninstallBlocked] {ex.Message}"); }
                });
            };

            // Hook per-game progress notifications — update loading dialog text in real time.
            // Suppressed during user-initiated lock/unlock (service iterates ALL games, not just selected ones).
            // Generation check discards stale callbacks from a previous operation that arrive after HideLoading.
            _serviceCommunicator.ProtectionProgressChanged += text =>
            {
                int gen = _loadingGeneration;
                Dispatcher.InvokeAsync(() =>
                {
                    if (GlobalLoadingDialog.IsOpen && _loadingGeneration == gen && !_suppressProgressUpdates)
                        GlobalLoadingText.Text = text;
                });
            };
        }

        private void ShowLoading(string message)
        {
            _loadingGeneration++;
            GlobalLoadingText.Text = message;
            GlobalLoadingDialog.IsOpen = true;
        }

        private void HideLoading()
        {
            _loadingGeneration++; // Invalidate any pending InvokeAsync callbacks before closing
            GlobalLoadingDialog.IsOpen = false;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Constrain to work area so maximizing never covers the taskbar
            UpdateWorkAreaConstraints();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.Invoke(UpdateWorkAreaConstraints);

            // Start listening for service notifications
            _serviceCommunicator.StartNotificationListener();

            // Parallelize startup tasks to improve performance
            var serviceStatusTask = _serviceCommunicator.GetServiceStatusAsync();
            var hwIdTask = Task.Run(() => CacheManager.GetHardwareId());
            var passwordHashTask = _serviceCommunicator.GetAdminPasswordHashAsync();

            await Task.WhenAll(serviceStatusTask, hwIdTask, passwordHashTask);

            // Auto-start service if not running
            if (!serviceStatusTask.Result)
            {
               await _serviceCommunicator.StartServiceAsync();
            }

            // Check Startup Registry Key
            try { StartWithWindowsCheckBox.IsChecked = GetStartupEnabled(); } catch (Exception ex) { DebugLog($"[StartupCheck] {ex}"); }

            // Display hardware ID (masked until authenticated)
            _hardwareId = hwIdTask.Result ?? "ERROR: Could not generate hardware ID";
            HardwareIdTextBox.Text = _isAuthenticated ? _hardwareId : new string('•', Math.Min(_hardwareId.Length, 64));
            DebugLog($"[HardwareId] Generated, length={_hardwareId.Length}, displayed={(_isAuthenticated ? "real" : "masked")}");


            // Process admin password
            _adminPasswordHash = passwordHashTask.Result;
            UpdatePasswordStatus();

            // Log hash details for debugging — preview only (not full value)
            var hashLen = _adminPasswordHash?.Length ?? 0;
            var hashPreview = hashLen > 0 ? _adminPasswordHash!.Substring(0, Math.Min(40, hashLen)) : "(empty)";
            var hashHasNewline = _adminPasswordHash?.Contains('\n') == true || _adminPasswordHash?.Contains('\r') == true;
            DebugLog($"[Auth] AdminPasswordHash len={hashLen}, preview='{hashPreview}', hasNewline={hashHasNewline}, format: {(_adminPasswordHash?.StartsWith("v2:") == true ? "v2/PBKDF2" : string.IsNullOrEmpty(_adminPasswordHash) ? "empty" : "legacy/SHA256")}");

            if (string.IsNullOrEmpty(_adminPasswordHash))
            {
                DebugLog("[Auth] No password set — showing initial setup dialog");
                // Force user to set an initial admin password
                var setupDialog = new PasswordDialog("", "Set an admin password to lock down the settings panel.");
                setupDialog.Title = "Initial Setup";
                setupDialog.Owner = this;

                while (string.IsNullOrEmpty(_adminPasswordHash))
                {
                    if (setupDialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(setupDialog.EnteredPassword))
                    {
                        var hash = PasswordDialog.HashPassword(setupDialog.EnteredPassword);
                        var success = await _serviceCommunicator.SetAdminPasswordAsync(hash);
                        if (success)
                        {
                            _adminPasswordHash = hash;
                            _isAuthenticated = true;
                            DebugLog("[Auth] Initial password set successfully");
                            DialogHelper.Show("Password saved.", "Setup Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                            UpdatePasswordStatus();
                            break;
                        }
                        else
                        {
                            DebugLog("[Auth] SetAdminPasswordAsync failed");
                            var failMsg = _serviceCommunicator.IsServiceInstalled()
                                ? "Could not save the password. Make sure the GamePinned service is running, then try again."
                                : "GamePinned's background service isn't installed, so nothing can be saved yet. Reinstall GamePinned and try again.";
                            DialogHelper.Show(failMsg, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            // Recreate dialog, ShowDialog() cannot be called on a closed window
                            setupDialog = new PasswordDialog("", "Set an admin password to lock down the settings panel.");
                            setupDialog.Title = "Initial Setup";
                            setupDialog.Owner = this;
                        }
                    }
                    else
                    {
                        DialogHelper.Show("Set an admin password to continue.", "Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                        setupDialog = new PasswordDialog("", "Set an admin password to lock down the settings panel.");
                        setupDialog.Title = "Initial Setup";
                        setupDialog.Owner = this;
                    }
                }
            }
            else
            {
                DebugLog("[Auth] Showing login dialog");
                // Admin password is set - prompt for authentication
                var dialog = new PasswordDialog(_adminPasswordHash, "Enter your admin password to access GamePinned:");
                dialog.Owner = this;
                bool authDialogResult = false;
                try
                {
                    var authResult = dialog.ShowDialog();
                    authDialogResult = authResult == true && dialog.IsAuthenticated;
                    DebugLog($"[Auth] ShowDialog returned: result={authResult}, IsAuthenticated={dialog.IsAuthenticated}");
                }
                catch (Exception authEx)
                {
                    DebugLog($"[Auth] CRASH in ShowDialog or VerifyPassword: {authEx}");
                    throw;
                }
                _isAuthenticated = authDialogResult;
                DebugLog($"[Auth] _isAuthenticated = {_isAuthenticated}");
                if (_isAuthenticated)
                {
                    if (dialog.NewPasswordHash != null)
                    {
                        // Password was reset via Windows identity verification.
                        // Write directly to config.json — service may not be running at login time.
                        _adminPasswordHash = dialog.NewPasswordHash;
                        UpgradePasswordHashIfNeeded(dialog.NewPasswordHash);
                        DebugLog("[Auth] Password reset via Windows identity verification");
                        UpdatePasswordStatus();
                    }
                }
            }

            DebugLog("[Auth] Calling UpdateUILockState");
            try { UpdateUILockState(); } catch (Exception ex) { DebugLog($"[Auth] CRASH in UpdateUILockState: {ex}"); throw; }
            DebugLog("[Auth] Calling CacheManager.LoadCache");
            try { CacheManager.LoadCache(); } catch (Exception ex) { DebugLog($"[Auth] CRASH in CacheManager.LoadCache: {ex}"); throw; }
            DebugLog("[Auth] Calling RefreshAllData");
            // (RefreshAllData has its own internal catch — any crash here goes to DispatcherUnhandledException)
            await RefreshAllData();
            DebugLog("[Auth] RefreshAllData complete");
            UpdateMaximizeIcon();

            _updateChecker.ConsumeUpdateFlag();

            _serviceCommunicator.OnServiceStatusChanged += (isRunning) =>
            {
                Dispatcher.Invoke(async () =>
                {
                    await _refreshThrottler.ThrottleAsync(async () => await RefreshAllData());
                });
            };
        }

        #region Navigation

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton rb) return;
            var tag = rb.Tag?.ToString() ?? "";

            // Hide all pages
            if (PageDashboard != null) PageDashboard.Visibility = Visibility.Collapsed;
            if (PageSteam != null) PageSteam.Visibility = Visibility.Collapsed;
            if (PageEpic != null) PageEpic.Visibility = Visibility.Collapsed;
            if (PageRiot != null) PageRiot.Visibility = Visibility.Collapsed;
            if (PageUsers != null) PageUsers.Visibility = Visibility.Collapsed;
            if (PageLicense != null) PageLicense.Visibility = Visibility.Collapsed;
            if (PageLogs != null) PageLogs.Visibility = Visibility.Collapsed;
            if (PageSettings != null) PageSettings.Visibility = Visibility.Collapsed;
            if (PageNvidia != null) PageNvidia.Visibility = Visibility.Collapsed;

            // Show selected page with fade-in
            UIElement? target = tag switch
            {
                "Dashboard" => PageDashboard,
                "Steam" => PageSteam,
                "Epic" => PageEpic,
                "Riot" => PageRiot,
                "Users" => PageUsers,
                "License" => PageLicense,
                "Logs" => PageLogs,
                "Settings" => PageSettings,
                "Nvidia" => PageNvidia,
                _ => null
            };
            if (target != null)
            {
                var tt = new TranslateTransform(0, 14);
                target.RenderTransform = tt;
                target.Visibility = Visibility.Visible;
                target.Opacity = 0;
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                target.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(200))) { EasingFunction = ease });
                tt.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(14, 0, new Duration(TimeSpan.FromMilliseconds(230))) { EasingFunction = ease });

                if (tag == "Dashboard")
                    AnimateDashboardEntrance();
            }
        }

        #endregion

        #region Window Chrome

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            UpdateMaximizeIcon();
        }

        private void UpdateMaximizeIcon()
        {
            if (MaximizeIcon == null) return;
            MaximizeIcon.Kind = WindowState == WindowState.Maximized
                ? MaterialDesignThemes.Wpf.PackIconKind.WindowRestore
                : MaterialDesignThemes.Wpf.PackIconKind.WindowMaximize;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            // Reuse existing closing logic (minimize to tray / ask for password)
            var args = new System.ComponentModel.CancelEventArgs();
            Window_Closing(this, args);
        }

        #endregion

        #region Search Filters

        private void SteamSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            _steamSearch = ((System.Windows.Controls.TextBox)sender).Text;
            _gamesView?.Refresh();
        }

        private void EpicSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            _epicSearch = ((System.Windows.Controls.TextBox)sender).Text;
            _epicGamesView?.Refresh();
        }

        private void RiotSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            _riotSearch = ((System.Windows.Controls.TextBox)sender).Text;
            _riotGamesView?.Refresh();
        }

        #endregion

        #region User Chip Remove

        private async void RemoveUserChip_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireAuth("remove users")) return;
            if (sender is not System.Windows.Controls.Button btn) return;
            var username = btn.Tag as string;
            if (string.IsNullOrEmpty(username)) return;

            var confirm = DialogHelper.Show(
                $"Remove '{username}'?\nThey'll be able to uninstall games.",
                "Confirm Removal", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirm == MessageBoxResult.Yes)
            {
                ShowLoading($"Removing user '{username}'...");
                var result = await _serviceCommunicator.RemoveBlockedUserAsync(username);
                HideLoading();
                if (result)
                    await RefreshBlockedUsers();
                else
                    DialogHelper.Show("Could not reach the service. Make sure it is running.", "Service Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        #endregion

        #region Pulse Animation

        private void StartPulseAnimation()
        {
            if (_pulseStoryboard != null) return;
            var anim = new DoubleAnimation
            {
                From = 1.0,
                To = 0.35,
                Duration = new Duration(TimeSpan.FromSeconds(1)),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(anim, ServiceStatusIndicator);
            Storyboard.SetTargetProperty(anim, new PropertyPath(UIElement.OpacityProperty));
            _pulseStoryboard = new Storyboard();
            _pulseStoryboard.Children.Add(anim);
            _pulseStoryboard.Begin(this, true);
        }

        private void StopPulseAnimation()
        {
            _pulseStoryboard?.Stop(this);
            _pulseStoryboard = null;
            if (ServiceStatusIndicator != null) ServiceStatusIndicator.Opacity = 1.0;
        }

        // Animate a SolidColorBrush held on a DependencyProperty to a new color.
        // First call (frozen XAML brush): sets a new unfrozen brush instantly.
        // Subsequent calls: animates the existing brush's Color property.
        private static void AnimateBrushTo(DependencyObject target, DependencyProperty prop, Color to, int durationMs = 200)
        {
            var anim = new ColorAnimation(to, new Duration(TimeSpan.FromMilliseconds(durationMs)))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            if (target.GetValue(prop) is SolidColorBrush brush && !brush.IsFrozen)
                brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
            else
                target.SetValue(prop, new SolidColorBrush(to));
        }

        // Slide a UIElement in from Y offset, fading from 0 opacity. GPU-accelerated — no layout work.
        private static void SlideIn(UIElement el, double fromY, int delayMs, int durationMs = 260)
        {
            el.Opacity = 0;
            var tt = new TranslateTransform(0, fromY);
            el.RenderTransform = tt;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = new Duration(TimeSpan.FromMilliseconds(durationMs));
            var fade = new DoubleAnimation(0, 1, dur) { EasingFunction = ease, BeginTime = TimeSpan.FromMilliseconds(delayMs) };
            var slide = new DoubleAnimation(fromY, 0, dur) { EasingFunction = ease, BeginTime = TimeSpan.FromMilliseconds(delayMs) };
            el.BeginAnimation(UIElement.OpacityProperty, fade);
            tt.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        private void AnimateDashboardEntrance()
        {
            // Staggered entrance: hero → stats → bottom row. 70ms apart.
            // Page itself is already animating via Nav_Checked — only animate children here.
            if (HeroBorder != null)     SlideIn(HeroBorder,    20, delayMs: 40);
            if (DashStatsGrid != null)  SlideIn(DashStatsGrid, 20, delayMs: 110);
            if (DashBottomGrid != null) SlideIn(DashBottomGrid, 20, delayMs: 180);
        }

        // Count-up animation for a stat TextBlock. Runs at ~30fps (33ms ticks) to keep layout work light.
        // Skips animation if text is not a plain integer (e.g. "All").
        private void AnimateStatCount(TextBlock tb, int toValue)
        {
            if (!int.TryParse(tb.Text, out int fromValue) || fromValue == toValue)
            {
                tb.Text = toValue.ToString();
                return;
            }

            const int durationMs = 520;
            const int intervalMs = 33; // ~30fps
            int steps = durationMs / intervalMs;
            int step = 0;
            var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(intervalMs)
            };
            timer.Tick += (_, _) =>
            {
                step++;
                if (step >= steps)
                {
                    tb.Text = toValue.ToString();
                    timer.Stop();
                    return;
                }
                // Ease-out: progress decelerates toward end
                double t = (double)step / steps;
                double eased = 1 - (1 - t) * (1 - t);
                tb.Text = ((int)Math.Round(fromValue + (toValue - fromValue) * eased)).ToString();
            };
            timer.Start();
        }

        #endregion

        #region Dashboard

        private void UpdateDashboardStats()
        {
            // Count locked games across ALL platforms
            var protectedCount = _games.Count(g => g.StatusText == "Locked") +
                                _epicGames.Count(g => g.StatusText == "Locked") +
                                _riotGames.Count(g => g.StatusText == "Locked");
            AnimateStatCount(StatProtectedCount, protectedCount);

            // Count total games across ALL platforms
            var totalCount = _games.Count + _epicGames.Count + _riotGames.Count;
            AnimateStatCount(StatTotalGamesCount, totalCount);

            // "All" is not a number — set directly; otherwise count-up animate
            if (_blockedUsers.Count == 0 && _blockAllNonAdminByDefault)
                StatUsersCount.Text = "All";
            else
                AnimateStatCount(StatUsersCount, _blockedUsers.Count);

            // Count active platforms (platforms with detected games)
            int activePlatforms = 0;
            if (_games.Count > 0) activePlatforms++;
            if (_epicGames.Count > 0) activePlatforms++;
            if (_riotGames.Count > 0) activePlatforms++;
            AnimateStatCount(StatPlatformsCount, activePlatforms);
        }

        private async System.Threading.Tasks.Task RefreshServiceStatus()
        {
            try
            {
                // Check installed-at-all separately from running, so a fresh/broken install
                // shows "Not Installed" instead of the misleading "Stopped" (which implies the
                // service exists and just needs a click on Start, when there's nothing to start).
                if (!_serviceCommunicator.IsServiceInstalled())
                {
                    ServiceStatusText.Text = "Not Installed";
                    AnimateBrushTo(ServiceStatusIndicator, Shape.FillProperty, Colors.Gray, 300);
                    StopPulseAnimation();
                    TrayIconManager.Instance?.UpdateServiceStatus(false);
                    UpdateUILockState();
                    return;
                }

                var isRunning = await _serviceCommunicator.GetServiceStatusAsync();

                ServiceStatusText.Text = isRunning ? "Running" : "Stopped";
                AnimateBrushTo(ServiceStatusIndicator, Shape.FillProperty,
                    isRunning ? Color.FromRgb(0x4C, 0xAF, 0x50) : Color.FromRgb(0xE5, 0x39, 0x35), 300);

                if (isRunning) StartPulseAnimation(); else StopPulseAnimation();

                TrayIconManager.Instance?.UpdateServiceStatus(isRunning);

                // Update button states based on service status AND auth
                UpdateUILockState();
            }
            catch (Exception ex)
            {
                ServiceStatusText.Text = "Error";
                AnimateBrushTo(ServiceStatusIndicator, Shape.FillProperty, Colors.Gray, 300);
            }
        }

        #endregion

        #region License

        /// Maps internal license type strings to premium display names shown in the UI.
        private static string GetTierDisplayName(string licenseType) => licenseType switch
        {
            "Subscription" => StringConstants.TierPro,
            "Perpetual"    => StringConstants.TierElite,
            "Trial"        => StringConstants.TierTrial,
            _              => StringConstants.TierFree
        };

        /// Styles the sidebar tier badge based on the display tier name.
        private void SetHeroTint(bool maintenanceOn)
        {
            HeroBorder.Background = maintenanceOn
                ? new SolidColorBrush(Color.FromRgb(0x1E, 0x00, 0x00))  // dark red
                : new SolidColorBrush(Color.FromRgb(0x13, 0x13, 0x13));  // default
            HeroBorder.BorderBrush = maintenanceOn
                ? new SolidColorBrush(Color.FromRgb(0x3D, 0x00, 0x00))  // red border
                : new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24)); // default
        }

        private void SetTierBadgeStyle(string tierDisplay)
        {
            switch (tierDisplay)
            {
                case "ELITE":
                    // Gold — one-time purchase
                    TierBadge.Background   = new SolidColorBrush(Color.FromRgb(0x25, 0x1A, 0x02));
                    TierBadge.BorderBrush  = new SolidColorBrush(Color.FromRgb(0x7A, 0x5C, 0x08));
                    LicenseTierText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F));
                    TierDot.Fill           = new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F));
                    break;
                case "PRO":
                    // Blue — subscription
                    TierBadge.Background   = new SolidColorBrush(Color.FromRgb(0x03, 0x1E, 0x2E));
                    TierBadge.BorderBrush  = new SolidColorBrush(Color.FromRgb(0x0D, 0x5E, 0x8A));
                    LicenseTierText.Foreground = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7));
                    TierDot.Fill           = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7));
                    break;
                case "TRIAL":
                    // Amber — time-limited trial
                    TierBadge.Background   = new SolidColorBrush(Color.FromRgb(0x26, 0x16, 0x00));
                    TierBadge.BorderBrush  = new SolidColorBrush(Color.FromRgb(0x8B, 0x5E, 0x00));
                    LicenseTierText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x00));
                    TierDot.Fill           = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x00));
                    break;
                default:
                    // Muted grey — free tier (readable but clearly subordinate to paid)
                    TierBadge.Background   = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
                    TierBadge.BorderBrush  = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
                    LicenseTierText.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
                    TierDot.Fill           = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
                    break;
            }
        }

        private async System.Threading.Tasks.Task RefreshLicenseStatus()
        {
            try
            {
                var (hwId, status, type, expiry) = await _serviceCommunicator.GetLicenseInfoAsync();

                var tierDisplay = GetTierDisplayName(status == StringConstants.Active ? type : "Free");

                LicenseStatusText.Text = status;
                LicenseTypeText.Text = tierDisplay;
                LicenseExpiryText.Text = expiry.HasValue ? expiry.Value.ToString("yyyy-MM-dd") : "∞";
                DashLicenseStatus.Text = tierDisplay;
                LicenseTierText.Text = tierDisplay;
                SetTierBadgeStyle(tierDisplay);

                LicenseStatusText.Foreground = status switch
                {
                    "Active" => new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),  // Green
                    "Expired" => new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)),  // Red
                    "Hardware Mismatch" => new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00)),  // Orange
                    "Invalid" => new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)),  // Red
                    _ => new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88))  // Gray
                };
            }
            catch (Exception ex)
            {
                LicenseStatusText.Text = "Error";
                LicenseTypeText.Text = "--";
                LicenseExpiryText.Text = "--";
                DashLicenseStatus.Text = "Error";
            }
        }

        private async void CopyHardwareId_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireAuth("copy the Hardware ID")) { DebugLog("[HardwareId] Copy blocked — not authenticated"); return; }
            try
            {
                // WPF clipboard can fail if another process is locking it. Retry up to 10 times.
                for (int i = 0; i < 10; i++)
                {
                    try
                    {
                        Clipboard.SetText(_hardwareId);
                        DialogHelper.Show("Hardware ID copied.", "Copied", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                    catch (Exception)
                    {
                        await Task.Delay(50);
                    }
                }
                DialogHelper.Show("Could not copy. Another application is using the clipboard.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                DialogHelper.Show("Could not copy to clipboard. Try again.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ActivateLicense_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireAuth("activate a license")) return;
            var licenseKey = LicenseKeyTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(licenseKey))
            {
                DialogHelper.Show("Need a key to activate.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                // Show loading indicator
                ShowLoading("Activating license...");
                ActivateLicenseButton.IsEnabled = false;
                LicenseKeyTextBox.IsEnabled = false;

                var (success, message) = await _serviceCommunicator.ActivateOnlineAsync(licenseKey);
                
                if (success)
                {
                    DialogHelper.Show("License activated. All features unlocked.", "Activated", MessageBoxButton.OK, MessageBoxImage.Information);
                    LicenseKeyTextBox.Clear();
                    await RefreshAllData();
                }
                else
                {
                    // Fallback: If it's already a signed blob (offline key), try direct local activation
                    if (licenseKey.Length > 100)
                    {
                        var localResult = await _serviceCommunicator.ActivateLicenseAsync(licenseKey);
                        if (localResult)
                        {
                            DialogHelper.Show("License activated. All features unlocked.", "Activated", MessageBoxButton.OK, MessageBoxImage.Information);
                            await RefreshAllData();
                            return;
                        }
                    }
                    DialogHelper.Show(message, "Activation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                DialogHelper.Show("Activation failed. Check your connection and try again.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                HideLoading();
                ActivateLicenseButton.IsEnabled = true;
                LicenseKeyTextBox.IsEnabled = true;
            }
        }

        #endregion

        #region Data Loading

        /// <summary>
        /// Efficient batch data refresh using new GetAllData endpoint
        /// Replaces multiple separate service calls with single optimized call
        /// </summary>
        internal async Task RefreshAllData()
        {
            try
            {
                ShowLoading("Loading data...");
                
                var response = await _serviceCommunicator.SendCommandAsync(PipeMessage.CreateCommand(PipeCommand.GetAllData));
                if (response != null && response.StartsWith("RSP|OK"))
                {
                    var dataBase64 = response.Split('|')[2];
                    var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(dataBase64));

                    // Use source-generated JSON context for Native AOT compatibility
                    var refreshData = System.Text.Json.JsonSerializer.Deserialize(json, GamePinned.Shared.Models.AppJsonContext.Default.RefreshData);

                    if (refreshData != null)
                    {
                        // Update all UI components in batch
                        UpdateUIFromRefreshData(refreshData);

                        // Protocol version mismatch: service and GUI are on different versions.
                        // This happens when the auto-updater updates the service but the user
                        // hasn't restarted the GUI yet. Show once per session.
                        if (!_protocolMismatchNotified
                            && refreshData.ProtocolVersion != 0
                            && refreshData.ProtocolVersion != GamePinned.Shared.PipeConstants.PROTOCOL_VERSION)
                        {
                            _protocolMismatchNotified = true;
                            DialogHelper.Show(
                                "GamePinned was updated in the background. Restart the app to finish updating.",
                                "Restart Required",
                                System.Windows.MessageBoxButton.OK,
                                System.Windows.MessageBoxImage.Information);
                        }

                        // Refresh NVIDIA tab status (separate call — not part of main batch)
                        var nvidiaStatus = await _serviceCommunicator.GetNvidiaStatusAsync();
                        if (nvidiaStatus != null)
                            UpdateNvidiaUI(nvidiaStatus);
                    }
                }
                else
                {
                    // Fallback to individual calls if batch endpoint fails
                    // Hide current loading before showing progressive loading
                    HideLoading();
                    await RefreshAllDataFallback();
                }
            }
            catch (Exception ex)
            {
                // Fallback to individual calls on error
                HideLoading();
                await RefreshAllDataFallback();
            }
            finally
            {
                HideLoading();
            }
        }

        /// <summary>
        /// Fallback method using individual service calls
        /// </summary>
        private async Task RefreshAllDataFallback()
        {
            // Simple sequential refresh without progressive loading
            // This is only used if the batch endpoint fails
            await Task.WhenAll(
                RefreshLicenseStatus(),
                RefreshServiceStatus(),
                RefreshGames(),
                RefreshBlockedUsers(),
                RefreshLogs()
            );
            UpdateDashboardStats();
        }

        /// <summary>
        /// Update UI from batch refresh data
        /// </summary>
        private void UpdateUIFromRefreshData(RefreshData data)
        {
            // Update games list (all platforms)
            var newSteamGames = new List<GameDisplayInfo>();
            var newEpicGames = new List<GameDisplayInfo>();
            var newRiotGames = new List<GameDisplayInfo>();
            // Only FREE tier has 1-game limit. Perpetual, Subscription, and Trial are unlimited.
            bool isFreeTier = data.LicenseInfo.LicenseType.Equals("Free", StringComparison.OrdinalIgnoreCase);
            int protectedCount = 0;

            // Process ALL games across all platforms
            // Free tier limit (1 game) applies globally
            foreach (var game in data.Games)
            {
                // Use backend's IsProtected flag (set based on actual NTFS deny rules)
                bool isActuallyProtected = game.IsProtected;
                string statusText;

                if (game.IsExcluded)
                {
                    statusText = StringConstants.Exempt;
                }
                else if (isActuallyProtected)
                {
                    statusText = StringConstants.Protected;
                    protectedCount++;
                }
                else
                {
                    // Not protected and not exempt = user hasn't selected it yet
                    statusText = "Unlocked";
                }

                var gameDisplay = new GameDisplayInfo();
                gameDisplay.GameName = game.GameName;
                gameDisplay.PlatformId = game.PlatformId;
                gameDisplay.InstallPath = game.InstallPath;
                gameDisplay.StatusText = statusText;
                gameDisplay.IsProtected = isActuallyProtected;
                gameDisplay.IsExcluded = game.IsExcluded;

                // Add to appropriate platform list
                if (game.PlatformId == StringConstants.Steam)
                    newSteamGames.Add(gameDisplay);
                else if (game.PlatformId == StringConstants.Epic)
                    newEpicGames.Add(gameDisplay);
                else if (game.PlatformId == StringConstants.Riot)
                    newRiotGames.Add(gameDisplay);
            }

            // Update Steam games
            _games.Clear();
            foreach (var game in newSteamGames)
                _games.Add(game);
            if (SteamEmptyState != null)
                SteamEmptyState.Visibility = newSteamGames.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Update Epic games
            _epicGames.Clear();
            foreach (var game in newEpicGames)
                _epicGames.Add(game);
            if (EpicEmptyState != null)
                EpicEmptyState.Visibility = newEpicGames.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Update Riot games
            _riotGames.Clear();
            foreach (var game in newRiotGames)
                _riotGames.Add(game);
            if (RiotEmptyState != null)
                RiotEmptyState.Visibility = newRiotGames.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Update license info
            var tierDisplay = GetTierDisplayName(data.LicenseInfo.LicenseType);
            var licenseStatus = data.LicenseInfo.IsValid ? StringConstants.Active : StringConstants.Free;
            LicenseStatusText.Text = licenseStatus;
            LicenseTypeText.Text = tierDisplay;
            LicenseExpiryText.Text = data.LicenseInfo.ExpiryDate.HasValue ? data.LicenseInfo.ExpiryDate.Value.ToString("yyyy-MM-dd") : "∞";
            _hardwareId = data.LicenseInfo.HardwareId ?? _hardwareId;
            HardwareIdTextBox.Text = _isAuthenticated ? _hardwareId : new string('•', Math.Min(_hardwareId.Length, 64));
            DebugLog($"[HardwareId] Refreshed from service, displayed={(_isAuthenticated ? "real" : "masked")}");

            // Update dashboard and sidebar
            DashLicenseStatus.Text = tierDisplay;
            if (!data.NvidiaPresent)
            {
                DashNvidiaStatus.Text = "No GPU detected";
                DashNvidiaStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
            }
            else
            {
                DashNvidiaStatus.Text = data.NvidiaAutoRestore ? "Auto-restore ON" : "Auto-restore OFF";
                DashNvidiaStatus.Foreground = new SolidColorBrush(data.NvidiaAutoRestore
                    ? Color.FromRgb(0x4C, 0xAF, 0x50)   // green
                    : Color.FromRgb(0x55, 0x55, 0x55));  // dim
            }

            // Update sidebar tier badge
            LicenseTierText.Text = tierDisplay;
            SetTierBadgeStyle(tierDisplay);

            // Update service status
            ServiceStatusText.Text = data.ServiceRunning ? StringConstants.Running : StringConstants.Stopped;
            AnimateBrushTo(ServiceStatusIndicator, Shape.FillProperty,
                data.ServiceRunning ? Color.FromRgb(0x4C, 0xAF, 0x50) : Color.FromRgb(0xE5, 0x39, 0x35), 300);
            if (data.ServiceRunning) StartPulseAnimation(); else StopPulseAnimation();

            // Update users
            _blockAllNonAdminByDefault = data.BlockAllNonAdminByDefault;
            _blockedUsers.Clear();
            foreach (var user in data.BlockedUsers) _blockedUsers.Add(user);
            if (NoUsersHint != null)
                NoUsersHint.Visibility = data.BlockedUsers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Update logs with colored rendering
            SetLogContent(data.RecentLogs.TakeLast(500));

            // Update settings - startup and auto-update are paid-tier features
            bool isPaidTier = data.LicenseInfo.IsValid;
            StartupPaidBadge.Visibility = isPaidTier ? Visibility.Collapsed : Visibility.Visible;
            StartWithWindowsCheckBox.IsChecked = isPaidTier && GetStartupEnabled();
            AutoUpdatePaidBadge.Visibility = isPaidTier ? Visibility.Collapsed : Visibility.Visible;
            _autoUpdateEnabled = isPaidTier && data.AutoUpdateEnabled;
            AutoUpdateToggle.IsChecked = isPaidTier && data.AutoUpdateEnabled;

            // Update maintenance mode without triggering the event handlers
            SetMaintenanceToggle(data.MaintenanceMode);

            MaintenanceModeText.Text = data.MaintenanceMode
                ? "Locks disabled. Admins can uninstall games."
                : "Locks active. Toggle to allow uninstalls.";
            MaintenanceModeText.Foreground = data.MaintenanceMode
                ? new SolidColorBrush(Color.FromRgb(0xE6, 0x00, 0x12))  // Red
                : new SolidColorBrush(Color.FromRgb(85, 85, 85));        // Dim
            SetHeroTint(data.MaintenanceMode);

            // Update dashboard stats
            StatProtectedCount.Text = data.Stats.ProtectedGamesCount.ToString();
            StatTotalGamesCount.Text = data.Stats.TotalGamesCount.ToString();
            StatUsersCount.Text = data.Stats.BlockedUsersCount == 0 && data.BlockAllNonAdminByDefault ? "All" : data.Stats.BlockedUsersCount.ToString();
            StatPlatformsCount.Text = data.Stats.ActivePlatformsCount.ToString();

            // Update tray icon
            TrayIconManager.Instance?.UpdateServiceStatus(data.ServiceRunning);

            // Update NVIDIA tab paid badge and auto-restore toggle state from config
            bool nvidiaIsPaid = data.LicenseInfo.IsValid;
            NvidiaPaidBanner.Visibility = nvidiaIsPaid ? Visibility.Collapsed : Visibility.Visible;
            NvidiaAutoRestoreToggle.IsEnabled = nvidiaIsPaid;

            // Detach event, set toggle, re-attach (prevents spurious config push during refresh)
            NvidiaAutoRestoreToggle.Click -= NvidiaAutoRestore_Click;
            NvidiaAutoRestoreToggle.IsChecked = data.NvidiaAutoRestore;
            NvidiaAutoRestoreToggle.Click += NvidiaAutoRestore_Click;

            // Sync button enabled states now that service status is known
            UpdateUILockState();
        }

        #endregion

        #region Games (All Platforms)

        private bool _isRefreshingGames = false;
        private async System.Threading.Tasks.Task RefreshGames()
        {
            if (_isRefreshingGames) return;
            _isRefreshingGames = true;
            try
            {
                var gamesTask = _serviceCommunicator.GetProtectedGamesAsync();
                var licenseTask = _serviceCommunicator.GetLicenseInfoAsync();

                await Task.WhenAll(gamesTask, licenseTask);

                var games = gamesTask.Result;
                var (hwId, status, type, expiry) = licenseTask.Result;

                bool isFreeTier = status != "Active";
                int protectedCount = 0;

                var newSteamGames = new List<GameDisplayInfo>();
                var newEpicGames = new List<GameDisplayInfo>();
                var newRiotGames = new List<GameDisplayInfo>();

                // Process all games across all platforms
                // Free tier limit (1 game) applies globally, not per-platform
                foreach (var game in games)
                {
                    bool isActuallyProtected = !game.IsExcluded;
                    string statusText = isActuallyProtected ? "Locked" : "Unlocked";

                    if (isFreeTier && isActuallyProtected)
                    {
                        if (protectedCount >= 1)
                        {
                            statusText = "Unlocked (Free Limit)";
                            isActuallyProtected = false; // Treat as exempt for GUI purposes
                        }
                        else
                        {
                            protectedCount++;
                        }
                    }

                    var displayGame = new GameDisplayInfo
                    {
                        GameName = game.GameName,
                        PlatformId = game.PlatformId,
                        InstallPath = game.InstallPath,
                        IsExcluded = !isActuallyProtected,
                        StatusText = statusText
                    };

                    if (game.PlatformId == "steam")
                        newSteamGames.Add(displayGame);
                    else if (game.PlatformId == "epic")
                        newEpicGames.Add(displayGame);
                    else if (game.PlatformId == "riot")
                        newRiotGames.Add(displayGame);
                }

                // Update Steam games
                SyncGameList(_games, newSteamGames);

                // Update Epic games
                SyncGameList(_epicGames, newEpicGames);

                // Update Riot games
                SyncGameList(_riotGames, newRiotGames);

                UpdateDashboardStats();
            }
            catch (Exception ex)
            {
                DialogHelper.Show("Could not load games. Make sure the service is running.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isRefreshingGames = false;
            }
        }

        private void SyncGameList(ObservableCollection<GameDisplayInfo> collection, List<GameDisplayInfo> newList)
        {
            // Sync to avoid flashing
            for (int i = 0; i < newList.Count; i++)
            {
                if (i < collection.Count)
                {
                    var existing = collection[i];
                    var incoming = newList[i];
                    if (existing.GameName != incoming.GameName || existing.StatusText != incoming.StatusText)
                    {
                        collection[i] = incoming;
                    }
                }
                else
                {
                    collection.Add(newList[i]);
                }
            }
            while (collection.Count > newList.Count)
            {
                collection.RemoveAt(collection.Count - 1);
            }
        }

        private async void RefreshGames_Click(object sender, RoutedEventArgs e)
        {
            await _refreshThrottler.ThrottleAsync(async () => await RefreshAllData());
        }

        private async void ClearSession_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireAuth("clear platform session")) return;

            var btn = sender as System.Windows.Controls.Button;
            var platformId = btn?.Tag?.ToString() ?? "";
            var platformName = platformId switch
            {
                "steam" => "Steam",
                "epic" => "Epic Games",
                "riot" => "Riot Games",
                _ => platformId
            };

            var confirm = DialogHelper.Show(
                $"This will close {platformName} and delete all saved login data for all accounts on this PC.\n\nThe next person to open {platformName} will need to sign in fresh.\n\nContinue?",
                $"Clear {platformName} Session",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);

            if (confirm != System.Windows.MessageBoxResult.Yes) return;

            ShowLoading($"Clearing {platformName} session...");
            if (btn != null) btn.IsEnabled = false;
            try
            {
                var success = await _serviceCommunicator.ClearPlatformSessionAsync(platformId);
                HideLoading();
                if (success)
                    DialogHelper.Show($"{platformName} session cleared. The next launch will show a fresh login screen.", "Session Cleared", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                else
                    DialogHelper.Show($"Failed to clear {platformName} session. Make sure the service is running.", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
            finally
            {
                HideLoading();
                if (btn != null) btn.IsEnabled = true;
            }
        }

        private async void ProtectGame_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireAuth("change game lock status")) return;

            // Determine which platform tab is active and get selected items
            string platformId = "steam";
            System.Collections.IList selectedItems = GamesDataGrid.SelectedItems;
            if (PageEpic.Visibility == Visibility.Visible)
            {
                platformId = "epic";
                selectedItems = EpicGamesDataGrid.SelectedItems;
            }
            else if (PageRiot.Visibility == Visibility.Visible)
            {
                platformId = "riot";
                selectedItems = RiotGamesDataGrid.SelectedItems;
            }

            if (selectedItems.Count == 0)
            {
                DialogHelper.Show("Select at least one game.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Get game names for batch operation
            var gameNames = selectedItems.Cast<GameDisplayInfo>().Select(g => g.GameName).ToList();

            // Check FREE tier limit (use cached status from UI instead of re-fetching)
            string status = LicenseStatusText.Text;
            if (status != "Active" && selectedItems.Count > 1)
            {
                DialogHelper.Show("Free plan supports 1 game. Upgrade to lock more.", "Upgrade Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Check if free tier already has a protected game (avoid wasteful protect/unprotect cycle)
            if (status != "Active")
            {
                try
                {
                    var config = Config.Load(@"C:\ProgramData\GamePinned\config.json");
                    var existingProtected = config.Platforms.SelectMany(p => p.ProtectedGames).ToList();

                    if (existingProtected.Count > 0 && !existingProtected.All(g => gameNames.Contains(g)))
                    {
                        DialogHelper.Show(
                            $"Free plan: 1 game only.\n\n'{existingProtected.First()}' is currently locked. Unlock it first.",
                            "Upgrade Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    DebugLog($"[ProtectGame] Config.Load failed: {ex.Message}");
                }
            }

            string message = selectedItems.Count == 1
                ? $"Lock '{((GameDisplayInfo)selectedItems[0]).GameName}'?"
                : $"Lock {selectedItems.Count} selected games on {platformId.ToUpper()}?";

            var confirm = DialogHelper.Show(message, "Confirm Lock", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            string lockMsg = gameNames.Count == 1
                ? $"Locking: {gameNames[0]}..."
                : $"Locking {gameNames.Count} games...";
            ShowLoading(lockMsg);

            // Batch update — suppress service progress (it covers ALL games, not just selected ones)
            _suppressProgressUpdates = true;
            int addedCount = await _serviceCommunicator.ProtectAllGamesAsync(platformId, gameNames);
            _suppressProgressUpdates = false;

            HideLoading();
            await RefreshAllData();  // Use full refresh to get all platforms (Steam, Epic, Riot)

            if (addedCount < 0)
                DialogHelper.Show("Could not reach the service. Make sure it is running.", "Service Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            else if (addedCount > 0)
                DialogHelper.Show("Done. Games locked.", "Lock Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                DialogHelper.Show("No changes. Games may already be locked.", "No Changes", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private async void ExemptGame_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireAuth("change game lock status")) return;

            // Determine which platform tab is active and get selected items
            string platformId = "steam";
            System.Collections.IList selectedItems = GamesDataGrid.SelectedItems;
            if (PageEpic.Visibility == Visibility.Visible)
            {
                platformId = "epic";
                selectedItems = EpicGamesDataGrid.SelectedItems;
            }
            else if (PageRiot.Visibility == Visibility.Visible)
            {
                platformId = "riot";
                selectedItems = RiotGamesDataGrid.SelectedItems;
            }

            if (selectedItems.Count == 0)
            {
                DialogHelper.Show("Select at least one game.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string message = selectedItems.Count == 1
                ? $"Unlock '{((GameDisplayInfo)selectedItems[0]).GameName}'?\nUsers can uninstall it."
                : $"Unlock {selectedItems.Count} games on {platformId.ToUpper()}?\nUsers can uninstall them.";

            var confirm = DialogHelper.Show(message, "Confirm Unlock", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            var gameNames = selectedItems.Cast<GameDisplayInfo>().Select(g => g.GameName).ToList();
            string unlockMsg = gameNames.Count == 1
                ? $"Unlocking: {gameNames[0]}..."
                : $"Unlocking {gameNames.Count} games...";
            ShowLoading(unlockMsg);

            // Batch update — suppress service progress (it covers ALL games, not just selected ones)
            _suppressProgressUpdates = true;
            int addedCount = await _serviceCommunicator.ExemptAllGamesAsync(platformId, gameNames);
            _suppressProgressUpdates = false;

            HideLoading();
            await RefreshAllData();  // Use full refresh to get all platforms (Steam, Epic, Riot)

            if (addedCount < 0)
                DialogHelper.Show("Could not reach the service. Make sure it is running.", "Service Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            else if (addedCount > 0)
                DialogHelper.Show("Done. Games unlocked.", "Unlock Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                DialogHelper.Show("No changes. Games may already be unlocked.", "No Changes", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        #endregion

        #region Users

        private async System.Threading.Tasks.Task RefreshBlockedUsers()
        {
            try
            {
                var users = await _serviceCommunicator.GetBlockedUsersAsync();
                _blockedUsers.Clear();
                foreach (var user in users)
                    _blockedUsers.Add(user);
                UpdateDashboardStats();
            }
            catch (Exception ex)
            {
                DialogHelper.Show("Could not load users. Make sure the service is running.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void AddUser_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireAuth("add users")) return;
            var username = AddUserTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(username))
            {
                DialogHelper.Show("Username required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ShowLoading($"Adding user '{username}'...");
            try
            {
                var result = await _serviceCommunicator.AddBlockedUserAsync(username);
                HideLoading();
                if (result)
                {
                    AddUserTextBox.Clear();
                    await RefreshBlockedUsers();
                    DialogHelper.Show($"'{username}' added to blocked users.", "User Added", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    DialogHelper.Show("Could not reach the service. Make sure it is running.", "Service Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                HideLoading();
                DialogHelper.Show("Could not add user. Make sure the service is running.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Logs

        private async System.Threading.Tasks.Task RefreshLogs()
        {
            try
            {
                var logs = await _serviceCommunicator.GetLogsAsync(100);
                SetLogContent(logs);
            }
            catch (Exception ex)
            {
                SetLogContent(new[] { $"Error loading logs: {ex.Message}" });
            }
        }

        private void SetLogContent(IEnumerable<string> lines)
        {
            var doc = new FlowDocument { PagePadding = new Thickness(0), LineHeight = 18 };
            var para = new Paragraph { Margin = new Thickness(0) };
            bool first = true;
            foreach (var line in lines)
            {
                if (!first) para.Inlines.Add(new LineBreak());
                first = false;
                var run = new Run(line) { Foreground = GetLogLineBrush(line) };
                para.Inlines.Add(run);
            }
            doc.Blocks.Add(para);
            LogsTextBox.Document = doc;
            LogsTextBox.ScrollToEnd();
        }

        private static System.Windows.Media.Brush GetLogLineBrush(string line)
        {
            if (line.Contains("BLOCKED") || line.Contains(" ERR") || line.Contains("Error") || line.Contains("[CRASH]"))
                return new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50));   // red
            if (line.Contains("MAINT") || line.Contains("MAINTENANCE") || line.Contains("WARNING"))
                return new SolidColorBrush(Color.FromRgb(0xFF, 0xB7, 0x4D));   // orange
            if (line.Contains("License") || line.Contains("ACTIV") || line.Contains("CloudSync"))
                return new SolidColorBrush(Color.FromRgb(0x64, 0xB5, 0xF6));   // blue
            if (line.Contains("ALLOWED") || line.Contains("Locked") || line.Contains("[NEW") || line.Contains("protected"))
                return new SolidColorBrush(Color.FromRgb(0x81, 0xC7, 0x84));   // green
            return new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));       // default grey
        }

        private async void RefreshLogs_Click(object sender, RoutedEventArgs e)
        {
            await _refreshThrottler.ThrottleAsync(async () => await RefreshAllData());
        }

        private async void ClearLogs_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireAuth("clear logs")) return;

            var result = DialogHelper.Show("Are you sure you want to clear all logs?", "Clear Logs", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            try
            {
                var cleared = await _serviceCommunicator.ClearLogsAsync();
                if (!cleared)
                {
                    DialogHelper.Show("Could not reach the service. Make sure it is running.", "Service Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                LogsTextBox.Document.Blocks.Clear();
                await RefreshLogs();
            }
            catch (Exception ex)
            {
                DialogHelper.Show("Could not clear logs.", "Error");
            }
        }

        #endregion

        #region Settings

        private async void StartService_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireAuth("control the service")) return;
            try
            {
                ShowLoading("Starting service...");
                var result = await _serviceCommunicator.StartServiceAsync();
                HideLoading();
                if (result)
                {
                    await RefreshServiceStatus();
                    DialogHelper.Show("Service started. Protection is active.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    DialogHelper.Show("Failed to start service. Is GamePinned installed?", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                HideLoading();
                DialogHelper.Show("Could not start service.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void StartWithWindows_Click(object sender, RoutedEventArgs e)
        {
            // Paid-tier feature check
            if (LicenseStatusText.Text != StringConstants.Active)
            {
                DialogHelper.Show("Launch with Windows requires an ELITE or PRO license.", "Pro Feature", MessageBoxButton.OK, MessageBoxImage.Information);
                StartWithWindowsCheckBox.IsChecked = !StartWithWindowsCheckBox.IsChecked;
                return;
            }

            // Protect this setting
            if (!RequireAuth("modify startup settings"))
            {
                // Revert checkbox if auth failed
                StartWithWindowsCheckBox.IsChecked = !StartWithWindowsCheckBox.IsChecked;
                return;
            }

            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (exePath == null) throw new Exception("Executable path could not be determined.");

                bool enable = StartWithWindowsCheckBox.IsChecked == true;
                bool success = await _serviceCommunicator.ToggleStartupAsync(enable, exePath);

                if (!success)
                {
                    DialogHelper.Show("Failed to update startup settings via GamePinned Service.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    StartWithWindowsCheckBox.IsChecked = !StartWithWindowsCheckBox.IsChecked;
                }
                else
                {
                    // Service wrote HKLM\Run. Also update HKCU\StartupApproved — Windows Settings
                    // reads this key to show enabled/disabled state. Service can't touch user HKCU.
                    SetStartupApproved(enable);
                }
            }
            catch (Exception ex)
            {
                DialogHelper.Show("Could not update startup setting.", "Error");
                // Revert checkbox if failed
                StartWithWindowsCheckBox.IsChecked = !StartWithWindowsCheckBox.IsChecked;
            }
        }

        private void AuthBanner_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_isAuthenticated)
            {
                _isAuthenticated = false;
                UpdateUILockState();
                UpdatePasswordStatus();
            }
            else
            {
                var dialog = new PasswordDialog(_adminPasswordHash, "Enter your admin password to unlock full access:");
                dialog.Owner = this;
                var authResult = dialog.ShowDialog();

                if (authResult == true && dialog.IsAuthenticated)
                {
                    _isAuthenticated = true;
                    UpdateUILockState();
                    UpdatePasswordStatus();
                    if (dialog.NewPasswordHash != null)
                    {
                        // Write directly to config.json — service may not be running at login time.
                        _adminPasswordHash = dialog.NewPasswordHash;
                        UpgradePasswordHashIfNeeded(dialog.NewPasswordHash);
                        DebugLog("[Auth] Password reset via Windows identity verification (AuthBanner)");
                    }
                }
            }
        }

        private async void StopService_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireAuth("stop the service")) return;
            var confirm = DialogHelper.Show(
                "This disables all game protection.",
                "Confirm Stop",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (confirm == MessageBoxResult.Yes)
            {
                try
                {
                    ShowLoading("Stopping service...");
                    var result = await _serviceCommunicator.StopServiceAsync();
                    HideLoading();
                    if (result)
                    {
                        await RefreshServiceStatus();
                        DialogHelper.Show("Service stopped. Protection is off.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        DialogHelper.Show("Failed to stop service.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                catch (Exception ex)
                {
                    HideLoading();
                    DialogHelper.Show("Could not stop service.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void RestartService_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireAuth("restart the service")) return;
            var confirm = DialogHelper.Show(
                "Protection will be offline briefly during the restart.",
                "Confirm Restart",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirm == MessageBoxResult.Yes)
            {
                try
                {
                    ShowLoading("Stopping service...");
                    var stopResult = await _serviceCommunicator.StopServiceAsync();
                    if (!stopResult)
                    {
                        HideLoading();
                        DialogHelper.Show("Failed to stop service.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    ShowLoading("Waiting for service to stop...");
                    await Task.Delay(2000);

                    ShowLoading("Starting service...");
                    var startResult = await _serviceCommunicator.StartServiceAsync();
                    HideLoading();
                    if (startResult)
                    {
                        await RefreshServiceStatus();
                        DialogHelper.Show("Service restarted.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        DialogHelper.Show("Service stopped but failed to restart. Start it manually.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                catch (Exception ex)
                {
                    HideLoading();
                    DialogHelper.Show("Could not restart service.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void MaintenanceMode_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isAuthenticated)
            {
                SetMaintenanceToggle(!MaintenanceModeToggle.IsChecked ?? false);
                RequireAuth("toggle maintenance mode");
                return;
            }
            try
            {
                var enabled = MaintenanceModeToggle.IsChecked ?? false;

                _suppressProgressUpdates = true;
                ShowLoading(enabled ? "Unlocking everything..." : "Locking everything...");
                MaintenanceModeToggle.IsEnabled = false;

                var success = await _serviceCommunicator.ToggleMaintenanceModeAsync();

                if (!success)
                {
                    SetMaintenanceToggle(!enabled);
                    MaintenanceModeText.Text = !enabled ? "Locks disabled. Admins can uninstall games." : "Locks active. Toggle to allow uninstalls.";
                    MaintenanceModeText.Foreground = new System.Windows.Media.SolidColorBrush(
                        !enabled ? System.Windows.Media.Color.FromRgb(0xE6, 0x00, 0x12) : System.Windows.Media.Color.FromRgb(85, 85, 85));
                    SetHeroTint(!enabled);
                    DialogHelper.Show("Could not reach the service. Make sure it is running.", "Service Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                MaintenanceModeText.Text = enabled
                    ? "Locks disabled. Admins can uninstall games."
                    : "Locks active. Toggle to allow uninstalls.";
                MaintenanceModeText.Foreground = enabled
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE6, 0x00, 0x12))  // Red
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(85, 85, 85));        // Dim
                SetHeroTint(enabled);
            }
            catch (Exception ex)
            {
                DialogHelper.Show("Could not toggle maintenance mode.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                SetMaintenanceToggle(!(MaintenanceModeToggle.IsChecked ?? false));
            }
            finally
            {
                _suppressProgressUpdates = false;
                HideLoading();
                MaintenanceModeToggle.IsEnabled = true;
            }
        }

        /// <summary>
        /// Sets MaintenanceModeToggle.IsChecked without triggering the Checked/Unchecked event handlers.
        /// Always use this instead of direct assignment to avoid re-entrant handler loops.
        /// </summary>
        private void SetMaintenanceToggle(bool value)
        {
            MaintenanceModeToggle.Checked -= MaintenanceMode_Toggled;
            MaintenanceModeToggle.Unchecked -= MaintenanceMode_Toggled;
            MaintenanceModeToggle.IsChecked = value;
            MaintenanceModeToggle.Checked += MaintenanceMode_Toggled;
            MaintenanceModeToggle.Unchecked += MaintenanceMode_Toggled;
        }

        private async void SetAdminPassword_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireAuth("set a new password")) return;
            var password = AdminPasswordBox.Password;
            if (string.IsNullOrWhiteSpace(password))
            {
                DialogHelper.Show("Please enter a password.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (password.Length < 4)
            {
                DialogHelper.Show("Password must be at least 4 characters.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var hash = PasswordDialog.HashPassword(password);
            var result = await _serviceCommunicator.SetAdminPasswordAsync(hash);
            if (result)
            {
                _adminPasswordHash = hash;
                AdminPasswordBox.Clear();
                UpdatePasswordStatus();
                DialogHelper.Show("Password updated.", "Password Set", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                DialogHelper.Show("Could not reach the service. Make sure it is running.", "Service Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void UpdatePasswordStatus()
        {
            PasswordStatusText.Text = string.IsNullOrEmpty(_adminPasswordHash)
                ? "No password set. Settings are unprotected."
                : _isAuthenticated
                    ? "Admin access. All settings unlocked."
                    : "Locked. Enter admin password to make changes.";
        }

        private void UpdateUILockState()
        {
            // If not authenticated, disable all modification controls
            var canModify = _isAuthenticated;

            if (AuthStatusBanner != null)
            {
                var fgColor  = canModify ? Color.FromRgb(0x4C, 0xAF, 0x50) : Color.FromRgb(0xFF, 0x52, 0x52);
                var bgColor  = canModify ? Color.FromRgb(0x06, 0x1A, 0x0C) : Color.FromRgb(0x1E, 0x00, 0x00);
                var bdrColor = canModify ? Color.FromRgb(0x0D, 0x33, 0x18) : Color.FromRgb(0x3D, 0x00, 0x00);
                AnimateBrushTo(AuthStatusBanner, Border.BackgroundProperty,   bgColor);
                AnimateBrushTo(AuthStatusBanner, Border.BorderBrushProperty,  bdrColor);
                AnimateBrushTo(AuthBannerIcon,   System.Windows.Controls.Control.ForegroundProperty,  fgColor);
                AnimateBrushTo(AuthBannerText,   TextBlock.ForegroundProperty, fgColor);
                AuthBannerIcon.Kind = canModify
                    ? MaterialDesignThemes.Wpf.PackIconKind.LockOpenOutline
                    : MaterialDesignThemes.Wpf.PackIconKind.LockOutline;
                AuthBannerText.Text = canModify ? "ADMIN MODE (click to lock)" : "LOCKED (click to unlock)";
            }

            if (MaintenanceModeToggle != null) 
                MaintenanceModeToggle.IsEnabled = canModify;

            // Service buttons depend on service state AND auth.
            // We can read the current status from the text block (simple way)
            var isRunning = ServiceStatusText.Text == "Running";
            
            if (StartServiceButton != null) StartServiceButton.IsEnabled = canModify && !isRunning;
            if (StopServiceButton != null) StopServiceButton.IsEnabled = canModify && isRunning;
            if (RestartServiceButton != null) RestartServiceButton.IsEnabled = canModify && isRunning;

            if (ServiceStoppedNote != null)
                ServiceStoppedNote.Visibility = isRunning ? Visibility.Collapsed : Visibility.Visible;

            // Hardware ID: show real value only when authenticated
            if (HardwareIdTextBox != null && !string.IsNullOrEmpty(_hardwareId))
            {
                HardwareIdTextBox.Text = canModify ? _hardwareId : new string('•', Math.Min(_hardwareId.Length, 64));
                DebugLog($"[HardwareId] UI lock state changed, displayed={( canModify ? "real" : "masked")}");
            }

            // NVIDIA: Save button and auto-restore toggle require admin auth.
            // When locked, force-disable them. When unlocked, UpdateNvidiaUI restores the correct
            // enabled state (which also checks nvidiaPresent and isPaid).
            if (!canModify)
            {
                if (SaveNvidiaConfigButton != null)    SaveNvidiaConfigButton.IsEnabled    = false;
                if (NvidiaAutoRestoreToggle != null)   NvidiaAutoRestoreToggle.IsEnabled   = false;
            }
        }

        private bool RequireAuth(string action = "modify settings")
        {
            if (_isAuthenticated) return true;

            if (!string.IsNullOrEmpty(_adminPasswordHash))
            {
                 var dialog = new PasswordDialog(_adminPasswordHash, $"Enter your admin password to {action}:");
                 dialog.Owner = this;
                 var result = dialog.ShowDialog();
                 if (result == true && dialog.IsAuthenticated)
                 {
                     _isAuthenticated = true;
                     UpdatePasswordStatus();
                     UpdateUILockState();
                     if (dialog.NewPasswordHash != null)
                     {
                         // Write directly to config.json — service may not be running at login time.
                         _adminPasswordHash = dialog.NewPasswordHash;
                         UpgradePasswordHashIfNeeded(dialog.NewPasswordHash);
                         DebugLog("[Auth] Password reset via Windows identity verification (RequireAuth)");
                     }
                     return true;
                 }
            }

            return false;
        }

        private void UpgradePasswordHashIfNeeded(string? upgradedHash)
        {
            if (upgradedHash == null) return;
            _ = Task.Run(() =>
            {
                try
                {
                    // Write directly — GUI runs as admin, no IPC needed for a hash-only update.
                    // Service is not necessarily running at login time.
                    const string configPath = @"C:\ProgramData\GamePinned\config.json";
                    var config = GamePinned.Shared.Models.Config.Load(configPath);
                    config.AdminPasswordHash = upgradedHash;
                    config.Save(configPath);
                    Dispatcher.Invoke(() => _adminPasswordHash = upgradedHash);
                }
                catch (Exception ex) { DebugLog($"[PasswordUpgrade] {ex}"); }
            });
        }

        private static bool GetStartupEnabled()
        {
            // Entry must exist in HKLM\Run AND not be disabled in HKCU\StartupApproved
            using var runKey = Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
            if (runKey?.GetValue("GamePinned") == null) return false;

            using var approvedKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", false);
            var approvedVal = approvedKey?.GetValue("GamePinned") as byte[];
            // First byte 0x03 = disabled by Windows Settings; 0x02 or absent = enabled
            return approvedVal == null || approvedVal.Length == 0 || approvedVal[0] != 0x03;
        }

        private static void SetStartupApproved(bool enable)
        {
            try
            {
                using var approvedKey = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", true);
                if (enable)
                    approvedKey?.SetValue("GamePinned", new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
                else
                    approvedKey?.DeleteValue("GamePinned", false);
            }
            catch (Exception ex) { DebugLog($"[SetStartupApproved] {ex}"); }
        }

        private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ShowLoading("Checking for updates...");
                var (updateAvailable, version, downloadUrl, fileSize, releaseNotes, sha256) = await _updateChecker.CheckForUpdatesAsync();
                HideLoading();

                if (updateAvailable)
                {
                    // Format file size
                    string sizeStr = fileSize > 0 ? FormatFileSize(fileSize) : "Unknown size";

                    // Build update message
                    var message = $"Version {version} is now available ({sizeStr}).\n\n";
                    if (!string.IsNullOrWhiteSpace(releaseNotes))
                    {
                        message += "What's New:\n" + releaseNotes.Trim();
                    }
                    message += "\n\nDownload and install now?";

                    var result = DialogHelper.Show(message, "Update Available", MessageBoxButton.YesNo, MessageBoxImage.Information);

                    if (result == MessageBoxResult.Yes && !string.IsNullOrEmpty(downloadUrl))
                    {
                        await DownloadUpdateWithProgress(downloadUrl, sha256);
                    }
                }
                else
                {
                    DialogHelper.Show("You're running the latest version.", "No Updates", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                HideLoading();
                DialogHelper.Show("Update check failed. Check your connection.", "Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task DownloadUpdateWithProgress(string downloadUrl, string sha256 = "")
        {
            var progressDialog = new Window
            {
                Title = "Downloading Update",
                Width = 400,
                Height = 170,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                Background = new SolidColorBrush(Color.FromRgb(26, 26, 26))
            };

            var grid = new Grid { Margin = new Thickness(20) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var statusText = new TextBlock
            {
                Text = "Downloading...",
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(statusText, 0);

            var progressBar = new System.Windows.Controls.ProgressBar
            {
                Height = 25,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(progressBar, 1);

            var percentText = new TextBlock
            {
                Text = "0%",
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 12,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };
            Grid.SetRow(percentText, 2);

            grid.Children.Add(statusText);
            grid.Children.Add(progressBar);
            grid.Children.Add(percentText);

            progressDialog.Content = grid;

            var progress = new Progress<int>(percent =>
            {
                progressBar.Value = percent;
                percentText.Text = $"{percent}%";
            });

            progressDialog.Show();

            try
            {
                await _updateChecker.DownloadAndInstallAsync(downloadUrl, progress, sha256);
            }
            catch (Exception ex)
            {
                progressDialog.Close();
                DialogHelper.Show("Download failed. Check your connection and try again.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        #endregion

        #region Window Management

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_forceClose) return;

            // Minimize to tray instead of closing
            e.Cancel = true;
            Hide();
            
            // Log out user (reset auth state) so next open requires password
            if (!string.IsNullOrEmpty(_adminPasswordHash))
            {
                _isAuthenticated = false;
                UpdatePasswordStatus();
                UpdateUILockState();
            }

        }

        public bool RequestExit()
        {
            if (!string.IsNullOrEmpty(_adminPasswordHash))
            {
                var dialog = new PasswordDialog(_adminPasswordHash, "Enter your admin password to exit GamePinned:");
                dialog.Owner = this.IsVisible ? this : null;
                var result = dialog.ShowDialog();
                if (result != true || !dialog.IsAuthenticated)
                    return false;
                // If password was reset via Windows identity verification, save it synchronously
                // before shutdown — can't await here, direct write is the only safe option
                if (dialog.NewPasswordHash != null)
                {
                    _adminPasswordHash = dialog.NewPasswordHash;
                    try
                    {
                        const string configPath = @"C:\ProgramData\GamePinned\config.json";
                        var config = GamePinned.Shared.Models.Config.Load(configPath);
                        config.AdminPasswordHash = dialog.NewPasswordHash;
                        config.Save(configPath);
                        DebugLog("[Auth] Password reset saved before exit (Windows identity verification)");
                    }
                    catch (Exception ex) { DebugLog($"[Auth] Failed to save reset password before exit: {ex.Message}"); }
                }
            }

            _forceClose = true;
            StopPulseAnimation();
            Application.Current.Shutdown();
            return true;
        }

        private async void AutoUpdateToggle_Click(object sender, RoutedEventArgs e)
        {
            var enabled = AutoUpdateToggle.IsChecked == true;

            // Paid-tier feature check
            if (enabled && LicenseStatusText.Text != StringConstants.Active)
            {
                DialogHelper.Show("Automatic updates require an ELITE or PRO license.", "Pro Feature", MessageBoxButton.OK, MessageBoxImage.Information);
                AutoUpdateToggle.IsChecked = false;
                return;
            }

            if (!RequireAuth("modify update settings"))
            {
                AutoUpdateToggle.IsChecked = !enabled; // revert if auth failed
                return;
            }

            var success = await _serviceCommunicator.SetAutoUpdateEnabledAsync(enabled);
            if (!success)
            {
                AutoUpdateToggle.IsChecked = !enabled;
                DialogHelper.Show("Could not reach the service. Make sure it is running.", "Service Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
                _autoUpdateEnabled = enabled;
        }

        #endregion

        private async void SendDiagnosticReport_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireAuth("send a diagnostic report")) return;

            var confirm = DialogHelper.Show(
                "Send today's debug log to GamePinned support?",
                "Send Diagnostic Report",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            ShowLoading("Sending diagnostic report...");
            try
            {
                var (success, message) = await _serviceCommunicator.SendDiagnosticReportAsync();
                if (success)
                    DialogHelper.Show(message, "Report Sent", MessageBoxButton.OK, MessageBoxImage.Information);
                else
                    DialogHelper.Show(message, "Failed to Send", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                DialogHelper.Show("An unexpected error occurred. Contact support if this keeps happening.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                HideLoading();
            }
        }

        private void Website_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://gamepinned.com/support",
                    UseShellExecute = true
                });
            }
            catch (Exception ex) { DebugLog($"[OpenUrl] {ex}"); }
        }

        private void OpenFaq_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://gamepinned.com/#faq",
                    UseShellExecute = true
                });
            }
            catch (Exception ex) { DebugLog($"[OpenUrl] {ex}"); }
        }

        private void OpenChangelog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://gamepinned.com/docs/changelog",
                    UseShellExecute = true
                });
            }
            catch (Exception ex) { DebugLog($"[OpenUrl] {ex}"); }
        }

        private void OpenDocs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://gamepinned.com/docs",
                    UseShellExecute = true
                });
            }
            catch (Exception ex) { DebugLog($"[OpenUrl] {ex}"); }
        }

        private void OpenUrl_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is FrameworkElement el && el.Tag is string url && !string.IsNullOrEmpty(url))
                    Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception ex) { DebugLog($"[OpenUrl] {ex}"); }
        }


        #region NVIDIA Profile

        // ── UI state ──────────────────────────────────────────────────────────

        /// <summary>
        /// Updates the entire NVIDIA tab UI from a fresh status snapshot.
        /// Rebuilds the profile list, cancelling any in-progress rename.
        /// </summary>
        private void UpdateNvidiaUI(NvidiaConfigInfo status)
        {
            bool nvidiaPresent = status.IsNvidiaPresent;

            // GPU card vs not-detected banner
            NvidiaGpuInfoCard.Visibility = nvidiaPresent ? Visibility.Visible : Visibility.Collapsed;
            NvidiaNotDetectedCard.Visibility = nvidiaPresent ? Visibility.Collapsed : Visibility.Visible;

            if (nvidiaPresent && status.Gpu != null)
            {
                NvidiaGpuNameText.Text = status.Gpu.Name;
                NvidiaDriverText.Text = status.Gpu.DriverVersion;
                NvidiaVramText.Text = int.TryParse(status.Gpu.VramMb, out var vramMb)
                    ? $"{vramMb / 1024} GB" : "—";
            }
            else if (nvidiaPresent)
            {
                NvidiaGpuNameText.Text = "NVIDIA GPU";
                NvidiaDriverText.Text = "—";
                NvidiaVramText.Text = "—";
            }

            // Rebuild profiles list
            var profiles = status.Profiles ?? new List<NvidiaProfile>();
            RebuildProfilesList(profiles, status.ActiveProfileId);

            // Save button: requires NVIDIA present + paid license + not already at limit
            bool isPaid = LicenseStatusText.Text == StringConstants.Active;
            SaveNvidiaConfigButton.IsEnabled = nvidiaPresent && isPaid && profiles.Count < 5;

            // Auto-restore toggle: only meaningful when at least one profile exists
            // (UpdateUILockState will override to false when the GUI is locked)
            NvidiaAutoRestoreToggle.IsEnabled = isPaid && profiles.Count > 0;
        }

        // ── Profile list builder ──────────────────────────────────────────────

        private void RebuildProfilesList(List<NvidiaProfile> profiles, string? activeProfileId)
        {
            NvidiaProfilesList.Children.Clear();

            bool hasProfiles = profiles.Count > 0;
            NvidiaNoProfilesHint.Visibility = hasProfiles ? Visibility.Collapsed : Visibility.Visible;

            bool isPaid = LicenseStatusText.Text == StringConstants.Active;

            foreach (var profile in profiles.OrderByDescending(p => p.SavedAt))
            {
                bool isActive = profile.Id == activeProfileId;
                NvidiaProfilesList.Children.Add(BuildProfileCard(profile, isActive, isPaid));
            }
        }

        /// <summary>
        /// Builds one profile card for the list.
        /// Each card has a normal view row (name, date, badges, action buttons)
        /// and a rename row that swaps in when the user clicks RENAME.
        /// </summary>
        private UIElement BuildProfileCard(NvidiaProfile profile, bool isActive, bool isPaid)
        {
            var accentColor = isActive
                ? System.Windows.Media.Color.FromRgb(0x76, 0xB9, 0x00)
                : System.Windows.Media.Color.FromRgb(0x1E, 0x1E, 0x1E);

            var outerCard = new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x0E, 0x0E, 0x0E)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x1E, 0x1E, 0x1E)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 8),
                ClipToBounds = true
            };

            // Root grid: accent stripe (col 0) + content (col 1)
            var rootGrid = new Grid();
            rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
            rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var stripe = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(accentColor)
                // HorizontalAlignment and VerticalAlignment default to Stretch inside a Grid cell
            };
            Grid.SetColumn(stripe, 0);
            rootGrid.Children.Add(stripe);

            // Content panel: normal view + rename row stacked
            var contentPanel = new StackPanel { Margin = new Thickness(14, 10, 12, 10) };
            Grid.SetColumn(contentPanel, 1);
            rootGrid.Children.Add(contentPanel);

            // ── Normal view row ────────────────────────────────────────────
            var normalView = new Grid();
            normalView.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            normalView.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Left side: name + date + badges
            var leftStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

            // Name row (with optional AUTO badge)
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
            var nameText = new TextBlock
            {
                Text = profile.Name,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xD0, 0xD0, 0xD0)),
                VerticalAlignment = VerticalAlignment.Center
            };
            nameRow.Children.Add(nameText);

            if (isActive)
            {
                var autoBadge = new Border
                {
                    Background = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromArgb(0x20, 0x76, 0xB9, 0x00)),
                    BorderBrush = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromArgb(0x55, 0x76, 0xB9, 0x00)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(6, 1, 6, 1),
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                autoBadge.Child = new TextBlock
                {
                    Text = "AUTO",
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x76, 0xB9, 0x00))
                };
                nameRow.Children.Add(autoBadge);
            }
            leftStack.Children.Add(nameRow);

            // Date line
            leftStack.Children.Add(new TextBlock
            {
                Text = profile.SavedAt.ToString("MMM d, yyyy 'at' h:mm tt"),
                FontSize = 11,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x44, 0x44, 0x44)),
                Margin = new Thickness(0, 0, 0, 5)
            });

            // Badges row
            var badgesRow = new StackPanel { Orientation = Orientation.Horizontal };
            if (profile.MonitorCount > 0)
                badgesRow.Children.Add(MakeProfileBadge(
                    $"Display ({profile.MonitorCount} monitor{(profile.MonitorCount != 1 ? "s" : "")})",
                    System.Windows.Media.Color.FromRgb(0x33, 0x60, 0x80),
                    System.Windows.Media.Color.FromArgb(0x1A, 0x3D, 0x60, 0x80)));
            leftStack.Children.Add(badgesRow);

            Grid.SetColumn(leftStack, 0);
            normalView.Children.Add(leftStack);

            // Right side: action buttons
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };

            // RESTORE button
            var restoreBtn = new Button
            {
                Content = "RESTORE",
                Style = (Style)FindResource("MaterialDesignRaisedButton"),
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x76, 0xB9, 0x00)),
                Foreground = System.Windows.Media.Brushes.Black,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 0),
                IsEnabled = isPaid
            };
            var capturedProfileId = profile.Id;
            var capturedProfileName = profile.Name;
            restoreBtn.Click += async (s, e) => await OnNvidiaRestoreProfileClick(capturedProfileId, capturedProfileName);
            btnPanel.Children.Add(restoreBtn);

            // SET AS AUTO button (only shown for non-active profiles)
            if (!isActive)
            {
                var setAutoBtn = MakeTextButton("SET AUTO", isPaid);
                setAutoBtn.Margin = new Thickness(0, 0, 6, 0);
                setAutoBtn.Click += async (s, e) => await OnNvidiaSetActiveClick(capturedProfileId);
                btnPanel.Children.Add(setAutoBtn);
            }

            // RENAME button
            StackPanel renameRow = null!; // forward reference, assigned below
            TextBox? renameBox = null;    // forward reference, assigned below

            var renameBtn = MakeTextButton("RENAME", isPaid);
            renameBtn.Margin = new Thickness(0, 0, 6, 0);

            // DELETE button
            var deleteBtn = MakeTextButton("DELETE", isPaid);
            deleteBtn.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x88, 0x33, 0x33));
            deleteBtn.Click += async (s, e) => await OnNvidiaDeleteProfileClick(capturedProfileId, profile.Name);

            btnPanel.Children.Add(renameBtn);
            btnPanel.Children.Add(deleteBtn);

            Grid.SetColumn(btnPanel, 1);
            normalView.Children.Add(btnPanel);

            contentPanel.Children.Add(normalView);

            // ── Rename row (hidden by default) ────────────────────────────
            renameRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 8, 0, 0)
            };

            renameBox = new TextBox
            {
                Text = profile.Name,
                MaxLength = 64,
                MinWidth = 160,
                Margin = new Thickness(0, 0, 8, 0),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };

            var saveRenameBtn = new Button
            {
                Content = "SAVE",
                Style = (Style)FindResource("MaterialDesignRaisedButton"),
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x76, 0xB9, 0x00)),
                Foreground = System.Windows.Media.Brushes.Black,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 0)
            };
            saveRenameBtn.Click += async (s, e) =>
            {
                var newName = renameBox.Text.Trim();
                if (string.IsNullOrEmpty(newName)) { renameBox.Focus(); return; }
                await OnNvidiaRenameProfileClick(capturedProfileId, newName);
            };

            var cancelRenameBtn = MakeTextButton("CANCEL", true);
            cancelRenameBtn.Click += (s, e) =>
            {
                renameRow.Visibility = Visibility.Collapsed;
                normalView.Visibility = Visibility.Visible;
            };

            renameRow.Children.Add(renameBox);
            renameRow.Children.Add(saveRenameBtn);
            renameRow.Children.Add(cancelRenameBtn);
            contentPanel.Children.Add(renameRow);

            // Wire RENAME button now that renameRow exists
            renameBtn.Click += (s, e) =>
            {
                normalView.Visibility = Visibility.Collapsed;
                renameRow.Visibility = Visibility.Visible;
                renameBox.SelectAll();
                renameBox.Focus();
            };

            outerCard.Child = rootGrid;
            return outerCard;
        }

        private static Border MakeProfileBadge(string text,
            System.Windows.Media.Color fg,
            System.Windows.Media.Color bg,
            int rightMargin = 0)
        {
            var badge = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(bg),
                BorderBrush = new System.Windows.Media.SolidColorBrush(fg) { Opacity = 0.35 },
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(0, 0, rightMargin, 0)
            };
            badge.Child = new TextBlock
            {
                Text = text,
                FontSize = 10,
                Foreground = new System.Windows.Media.SolidColorBrush(fg)
            };
            return badge;
        }

        private static Button MakeTextButton(string text, bool enabled)
        {
            return new Button
            {
                Content = text,
                Style = Application.Current.TryFindResource("MaterialDesignFlatButton") as Style,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x55)),
                Padding = new Thickness(8, 4, 8, 4),
                IsEnabled = enabled
            };
        }

        // ── Profile action handlers ───────────────────────────────────────────

        private async Task OnNvidiaRestoreProfileClick(string profileId, string profileName)
        {
            if (!RequireAuth("restore GPU Pinned profile")) return;
            if (LicenseStatusText.Text != StringConstants.Active)
            {
                DialogHelper.Show("GPU Pinned requires an ELITE or PRO license.", "Pro Feature",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = DialogHelper.Show(
                "Restore profile \"" + profileName + "\"?\n\nThis will overwrite your current GPU Pinned settings and display configuration. Any unsaved changes in NVIDIA Control Panel will be lost.",
                "Confirm Restore", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            var (success, message) = await _serviceCommunicator.RestoreNvidiaProfileAsync(profileId);
            if (!success)
                DialogHelper.Show(message, "Restore Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            else
                DialogHelper.Show("Settings restored.", "Restore Complete",
                    MessageBoxButton.OK, MessageBoxImage.None);
            // No list rebuild needed — restore doesn't change profile metadata
        }

        private async Task OnNvidiaSetActiveClick(string profileId)
        {
            if (!RequireAuth("change auto-restore profile")) return;
            if (LicenseStatusText.Text != StringConstants.Active)
            {
                DialogHelper.Show("GPU Pinned requires an ELITE or PRO license.", "Pro Feature",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var (success, message) = await _serviceCommunicator.SetActiveNvidiaProfileAsync(profileId);
            if (!success)
                DialogHelper.Show(message, "Error", MessageBoxButton.OK, MessageBoxImage.Warning);

            // Refresh list to update AUTO badges
            var nvidiaStatus = await _serviceCommunicator.GetNvidiaStatusAsync();
            if (nvidiaStatus != null) UpdateNvidiaUI(nvidiaStatus);
        }

        private async Task OnNvidiaRenameProfileClick(string profileId, string newName)
        {
            if (!RequireAuth("rename GPU Pinned profile")) return;
            if (LicenseStatusText.Text != StringConstants.Active)
            {
                DialogHelper.Show("GPU Pinned requires an ELITE or PRO license.", "Pro Feature",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var (success, message) = await _serviceCommunicator.RenameNvidiaProfileAsync(profileId, newName);
            if (!success)
            {
                DialogHelper.Show(message, "Rename Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // Rebuild list to show new name
            var nvidiaStatus = await _serviceCommunicator.GetNvidiaStatusAsync();
            if (nvidiaStatus != null) UpdateNvidiaUI(nvidiaStatus);
        }

        private async Task OnNvidiaDeleteProfileClick(string profileId, string profileName)
        {
            if (!RequireAuth("delete GPU Pinned profile")) return;
            if (LicenseStatusText.Text != StringConstants.Active)
            {
                DialogHelper.Show("GPU Pinned requires an ELITE or PRO license.", "Pro Feature",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var confirm = DialogHelper.Show(
                $"Delete profile \"{profileName}\"? This cannot be undone.",
                "Delete Profile", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            var (success, message) = await _serviceCommunicator.DeleteNvidiaProfileAsync(profileId);
            if (!success)
                DialogHelper.Show(message, "Delete Failed", MessageBoxButton.OK, MessageBoxImage.Warning);

            var nvidiaStatus = await _serviceCommunicator.GetNvidiaStatusAsync();
            if (nvidiaStatus != null) UpdateNvidiaUI(nvidiaStatus);
        }

        // ── SAVE NEW PROFILE button ───────────────────────────────────────────

        private async void SaveNvidiaConfig_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireAuth("save GPU Pinned profile")) return;
            if (LicenseStatusText.Text != StringConstants.Active)
            {
                DialogHelper.Show("GPU Pinned requires an ELITE or PRO license.", "Pro Feature",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SaveNvidiaConfigButton.IsEnabled = false;
            try
            {
                // Save with auto-name; the user can rename afterwards
                var (success, message) = await _serviceCommunicator.SaveNvidiaConfigAsync();
                if (!success)
                    DialogHelper.Show(message, "Save Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                else
                    DialogHelper.Show("Profile saved. Use RENAME to give it a name.",
                        "Profile Saved", MessageBoxButton.OK, MessageBoxImage.None);

                var nvidiaStatus = await _serviceCommunicator.GetNvidiaStatusAsync();
                if (nvidiaStatus != null) UpdateNvidiaUI(nvidiaStatus);
            }
            catch (Exception ex) { DebugLog($"[NVIDIA] SaveConfig click: {ex}"); }
            finally { SaveNvidiaConfigButton.IsEnabled = LicenseStatusText.Text == StringConstants.Active; }
        }

        // ── Auto-restore toggle ───────────────────────────────────────────────

        private async void NvidiaAutoRestore_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireAuth("change auto-restore setting")) { NvidiaAutoRestoreToggle.IsChecked = !NvidiaAutoRestoreToggle.IsChecked; return; }
            var enabled = NvidiaAutoRestoreToggle.IsChecked == true;
            if (enabled && LicenseStatusText.Text != StringConstants.Active)
            {
                DialogHelper.Show("GPU Pinned requires an ELITE or PRO license.", "Pro Feature",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                NvidiaAutoRestoreToggle.IsChecked = false;
                return;
            }

            try
            {
                // Route through the service pipe to avoid the TOCTOU race of a direct config.json write.
                var success = await _serviceCommunicator.SetNvidiaAutoRestoreAsync(enabled);
                if (!success)
                {
                    DialogHelper.Show("Failed to save auto-restore setting.", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    NvidiaAutoRestoreToggle.IsChecked = !enabled; // revert visual state
                }
            }
            catch (Exception ex) { DebugLog($"[NVIDIA] AutoRestore toggle: {ex}"); }
        }

        #endregion

    }

    public class GameDisplayInfo
    {
        public string GameName { get; set; } = "";
        public string PlatformId { get; set; } = "";
        public string InstallPath { get; set; } = "";
        public string StatusText { get; set; } = "";
        public bool IsExcluded { get; set; }
        public bool IsProtected { get; set; }
    }
}
