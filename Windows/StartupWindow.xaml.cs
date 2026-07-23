using System;
using System.Windows;
using System.Windows.Interop;

namespace SSMS
{
    public partial class StartupWindow : Window
    {
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DwmwaUseImmersiveDarkMode = 20;

        public StartupWindow()
        {
            InitializeComponent();
            ApplyDarkMode();
        }

        private void ApplyDarkMode()
        {
            try
            {
                var helper = new WindowInteropHelper(this);
                helper.EnsureHandle();
                int darkMode = 1;
                DwmSetWindowAttribute(helper.Handle, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));
            }
            catch
            {
                // Dark title-bar support is best effort on older Windows versions.
            }
        }

        public void SetStatus(string message)
        {
            StatusText.Text = message;
        }
    }
}
