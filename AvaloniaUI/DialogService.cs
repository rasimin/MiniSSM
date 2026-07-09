using Avalonia.Controls;

namespace SSMS;

internal static class DialogService
{
    public static Task ShowAsync(Window owner, string title, string message)
    {
        var ok = new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        var dialog = new Window
        {
            Title = title,
            Width = 440,
            Height = 190,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Grid
            {
                Margin = new Avalonia.Thickness(20),
                RowDefinitions = RowDefinitions.Parse("*,Auto"),
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    ok
                }
            }
        };
        Grid.SetRow(ok, 1);
        ok.Click += (_, _) => dialog.Close();
        return dialog.ShowDialog(owner);
    }
}
