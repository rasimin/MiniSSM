using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SSMS
{
    public partial class DarkMessageBoxWindow : Window
    {
        public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

        public DarkMessageBoxWindow(string message, string title = "Notifikasi", MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None)
        {
            InitializeComponent();

            TxtMessage.Text = message;
            TxtTitle.Text = title;

            // Configure Icon
            ConfigureIcon(icon);

            // Configure Buttons
            ConfigureButtons(button);
        }

        private void ConfigureIcon(MessageBoxImage icon)
        {
            switch (icon)
            {
                case MessageBoxImage.Error: // Or Hand / Stop
                    IconBadge.Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x19, 0x19));
                    IconBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x22, 0x22));
                    TxtIconSymbol.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0x55));
                    TxtIconSymbol.Text = "✕";
                    break;

                case MessageBoxImage.Warning: // Or Exclamation
                    IconBadge.Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x22, 0x10));
                    IconBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(0x5C, 0x45, 0x15));
                    TxtIconSymbol.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xA6, 0x23));
                    TxtIconSymbol.Text = "!";
                    break;

                case MessageBoxImage.Information: // Or Asterisk
                    IconBadge.Background = new SolidColorBrush(Color.FromRgb(0x10, 0x26, 0x36));
                    IconBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(0x18, 0x45, 0x66));
                    TxtIconSymbol.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
                    TxtIconSymbol.Text = "ℹ";
                    break;

                case MessageBoxImage.Question:
                    IconBadge.Background = new SolidColorBrush(Color.FromRgb(0x10, 0x26, 0x36));
                    IconBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(0x18, 0x45, 0x66));
                    TxtIconSymbol.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
                    TxtIconSymbol.Text = "?";
                    break;

                default:
                    IconBadge.Visibility = Visibility.Collapsed;
                    break;
            }
        }

        private void ConfigureButtons(MessageBoxButton button)
        {
            BtnOK.Visibility = Visibility.Collapsed;
            BtnYes.Visibility = Visibility.Collapsed;
            BtnNo.Visibility = Visibility.Collapsed;
            BtnCancel.Visibility = Visibility.Collapsed;

            switch (button)
            {
                case MessageBoxButton.OK:
                    BtnOK.Visibility = Visibility.Visible;
                    BtnOK.IsDefault = true;
                    break;

                case MessageBoxButton.OKCancel:
                    BtnOK.Visibility = Visibility.Visible;
                    BtnCancel.Visibility = Visibility.Visible;
                    BtnOK.IsDefault = true;
                    break;

                case MessageBoxButton.YesNo:
                    BtnYes.Visibility = Visibility.Visible;
                    BtnNo.Visibility = Visibility.Visible;
                    BtnYes.IsDefault = true;
                    break;

                case MessageBoxButton.YesNoCancel:
                    BtnYes.Visibility = Visibility.Visible;
                    BtnNo.Visibility = Visibility.Visible;
                    BtnCancel.Visibility = Visibility.Visible;
                    BtnYes.IsDefault = true;
                    break;
            }
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            CloseWithResult(MessageBoxResult.Cancel);
        }

        private void BtnOK_Click(object sender, RoutedEventArgs e)
        {
            CloseWithResult(MessageBoxResult.OK);
        }

        private void BtnYes_Click(object sender, RoutedEventArgs e)
        {
            CloseWithResult(MessageBoxResult.Yes);
        }

        private void BtnNo_Click(object sender, RoutedEventArgs e)
        {
            CloseWithResult(MessageBoxResult.No);
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            CloseWithResult(MessageBoxResult.Cancel);
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CloseWithResult(MessageBoxResult.Cancel);
            }
        }

        private void CloseWithResult(MessageBoxResult result)
        {
            Result = result;
            DialogResult = result == MessageBoxResult.OK || result == MessageBoxResult.Yes;
            Close();
        }

        public static MessageBoxResult Show(Window? owner, string message, string title = "Notifikasi", MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None)
        {
            var win = new DarkMessageBoxWindow(message, title, button, icon);
            if (owner != null && owner.IsLoaded && owner.IsVisible)
            {
                win.Owner = owner;
            }
            else if (Application.Current != null && Application.Current.MainWindow != null && Application.Current.MainWindow.IsLoaded && Application.Current.MainWindow.IsVisible)
            {
                win.Owner = Application.Current.MainWindow;
            }
            win.ShowDialog();
            return win.Result;
        }

        public static MessageBoxResult Show(string message, string title = "Notifikasi", MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None)
        {
            return Show(null, message, title, button, icon);
        }
    }
}
