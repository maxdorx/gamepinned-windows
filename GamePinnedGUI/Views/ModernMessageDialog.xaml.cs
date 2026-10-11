using System.Windows;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;

namespace GamePinned.GUI.Views
{
    public partial class ModernMessageDialog : Window
    {
        public MessageBoxResult MessageResult { get; private set; } = MessageBoxResult.Cancel;

        public ModernMessageDialog(string message, string title, MessageBoxButton button, MessageBoxImage icon)
        {
            InitializeComponent();
            TitleText.Text = title;
            MessageText.Text = message;

            SetupButtons(button);
            SetupIcon(icon);

            // Allow dragging
            MouseLeftButtonDown += (s, e) => DragMove();
        }

        private void SetupButtons(MessageBoxButton button)
        {
            OkButton.Visibility = Visibility.Collapsed;
            YesButton.Visibility = Visibility.Collapsed;
            NoButton.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Collapsed;

            switch (button)
            {
                case MessageBoxButton.OK:
                    OkButton.Visibility = Visibility.Visible;
                    break;
                case MessageBoxButton.OKCancel:
                    OkButton.Visibility = Visibility.Visible;
                    CancelButton.Visibility = Visibility.Visible;
                    break;
                case MessageBoxButton.YesNo:
                    YesButton.Visibility = Visibility.Visible;
                    NoButton.Visibility = Visibility.Visible;
                    break;
                case MessageBoxButton.YesNoCancel:
                    YesButton.Visibility = Visibility.Visible;
                    NoButton.Visibility = Visibility.Visible;
                    CancelButton.Visibility = Visibility.Visible;
                    break;
            }
        }

        private void SetupIcon(MessageBoxImage icon)
        {
            switch (icon)
            {
                case MessageBoxImage.Error:
                    IconIcon.Kind = PackIconKind.AlertCircle;
                    IconIcon.Foreground = System.Windows.Media.Brushes.Red;
                    break;
                case MessageBoxImage.Warning:
                    IconIcon.Kind = PackIconKind.Alert;
                    IconIcon.Foreground = System.Windows.Media.Brushes.Orange;
                    break;
                case MessageBoxImage.Question:
                    IconIcon.Kind = PackIconKind.HelpCircle;
                    IconIcon.Foreground = System.Windows.Media.Brushes.Cyan;
                    break;
                case MessageBoxImage.Information:
                default:
                    IconIcon.Kind = PackIconKind.Information;
                    IconIcon.Foreground = System.Windows.Media.Brushes.LightGreen;
                    break;
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            MessageResult = MessageBoxResult.OK;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            MessageResult = MessageBoxResult.Cancel;
            DialogResult = false;
            Close();
        }

        private void YesButton_Click(object sender, RoutedEventArgs e)
        {
            MessageResult = MessageBoxResult.Yes;
            DialogResult = true;
            Close();
        }

        private void NoButton_Click(object sender, RoutedEventArgs e)
        {
            MessageResult = MessageBoxResult.No;
            DialogResult = false;
            Close();
        }
    }
}
