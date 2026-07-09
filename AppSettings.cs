using System;
using System.IO;
using System.Text.Json;

namespace SSMS
{
    public sealed class AppSettings
    {
        private static readonly object SyncRoot = new();
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public bool EnablePerformanceLogging { get; set; }
        public QuerySettings Query { get; set; } = new();

        public static AppSettings Current { get; private set; } = Load();

        public static string SettingsFilePath =>
            Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        public static AppSettings Load()
        {
            lock (SyncRoot)
            {
                try
                {
                    if (File.Exists(SettingsFilePath))
                    {
                        string json = File.ReadAllText(SettingsFilePath);
                        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                        if (settings != null)
                        {
                            settings.Query ??= new QuerySettings();
                            return settings;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, "Failed to load application settings.");
                }

                return new AppSettings();
            }
        }

        public static void Save(AppSettings settings)
        {
            lock (SyncRoot)
            {
                settings.Query ??= new QuerySettings();
                string json = JsonSerializer.Serialize(settings, JsonOptions);
                File.WriteAllText(SettingsFilePath, json);
                Current = settings;
            }
        }
    }

    public sealed class QuerySettings
    {
        public int CommandTimeoutSeconds { get; set; } = 120;
    }
}
