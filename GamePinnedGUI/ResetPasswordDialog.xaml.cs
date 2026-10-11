using System.Security.Principal;
using System.Windows;
using Windows.Security.Credentials.UI;

namespace GamePinned.GUI
{
    public partial class ResetPasswordDialog : Window
    {
        private static readonly string _debugLogDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GamePinned", "logs");

        private static void DebugLog(string message)
        {
            try
            {
                System.IO.Directory.CreateDirectory(_debugLogDir);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(_debugLogDir, $"debug_{DateTime.Now:yyyyMMdd}.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [GUI] [ResetPassword] {message}\n");
            }
            catch { }
        }

        /// <summary>
        /// Set on successful reset. Caller should save this as the new AdminPasswordHash.
        /// </summary>
        public string? NewPasswordHash { get; private set; }

        public ResetPasswordDialog()
        {
            InitializeComponent();
            UsernameText.Text = WindowsIdentity.GetCurrent().Name;
            Loaded += (_, _) => NewPasswordBox.Focus();
        }

        private async void Reset_Click(object sender, RoutedEventArgs e)
        {
            var newPassword = NewPasswordBox.Password;
            var confirmPassword = ConfirmPasswordBox.Password;

            // Validate new password fields first
            if (string.IsNullOrWhiteSpace(newPassword))
            {
                ShowError("Please enter a new password.");
                return;
            }
            if (newPassword.Length < 4)
            {
                ShowError("New password must be at least 4 characters.");
                return;
            }
            if (newPassword != confirmPassword)
            {
                ShowError("Passwords do not match.");
                ConfirmPasswordBox.Clear();
                ConfirmPasswordBox.Focus();
                return;
            }

            // Check that the current Windows user is in the local Administrators group.
            // We check raw group membership (not the elevated token) so this works even
            // when the GUI is running unelevated.
            var identity = WindowsIdentity.GetCurrent();
            var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            bool isMemberOfAdmins = identity.Groups?.Any(g => g.Equals(adminSid)) == true;

            if (!isMemberOfAdmins)
            {
                DebugLog($"Admin group check failed for user: {identity.Name}");
                ShowError("This account doesn't have administrator permissions. Sign in with an admin account.");
                return;
            }

            DebugLog($"Admin group check passed for user: {identity.Name} — invoking UserConsentVerifier");

            // Disable button while Windows shows the verification prompt
            ResetButton.IsEnabled = false;
            HideError();

            UserConsentVerificationResult result;
            try
            {
                result = await UserConsentVerifier.RequestVerificationAsync(
                    "Authorize GamePinned admin password reset");
            }
            catch (Exception ex)
            {
                DebugLog($"UserConsentVerifier threw: {ex}");
                ResetButton.IsEnabled = true;
                ShowError("Could not verify your identity. Try again.");
                return;
            }
            finally
            {
                ResetButton.IsEnabled = true;
            }

            DebugLog($"UserConsentVerifier result: {result}");

            switch (result)
            {
                case UserConsentVerificationResult.Verified:
                    NewPasswordHash = PasswordDialog.HashPassword(newPassword);
                    DebugLog("Password reset successful — new hash generated");
                    DialogResult = true;
                    break;

                case UserConsentVerificationResult.Canceled:
                    // User dismissed the prompt — stay open, let them try again
                    break;

                case UserConsentVerificationResult.RetriesExhausted:
                    ShowError("Too many failed attempts. Try again later.");
                    break;

                case UserConsentVerificationResult.NotConfiguredForUser:
                    ShowError("Windows Hello isn't set up for this account.");
                    break;

                default:
                    ShowError("Verification failed. Try again.");
                    break;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void PasswordBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
                Reset_Click(sender, e);
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }

        private void HideError()
        {
            ErrorText.Visibility = Visibility.Collapsed;
        }
    }
}