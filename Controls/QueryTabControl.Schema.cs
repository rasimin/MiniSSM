using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Data;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Forms.Integration;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using Microsoft.Web.WebView2.Core;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace SSMS
{
    public partial class QueryTabControl : UserControl
    {

        private async Task ShowObjectDefinitionTabAsync(string objectName, string objectType)
        {
            var schemaTab = CreateSchemaTab(objectName, objectType);
            schemaTab.Content = new Grid
            {
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#1E1E1E")!,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Loading definition for {objectName}...",
                        Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#A0A0A0")!,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            };

            TabResults.Items.Add(schemaTab);
            TabResults.SelectedItem = schemaTab;

            string script;
            try
            {
                script = objectType.Equals("Table", StringComparison.OrdinalIgnoreCase)
                    ? await DatabaseHelper.GenerateTableCreateScriptAsync(ConnectionString, DatabaseName, objectName)
                    : await DatabaseHelper.GetObjectDefinitionAsync(ConnectionString, DatabaseName, objectName);

                if (string.IsNullOrWhiteSpace(script))
                {
                    script = $"-- Definition for {objectName} is unavailable or encrypted.";
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"Failed to load schema definition for '{objectName}'.");
                script = $"-- Failed to load definition for {objectName}.{Environment.NewLine}-- {ex.Message}";
            }

            schemaTab.Content = CreateSchemaTabContent(schemaTab, objectName, objectType, script);
        }

        private TabItem CreateSchemaTab(string objectName, string objectType)
        {
            var tab = new TabItem
            {
                Tag = new SchemaTabInfo(objectName, objectType),
                ToolTip = $"{objectType}: {objectName}"
            };

            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            header.Children.Add(new TextBlock
            {
                Text = $"Schema: {objectName}",
                MaxWidth = 220,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });

            var closeButton = new Button
            {
                Content = "x",
                ToolTip = "Close schema tab"
            };
            closeButton.SetResourceReference(StyleProperty, "SchemaTabCloseButton");
            closeButton.Click += (_, e) =>
            {
                e.Handled = true;
                CloseSchemaTab(tab);
            };
            header.Children.Add(closeButton);
            tab.Header = header;

            var contextMenu = new ContextMenu();
            var closeItem = new MenuItem { Header = "Close" };
            closeItem.Click += (_, _) => CloseSchemaTab(tab);
            contextMenu.Items.Add(closeItem);

            var closeAllItem = new MenuItem { Header = "Close All" };
            closeAllItem.Click += (_, _) => CloseAllSchemaTabs();
            contextMenu.Items.Add(closeAllItem);

            var colorMenu = new MenuItem { Header = "Set Color" };
            foreach (var (name, color) in new[]
            {
                ("Red", "#6A1B1B"),
                ("Green", "#1B5E20"),
                ("Blue", "#0D47A1"),
                ("Yellow", "#8D6E63"),
                ("Orange", "#D84315"),
                ("Default (Dark)", "#252526")
            })
            {
                var colorItem = new MenuItem { Header = name };
                colorItem.Click += (_, _) => tab.Background =
                    (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
                colorMenu.Items.Add(colorItem);
            }
            contextMenu.Items.Add(colorMenu);
            tab.ContextMenu = contextMenu;
            return tab;
        }

        private Button CreateSchemaActionButton(string text)
        {
            var button = new Button { Content = text };
            button.SetResourceReference(StyleProperty, "SchemaActionButton");
            return button;
        }

        private void CopySchemaToClipboard(string script, string objectName)
        {
            try
            {
                Clipboard.SetText(script);
                if (Window.GetWindow(this) is MainWindow mainWindow)
                {
                    mainWindow.UpdateStatusText($"Definition copied: {objectName}");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"Failed to copy schema definition for '{objectName}'.");
                MessageBox.Show(
                    $"Failed to copy definition: {ex.Message}",
                    "Copy Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void CloseSchemaTab(TabItem tab)
        {
            if (tab.Tag is not SchemaTabInfo)
            {
                return;
            }

            TabResults.Items.Remove(tab);
            if (TabResults.SelectedItem == null)
            {
                TabResults.SelectedIndex = 0;
            }
        }

        private void CloseAllSchemaTabs()
        {
            var schemaTabs = TabResults.Items
                .OfType<TabItem>()
                .Where(tab => tab.Tag is SchemaTabInfo)
                .ToList();

            foreach (TabItem tab in schemaTabs)
            {
                TabResults.Items.Remove(tab);
            }
            TabResults.SelectedIndex = 0;
        }

        public async Task CacheAndRefreshAutocompleteAsync()
        {
            if (!IsWebViewInitialized || _isDisposed || !_metadataRequested) return;

            try
            {
                string cacheKey = $"{ConnectionString}\u001f{DatabaseName}";
                var lazyMetadata = SharedMetadataCache.GetOrAdd(
                    cacheKey,
                    _ => new Lazy<Task<AutocompleteMetadata>>(
                        () => LoadAutocompleteMetadataAsync(ConnectionString, DatabaseName),
                        LazyThreadSafetyMode.ExecutionAndPublication));
                var metadata = await lazyMetadata.Value;
                if (_isDisposed) return;

                var payload = new
                {
                    columns = metadata.Columns,
                    objectTypes = metadata.ObjectTypes,
                    columnDetails = metadata.ColumnDetails,
                    storedProcedures = metadata.StoredProcedures,
                    scalarFunctions = metadata.ScalarFunctions,
                    tableFunctions = metadata.TableFunctions,
                    routineParameters = metadata.RoutineParameters,
                    databases = metadata.Databases,
                    activeDatabase = DatabaseName
                };
                string jsonPayload = JsonSerializer.Serialize(payload);
                await SqlEditorWebView.ExecuteScriptAsync($"updateMetadata({jsonPayload});");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"Failed to load autocomplete metadata for database '{DatabaseName}'");
            }
        }

        private void InvalidateAutocompleteCache(string databaseName)
        {
            string cacheKey = $"{ConnectionString}\u001f{databaseName}";
            SharedMetadataCache.TryRemove(cacheKey, out _);
        }

        public async Task RefreshAutocompleteAsync()
        {
            InvalidateAutocompleteCache(DatabaseName);
            await CacheAndRefreshAutocompleteAsync();
        }

        private void RequestAutocompleteMetadata()
        {
            if (_metadataRequested || _isDisposed) return;
            _metadataRequested = true;
            _ = CacheAndRefreshAutocompleteAsync();
        }

        private async Task LoadCrossDatabaseMetadataAsync(string databaseName)
        {
            try
            {
                var columns = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                var objectTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string dbConnectionString = DatabaseHelper.BuildConnectionString(ConnectionString, databaseName);
                using var connection = new Microsoft.Data.SqlClient.SqlConnection(dbConnectionString);
                await connection.OpenAsync();
                const string query = @"
                    SELECT s.name, o.name, c.name,
                           CASE WHEN o.type = 'V' THEN 'View' ELSE 'Table' END
                    FROM sys.objects o
                    JOIN sys.schemas s ON o.schema_id = s.schema_id
                    JOIN sys.columns c ON o.object_id = c.object_id
                    WHERE o.type IN ('U', 'V')
                    ORDER BY s.name, o.name, c.column_id;";
                using var command = new Microsoft.Data.SqlClient.SqlCommand(query, connection);
                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    string key = $"{reader.GetString(0)}.{reader.GetString(1)}";
                    if (!columns.TryGetValue(key, out var objectColumns))
                    {
                        objectColumns = new List<string>();
                        columns[key] = objectColumns;
                    }
                    objectColumns.Add(reader.GetString(2));
                    objectTypes[key] = reader.GetString(3);
                }

                var scalarFunctions =
                    await DatabaseHelper.GetFunctionsAsync(ConnectionString, databaseName, tableValued: false);
                var tableFunctions =
                    await DatabaseHelper.GetFunctionsAsync(ConnectionString, databaseName, tableValued: true);
                var payload = new { columns, objectTypes, scalarFunctions, tableFunctions };
                string jsonDatabase = JsonSerializer.Serialize(databaseName);
                string jsonPayload = JsonSerializer.Serialize(payload);
                await SqlEditorWebView.ExecuteScriptAsync(
                    $"updateDatabaseMetadata({jsonDatabase}, {jsonPayload});");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"Failed to load cross-database autocomplete metadata for '{databaseName}'");
            }
        }
    }
}