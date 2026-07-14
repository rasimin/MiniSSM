using System.Windows;
using System.Windows.Input;

namespace SSMS
{
    public enum UnsavedChangesChoice
    {
        Cancel,
        DontSave,
        Save
    }

    public partial class UnsavedChangesWindow : Window
    {
        public UnsavedChangesChoice Choice { get; private set; } = UnsavedChangesChoice.Cancel;

        public UnsavedChangesWindow(string queryName)
        {
            InitializeComponent();
            QueryNameText.Text = queryName;
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            CloseWithChoice(UnsavedChangesChoice.Save);
        }

        private void DontSaveButton_Click(object sender, RoutedEventArgs e)
        {
            CloseWithChoice(UnsavedChangesChoice.DontSave);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            CloseWithChoice(UnsavedChangesChoice.Cancel);
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CloseWithChoice(UnsavedChangesChoice.Cancel);
            }
        }

        private void CloseWithChoice(UnsavedChangesChoice choice)
        {
            Choice = choice;
            DialogResult = true;
        }
    }
}
