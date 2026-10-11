using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Input;
using MessageBox = System.Windows.MessageBox;

namespace GamePinned.GUI
{
    public partial class PasswordDialog : Window
    {
        public string EnteredPassword { get; private set; } = "";
        public bool IsAuthenticated { get; private set; }

        private readonly string _expectedHash;

        /// <summary>
        /// Set when the user resets via Windows identity verification instead of entering the old password.
        /// Caller should save this as the new AdminPasswordHash.
        /// </summary>
        public string? NewPasswordHash { get; private set; }

        // Rate limiting — static so state persists across dialog instances within the same app session.
        // Resets to zero when the app restarts (acceptable — auth state also resets on restart).
        private static int _failedAttempts = 0;
        private static DateTime _lockoutUntil = DateTime.MinValue;
        private const int MaxAttempts = 5;
        private const int LockoutSeconds = 30;

        private System.Windows.Threading.DispatcherTimer? _lockoutTimer;

        public PasswordDialog(string expectedHash, string prompt = "Enter your admin password:")
        {
            InitializeComponent();
            _expectedHash = expectedHash;
            PromptText.Text = prompt;
            // Only show "Forgot password?" when there is a password to reset
            if (!string.IsNullOrEmpty(expectedHash))
                ForgotPasswordButton.Visibility = System.Windows.Visibility.Visible;
            Loaded += OnLoaded;
            Closed += (s, e) => { _lockoutTimer?.Stop(); _lockoutTimer = null; };
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            PasswordInput.Focus();
            // If a lockout is still active from a previous dialog in this session, apply it immediately
            // so the user cannot bypass it by closing and reopening the dialog.
            // Only applies in verify mode (empty hash = initial setup, no rate limiting).
            if (!string.IsNullOrEmpty(_expectedHash) && DateTime.Now < _lockoutUntil)
                StartLockoutUI();
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            EnteredPassword = PasswordInput.Password;

            if (string.IsNullOrEmpty(_expectedHash))
            {
                // No password set - just accept
                IsAuthenticated = true;
                DialogResult = true;
                return;
            }

            bool verified;
            try
            {
                verified = VerifyPassword(EnteredPassword, _expectedHash);
            }
            catch (Exception ex)
            {
                // Log to same debug log used by MainWindow
                try
                {
                    var logDir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "GamePinned", "logs");
                    System.IO.Directory.CreateDirectory(logDir);
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(logDir, $"debug_{DateTime.Now:yyyyMMdd}.log"),
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [GUI] [PasswordDialog] VerifyPassword threw — hash format: '{(_expectedHash.Length > 10 ? _expectedHash.Substring(0, 10) : _expectedHash)}...', error: {ex}\n");
                }
                catch { }
                MessageBox.Show(
                    "Authentication failed. Try again.",
                    "Authentication Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (verified)
            {
                // Reset rate-limit state on success so the next session starts clean
                _failedAttempts = 0;
                _lockoutUntil = DateTime.MinValue;
                IsAuthenticated = true;
                DialogResult = true;
            }
            else
            {
                _failedAttempts++;
                PasswordInput.Clear();
                if (_failedAttempts >= MaxAttempts)
                {
                    _lockoutUntil = DateTime.Now.AddSeconds(LockoutSeconds);
                    StartLockoutUI();
                }
                else
                {
                    int remaining = MaxAttempts - _failedAttempts;
                    StatusText.Text = $"Incorrect password. {remaining} attempt{(remaining == 1 ? "" : "s")} remaining.";
                    StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF9800"));
                    StatusText.Visibility = System.Windows.Visibility.Visible;
                    PasswordInput.Focus();
                }
            }
        }

        private void ForgotPassword_Click(object sender, RoutedEventArgs e)
        {
            var resetDialog = new ResetPasswordDialog { Owner = this };
            if (resetDialog.ShowDialog() == true && resetDialog.NewPasswordHash != null)
            {
                // Password was reset via Windows identity verification — clear rate-limit state
                _failedAttempts = 0;
                _lockoutUntil = DateTime.MinValue;
                NewPasswordHash = resetDialog.NewPasswordHash;
                IsAuthenticated = true;
                DialogResult = true;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            IsAuthenticated = false;
            DialogResult = false;
        }

        private void PasswordInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                OK_Click(sender, e);
        }

        private void StartLockoutUI()
        {
            // Disable input — Cancel and Forgot Password remain active
            OkButton.IsEnabled = false;
            PasswordInput.IsEnabled = false;
            UpdateLockoutLabel();

            _lockoutTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _lockoutTimer.Tick += LockoutTimer_Tick;
            _lockoutTimer.Start();
        }

        private void UpdateLockoutLabel()
        {
            int secs = (int)Math.Ceiling((_lockoutUntil - DateTime.Now).TotalSeconds);
            StatusText.Text = $"Too many failed attempts. Try again in {Math.Max(secs, 0)}s.";
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF5252"));
            StatusText.Visibility = System.Windows.Visibility.Visible;
        }

        private void LockoutTimer_Tick(object? sender, EventArgs e)
        {
            if (DateTime.Now >= _lockoutUntil)
            {
                _lockoutTimer?.Stop();
                _lockoutTimer = null;
                _failedAttempts = 0;
                OkButton.IsEnabled = true;
                PasswordInput.IsEnabled = true;
                StatusText.Visibility = System.Windows.Visibility.Collapsed;
                PasswordInput.Focus();
            }
            else
            {
                UpdateLockoutLabel();
            }
        }

        /// <summary>
        /// Hash a password using PBKDF2-SHA256 with a random salt.
        /// Returns "v2:&lt;base64salt&gt;:&lt;base64hash&gt;" format.
        /// </summary>
        public static string HashPassword(string password)
        {
            var salt = new byte[16];
            RandomNumberGenerator.Fill(salt);
            var hash = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password), salt,
                iterations: 300_000, HashAlgorithmName.SHA256, outputLength: 32);
            return $"v2:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
        }

        /// <summary>
        /// Verify a password against a stored hash. Only the current v2 PBKDF2 format is accepted.
        /// Legacy plain-SHA256 hashes are no longer recognised (Critical #17, #31); a non-v2 hash returns false.
        /// </summary>
        public static bool VerifyPassword(string password, string storedHash)
        {
            if (!storedHash.StartsWith("v2:")) return false;
            // Trim the whole hash first — stray \r or \n from config.json parsing will
            // cause Convert.FromBase64String to throw FormatException
            var cleanHash = storedHash.Trim();
            var parts = cleanHash.Split(':');
            if (parts.Length != 3) return false;
            var salt = Convert.FromBase64String(parts[1].Trim());
            var expected = Convert.FromBase64String(parts[2].Trim());
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password), salt,
                iterations: 300_000, HashAlgorithmName.SHA256, outputLength: 32);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
    }
}
