using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace SSMS
{
    public partial class UnsafeExecutionWindow : Window
    {
        public bool ProceedWithExecution { get; private set; } = false;

        public UnsafeExecutionWindow(List<string> snippets)
        {
            InitializeComponent();

            if (snippets != null && snippets.Count > 0)
            {
                TxtSnippet.Text = string.Join(Environment.NewLine + Environment.NewLine, snippets);
            }
            else
            {
                TxtSnippet.Text = "-- Snippet tidak tersedia --";
            }
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            ProceedWithExecution = false;
            DialogResult = false;
            Close();
        }

        private void BtnProceed_Click(object sender, RoutedEventArgs e)
        {
            ProceedWithExecution = true;
            DialogResult = true;
            Close();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                BtnCancel_Click(sender, e);
            }
        }
    }
}
