using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SSMS;

public partial class InputDialog : Window
{
    private readonly TaskCompletionSource<string> _tcs = new();

    public InputDialog()
    {
        InitializeComponent();
    }

    public InputDialog(string title, string prompt, string defaultText) : this()
    {
        TitleText.Text = title;
        PromptText.Text = prompt;
        InputBox.Text = defaultText;
        
        Opened += (s, e) =>
        {
            InputBox.Focus();
            if (InputBox.Text != null)
            {
                InputBox.SelectionStart = 0;
                InputBox.SelectionEnd = InputBox.Text.Length;
            }
        };
    }

    public Task<string> ShowInputAsync(Window owner)
    {
        ShowDialog(owner);
        return _tcs.Task;
    }

    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void Ok_OnClick(object? sender, RoutedEventArgs e)
    {
        _tcs.TrySetResult(InputBox.Text?.Trim() ?? "");
        Close();
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs e)
    {
        _tcs.TrySetResult(string.Empty);
        Close();
    }

    private void InputBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Ok_OnClick(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Cancel_OnClick(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }
}
