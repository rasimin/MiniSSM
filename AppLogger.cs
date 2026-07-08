using System;
using System.IO;
using System.Text;

namespace SSMS
{
    public static class AppLogger
    {
        private static readonly object SyncRoot = new();

        public static string LogDirectory => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");

        public static void Info(string message)
        {
            Write("INFO", message);
        }

        public static void Error(Exception exception, string context)
        {
            Write("ERROR", $"{context}{Environment.NewLine}{exception}");
        }

        private static void Write(string level, string message)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                string path = Path.Combine(LogDirectory, $"minissms-{DateTime.Now:yyyyMMdd}.log");
                string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";

                lock (SyncRoot)
                {
                    File.AppendAllText(path, entry, Encoding.UTF8);
                }
            }
            catch
            {
                // Logging must never crash the app.
            }
        }
    }
}
