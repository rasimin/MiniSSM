using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace SSMS.Utilities
{
    public static class FileDialogHelper
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnableWindow(IntPtr hWnd, bool bEnable);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        public static Task<(bool result, string? filePath)> ShowSaveFileDialogAsync(
            string filter,
            string defaultExt,
            string title,
            string defaultFileName,
            string? initialDirectory,
            Window? ownerWindow = null)
        {
            var tcs = new TaskCompletionSource<(bool result, string? filePath)>(TaskCreationOptions.RunContinuationsAsynchronously);

            IntPtr ownerHwnd = IntPtr.Zero;
            if (ownerWindow != null)
            {
                ownerHwnd = new WindowInteropHelper(ownerWindow).Handle;
            }

            var thread = new Thread(() =>
            {
                try
                {
                    if (ownerHwnd != IntPtr.Zero)
                    {
                        ownerWindow?.Dispatcher.Invoke(() => EnableWindow(ownerHwnd, false));
                    }

                    var saveFileDialog = new SaveFileDialog
                    {
                        Filter = filter,
                        DefaultExt = defaultExt,
                        AddExtension = true,
                        Title = title,
                        FileName = defaultFileName,
                        RestoreDirectory = true
                    };

                    if (!string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory))
                    {
                        saveFileDialog.InitialDirectory = initialDirectory;
                    }

                    bool? dialogResult = saveFileDialog.ShowDialog();

                    if (dialogResult == true)
                    {
                        tcs.SetResult((true, saveFileDialog.FileName));
                    }
                    else
                    {
                        tcs.SetResult((false, null));
                    }
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
                finally
                {
                    if (ownerHwnd != IntPtr.Zero)
                    {
                        ownerWindow?.Dispatcher.Invoke(() =>
                        {
                            EnableWindow(ownerHwnd, true);
                            SetForegroundWindow(ownerHwnd);
                        });
                    }
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            return tcs.Task;
        }

        public static Task<(bool result, string[] fileNames)> ShowOpenFileDialogAsync(
            string filter,
            string defaultExt,
            string title,
            bool multiselect,
            string? initialDirectory,
            Window? ownerWindow = null)
        {
            var tcs = new TaskCompletionSource<(bool result, string[] fileNames)>(TaskCreationOptions.RunContinuationsAsynchronously);

            IntPtr ownerHwnd = IntPtr.Zero;
            if (ownerWindow != null)
            {
                ownerHwnd = new WindowInteropHelper(ownerWindow).Handle;
            }

            var thread = new Thread(() =>
            {
                try
                {
                    if (ownerHwnd != IntPtr.Zero)
                    {
                        ownerWindow?.Dispatcher.Invoke(() => EnableWindow(ownerHwnd, false));
                    }

                    var openFileDialog = new OpenFileDialog
                    {
                        Filter = filter,
                        DefaultExt = defaultExt,
                        Title = title,
                        Multiselect = multiselect,
                        RestoreDirectory = true
                    };

                    if (!string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory))
                    {
                        openFileDialog.InitialDirectory = initialDirectory;
                    }

                    bool? dialogResult = openFileDialog.ShowDialog();

                    if (dialogResult == true)
                    {
                        tcs.SetResult((true, openFileDialog.FileNames));
                    }
                    else
                    {
                        tcs.SetResult((false, Array.Empty<string>()));
                    }
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
                finally
                {
                    if (ownerHwnd != IntPtr.Zero)
                    {
                        ownerWindow?.Dispatcher.Invoke(() =>
                        {
                            EnableWindow(ownerHwnd, true);
                            SetForegroundWindow(ownerHwnd);
                        });
                    }
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            return tcs.Task;
        }
    }
}
