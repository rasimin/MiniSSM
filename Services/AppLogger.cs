using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SSMS
{
    public static class AppLogger
    {
        private static readonly object SyncRoot = new();

        public static string LogDirectory => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");

        private static bool? _enablePerformanceLogging;
        public static bool EnablePerformanceLogging
        {
            get
            {
                if (!_enablePerformanceLogging.HasValue)
                {
                    try
                    {
                        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
                        if (File.Exists(path))
                        {
                            string json = File.ReadAllText(path);
                            using var doc = JsonDocument.Parse(json);
                            if (doc.RootElement.TryGetProperty("EnablePerformanceLogging", out var prop))
                            {
                                _enablePerformanceLogging = prop.GetBoolean();
                            }
                        }
                    }
                    catch
                    {
                        // Default to false if error or not found
                    }
                    _enablePerformanceLogging ??= false;
                }
                return _enablePerformanceLogging.Value;
            }
        }

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
