using System.Windows;
using GamePinned.GUI.Views;

namespace GamePinned.GUI.Services
{
    public static class DialogHelper
    {
        public static MessageBoxResult Show(string message, string title, MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.Information, Window? owner = null)
        {
            var dialog = new ModernMessageDialog(message, title, button, icon);

            // Always bind to MainWindow as Owner, even when MainWindow is hidden (tray mode).
            // Without an Owner, a Window has no parent in the Z-order — it can appear over
            // unrelated apps (Epic launcher, browser, etc.) and outlive context switches.
            // Setting Owner ties the dialog's Z-order and minimize/restore state to the main
            // GamePinned window. If MainWindow is hidden, the dialog still shows but stays
            // bound to GamePinned's window context, so it doesn't float over other apps.
            var resolvedOwner = owner
                ?? System.Windows.Application.Current?.MainWindow;

            if (resolvedOwner != null)
            {
                dialog.Owner = resolvedOwner;
                // CenterOwner still works correctly when Owner is hidden — WPF uses the
                // Owner's saved Restore-state bounds rather than its current screen rect.
            }
            else
            {
                // Genuine no-MainWindow case (app initialization, shutdown). Fall back to
                // screen center so the dialog isn't placed at (0,0).
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            dialog.ShowDialog();
            return dialog.MessageResult;
        }
    }
}
