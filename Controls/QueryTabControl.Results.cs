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

        private void DisplayQueryResults(List<DataTable> dataTables)
        {
            DisposeDisplayedResults();

            if (dataTables == null || dataTables.Count == 0)
            {
                return;
            }

            for (int i = 0; i < dataTables.Count; i++)
            {
                _resultTables.Add(dataTables[i]);
                ResultsGridContainer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                var grid = CreateResultDataGrid(dataTables[i], i);
                Grid.SetRow(grid, ResultsGridContainer.RowDefinitions.Count - 1);
                ResultsGridContainer.Children.Add(grid);

                if (i < dataTables.Count - 1)
                {
                    ResultsGridContainer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    
                    var splitter = new GridSplitter
                    {
                        Height = 4,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        VerticalAlignment = VerticalAlignment.Center,
                        Background = (System.Windows.Media.SolidColorBrush)new System.Windows.Media.BrushConverter().ConvertFromString("#2D2D30")!
                    };
                    
                    Grid.SetRow(splitter, ResultsGridContainer.RowDefinitions.Count - 1);
                    ResultsGridContainer.Children.Add(splitter);
                }
            }
        }

        private FrameworkElement CreateResultDataGrid(DataTable dataTable, int resultIndex)
        {
            var dataGrid = new BufferedDataGridView
            {
                AutoGenerateColumns = false,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToOrderColumns = false,
                AllowUserToResizeRows = false,
                AutoSizeColumnsMode = WinForms.DataGridViewAutoSizeColumnsMode.None,
                AutoSizeRowsMode = WinForms.DataGridViewAutoSizeRowsMode.None,
                BackgroundColor = Drawing.Color.FromArgb(30, 30, 30),
                BorderStyle = WinForms.BorderStyle.None,
                CellBorderStyle = WinForms.DataGridViewCellBorderStyle.Single,
                ColumnHeadersBorderStyle = WinForms.DataGridViewHeaderBorderStyle.Single,
                ColumnHeadersHeight = 28,
                ColumnHeadersHeightSizeMode = WinForms.DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                Dock = WinForms.DockStyle.Fill,
                EnableHeadersVisualStyles = false,
                GridColor = Drawing.Color.FromArgb(72, 72, 76),
                MultiSelect = true,
                RowHeadersBorderStyle = WinForms.DataGridViewHeaderBorderStyle.Single,
                RowHeadersWidth = 46,
                RowHeadersWidthSizeMode = WinForms.DataGridViewRowHeadersWidthSizeMode.DisableResizing,
                ScrollBars = WinForms.ScrollBars.None,
                SelectionMode = WinForms.DataGridViewSelectionMode.CellSelect,
                ShowCellErrors = false,
                ShowEditingIcon = false,
                ShowRowErrors = false
            };

            dataGrid.Font = new Drawing.Font("Segoe UI", 9F, Drawing.FontStyle.Regular, Drawing.GraphicsUnit.Point);
            dataGrid.DefaultCellStyle = new WinForms.DataGridViewCellStyle
            {
                BackColor = Drawing.Color.FromArgb(30, 30, 30),
                ForeColor = Drawing.Color.FromArgb(204, 204, 204),
                SelectionBackColor = Drawing.Color.FromArgb(30, 58, 95),
                SelectionForeColor = Drawing.Color.White,
                Padding = new WinForms.Padding(5, 0, 5, 0),
                NullValue = "NULL"
            };
            dataGrid.AlternatingRowsDefaultCellStyle = new WinForms.DataGridViewCellStyle
            {
                BackColor = Drawing.Color.FromArgb(37, 37, 38),
                ForeColor = Drawing.Color.FromArgb(204, 204, 204),
                SelectionBackColor = Drawing.Color.FromArgb(30, 58, 95),
                SelectionForeColor = Drawing.Color.White
            };
            dataGrid.ColumnHeadersDefaultCellStyle = new WinForms.DataGridViewCellStyle
            {
                BackColor = Drawing.Color.FromArgb(37, 37, 38),
                ForeColor = Drawing.Color.FromArgb(241, 241, 241),
                SelectionBackColor = Drawing.Color.FromArgb(37, 37, 38),
                SelectionForeColor = Drawing.Color.White,
                Font = new Drawing.Font("Segoe UI", 9F, Drawing.FontStyle.Bold),
                Padding = new WinForms.Padding(5, 0, 5, 0)
            };
            dataGrid.RowHeadersDefaultCellStyle = new WinForms.DataGridViewCellStyle
            {
                BackColor = Drawing.Color.FromArgb(37, 37, 38),
                ForeColor = Drawing.Color.FromArgb(136, 136, 136),
                SelectionBackColor = Drawing.Color.FromArgb(45, 45, 48),
                SelectionForeColor = Drawing.Color.White,
                Alignment = WinForms.DataGridViewContentAlignment.MiddleRight
            };
            dataGrid.RowTemplate.Height = 24;

            foreach (DataColumn column in dataTable.Columns)
            {
                dataGrid.Columns.Add(new WinForms.DataGridViewTextBoxColumn
                {
                    DataPropertyName = column.ColumnName,
                    HeaderText = column.ColumnName,
                    Name = column.ColumnName,
                    SortMode = WinForms.DataGridViewColumnSortMode.NotSortable,
                    ValueType = column.DataType,
                    Width = 120
                });
            }

            var verticalScrollBar = new System.Windows.Controls.Primitives.ScrollBar
            {
                Orientation = System.Windows.Controls.Orientation.Vertical,
                Minimum = 0,
                SmallChange = 1,
                Width = 8
            };
            var horizontalScrollBar = new System.Windows.Controls.Primitives.ScrollBar
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Minimum = 0,
                SmallChange = 24,
                Height = 8
            };

            int lastHorizontalOffset = 0;
            bool synchronizingScrollBars = false;
            bool scrollBarUpdateScheduled = false;

            void UpdateScrollBars()
            {
                if (synchronizingScrollBars || !dataGrid.IsHandleCreated || dataGrid.RowCount == 0)
                {
                    return;
                }

                synchronizingScrollBars = true;
                try
                {
                    int displayedRows = Math.Max(1, dataGrid.DisplayedRowCount(false));
                    int maximumFirstRow = Math.Max(0, dataGrid.RowCount - displayedRows);
                    int firstRow = Math.Max(0, dataGrid.FirstDisplayedScrollingRowIndex);
                    verticalScrollBar.Maximum = maximumFirstRow;
                    verticalScrollBar.ViewportSize = displayedRows;
                    verticalScrollBar.LargeChange = displayedRows;
                    verticalScrollBar.Value = Math.Min(maximumFirstRow, firstRow);
                    verticalScrollBar.IsEnabled = maximumFirstRow > 0;

                    int totalColumnWidth = dataGrid.Columns.GetColumnsWidth(
                        WinForms.DataGridViewElementStates.Visible);
                    int rowHeaderWidth = dataGrid.RowHeadersVisible ? dataGrid.RowHeadersWidth : 0;
                    int viewportWidth = Math.Max(0, dataGrid.DisplayRectangle.Width - rowHeaderWidth);
                    int maximumHorizontalOffset = Math.Max(0, totalColumnWidth - viewportWidth);
                    horizontalScrollBar.Maximum = maximumHorizontalOffset;
                    horizontalScrollBar.ViewportSize = viewportWidth;
                    horizontalScrollBar.LargeChange = Math.Max(24, viewportWidth);

                    int targetOffset = Math.Clamp(lastHorizontalOffset, 0, maximumHorizontalOffset);
                    if (dataGrid.HorizontalScrollingOffset != targetOffset)
                    {
                        dataGrid.HorizontalScrollingOffset = targetOffset;
                    }
                    horizontalScrollBar.Value = targetOffset;
                    horizontalScrollBar.IsEnabled = maximumHorizontalOffset > 0;
                }
                finally
                {
                    synchronizingScrollBars = false;
                }
            }

            void ScheduleScrollBarUpdate()
            {
                if (scrollBarUpdateScheduled)
                {
                    return;
                }

                scrollBarUpdateScheduled = true;
                Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Render,
                    new Action(() =>
                    {
                        scrollBarUpdateScheduled = false;
                        UpdateScrollBars();
                    }));
            }

            dataGrid.CellFormatting += (_, e) => FormatResultCell(e);
            dataGrid.CellPainting += (_, e) => PaintRowNumber(dataGrid, e);
            dataGrid.ColumnDividerDoubleClick += (_, e) =>
            {
                if (e.ColumnIndex < 0 || e.ColumnIndex >= dataGrid.Columns.Count)
                {
                    return;
                }

                e.Handled = true;
                int savedOffset = lastHorizontalOffset;
                dataGrid.AutoResizeColumn(
                    e.ColumnIndex,
                    WinForms.DataGridViewAutoSizeColumnMode.DisplayedCells);
                lastHorizontalOffset = savedOffset;
                ScheduleScrollBarUpdate();
            };
            int rowSelectionAnchor = -1;
            bool isDraggingRowSelection = false;
            List<(int RowIndex, int ColumnIndex)>? rightClickSelectionSnapshot = null;
            bool rightClickWasOnSelection = false;

            void CaptureSelectionSnapshot()
            {
                rightClickSelectionSnapshot = dataGrid.SelectedCells
                    .Cast<WinForms.DataGridViewCell>()
                    .Select(cell => (cell.RowIndex, cell.ColumnIndex))
                    .ToList();
            }

            void RestoreSelectionSnapshot()
            {
                if (rightClickSelectionSnapshot == null || rightClickSelectionSnapshot.Count == 0)
                {
                    return;
                }

                dataGrid.ClearSelection();
                foreach (var (rowIndex, columnIndex) in rightClickSelectionSnapshot)
                {
                    if (rowIndex < 0 || rowIndex >= dataGrid.RowCount ||
                        columnIndex < 0 || columnIndex >= dataGrid.ColumnCount)
                    {
                        continue;
                    }

                    dataGrid.Rows[rowIndex].Cells[columnIndex].Selected = true;
                }

                var firstSelected = rightClickSelectionSnapshot[0];
                if (firstSelected.RowIndex >= 0 && firstSelected.RowIndex < dataGrid.RowCount &&
                    firstSelected.ColumnIndex >= 0 && firstSelected.ColumnIndex < dataGrid.ColumnCount)
                {
                    dataGrid.CurrentCell = dataGrid.Rows[firstSelected.RowIndex].Cells[firstSelected.ColumnIndex];
                }
            }

            void SelectRowRange(int targetRow)
            {
                if (rowSelectionAnchor < 0 || targetRow < 0)
                {
                    return;
                }

                dataGrid.ClearSelection();
                int firstRow = Math.Min(rowSelectionAnchor, targetRow);
                int lastRow = Math.Max(rowSelectionAnchor, targetRow);
                if (dataGrid.Rows[targetRow].Cells.Count > 0)
                {
                    dataGrid.CurrentCell = dataGrid.Rows[targetRow].Cells[0];
                }

                for (int rowIndex = firstRow; rowIndex <= lastRow; rowIndex++)
                {
                    foreach (WinForms.DataGridViewCell cell in dataGrid.Rows[rowIndex].Cells)
                    {
                        cell.Selected = true;
                    }
                }
            }

            dataGrid.CellMouseDown += (_, e) =>
            {
                if (e.Button == WinForms.MouseButtons.Right && e.RowIndex >= 0 && e.ColumnIndex >= 0)
                {
                    var clickedCell = dataGrid.Rows[e.RowIndex].Cells[e.ColumnIndex];
                    rightClickWasOnSelection = clickedCell.Selected;

                    if (rightClickWasOnSelection)
                    {
                        // Keep the whole selection available for Copy, just like SSMS.
                        CaptureSelectionSnapshot();
                        dataGrid.CurrentCell = clickedCell;
                    }
                    else
                    {
                        rightClickSelectionSnapshot = null;
                        dataGrid.ClearSelection();
                        clickedCell.Selected = true;
                        dataGrid.CurrentCell = clickedCell;
                    }

                    return;
                }

                if (e.RowIndex < 0 || e.ColumnIndex != -1 || e.Button != WinForms.MouseButtons.Left)
                {
                    return;
                }

                if ((WinForms.Control.ModifierKeys & WinForms.Keys.Shift) == 0 || rowSelectionAnchor < 0)
                {
                    rowSelectionAnchor = e.RowIndex;
                }
                isDraggingRowSelection = true;
                dataGrid.Capture = true;
                SelectRowRange(e.RowIndex);
            };
            dataGrid.MouseMove += (_, e) =>
            {
                if (!isDraggingRowSelection || (e.Button & WinForms.MouseButtons.Left) == 0)
                {
                    return;
                }

                var hit = dataGrid.HitTest(e.X, e.Y);
                if (hit.RowIndex >= 0)
                {
                    SelectRowRange(hit.RowIndex);
                }
                else if (dataGrid.RowCount > 0 && e.Y < dataGrid.ColumnHeadersHeight && dataGrid.FirstDisplayedScrollingRowIndex > 0)
                {
                    int targetRow = dataGrid.FirstDisplayedScrollingRowIndex - 1;
                    dataGrid.FirstDisplayedScrollingRowIndex = targetRow;
                    SelectRowRange(targetRow);
                }
                else if (dataGrid.RowCount > 0 && e.Y > dataGrid.ClientSize.Height)
                {
                    int lastDisplayedRow = Math.Min(
                        dataGrid.RowCount - 1,
                        dataGrid.FirstDisplayedScrollingRowIndex + dataGrid.DisplayedRowCount(false) - 1);
                    if (lastDisplayedRow < dataGrid.RowCount - 1)
                    {
                        int targetRow = lastDisplayedRow + 1;
                        dataGrid.FirstDisplayedScrollingRowIndex = Math.Min(
                            targetRow,
                            Math.Max(0, dataGrid.RowCount - dataGrid.DisplayedRowCount(false)));
                        SelectRowRange(targetRow);
                    }
                }
            };
            dataGrid.MouseUp += (_, e) =>
            {
                if (e.Button == WinForms.MouseButtons.Left)
                {
                    isDraggingRowSelection = false;
                    dataGrid.Capture = false;
                }
            };

            var contextMenu = new WinForms.ContextMenuStrip
            {
                BackColor = Drawing.Color.FromArgb(30, 30, 30),
                ForeColor = Drawing.Color.FromArgb(204, 204, 204),
                ShowImageMargin = false
            };
            contextMenu.Items.Add("Copy", null, (_, _) => CopyGridToClipboard(dataGrid, false));
            contextMenu.Items.Add("Copy with Headers", null, (_, _) => CopyGridToClipboard(dataGrid, true));
            var exportMenu = new WinForms.ToolStripMenuItem("Export Results");
            exportMenu.DropDownItems.Add("CSV...", null, (_, _) => ExportResultTable(dataTable, resultIndex, ResultExportFormat.Csv));
            exportMenu.DropDownItems.Add("Tab-delimited Text...", null, (_, _) => ExportResultTable(dataTable, resultIndex, ResultExportFormat.Tsv));
            exportMenu.DropDownItems.Add("JSON...", null, (_, _) => ExportResultTable(dataTable, resultIndex, ResultExportFormat.Json));
            exportMenu.DropDownItems.Add("XML...", null, (_, _) => ExportResultTable(dataTable, resultIndex, ResultExportFormat.Xml));
            contextMenu.Items.Add(exportMenu);
            contextMenu.Items.Add(new WinForms.ToolStripSeparator());
            contextMenu.Items.Add("Auto Fit Column", null, (_, _) =>
            {
                int columnIndex = dataGrid.CurrentCell?.ColumnIndex ?? -1;
                if (columnIndex >= 0)
                {
                    int savedOffset = lastHorizontalOffset;
                    dataGrid.AutoResizeColumn(
                        columnIndex,
                        WinForms.DataGridViewAutoSizeColumnMode.DisplayedCells);
                    lastHorizontalOffset = savedOffset;
                    ScheduleScrollBarUpdate();
                }
            });
            contextMenu.Items.Add("Widen Column (+200 px)", null, (_, _) =>
            {
                int columnIndex = dataGrid.CurrentCell?.ColumnIndex ?? -1;
                if (columnIndex >= 0)
                {
                    int savedOffset = lastHorizontalOffset;
                    var column = dataGrid.Columns[columnIndex];
                    column.Width = Math.Min(10000, column.Width + 200);
                    lastHorizontalOffset = savedOffset;
                    ScheduleScrollBarUpdate();
                }
            });
            contextMenu.Opening += (_, _) =>
            {
                if (rightClickWasOnSelection)
                {
                    RestoreSelectionSnapshot();
                }
            };
            contextMenu.Closed += (_, _) =>
            {
                rightClickSelectionSnapshot = null;
                rightClickWasOnSelection = false;
            };
            dataGrid.ContextMenuStrip = contextMenu;
            dataGrid.DataSource = dataTable;
            _resultGrids.Add(dataGrid);

            var host = new WindowsFormsHost
            {
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#1E1E1E")!,
                Child = dataGrid
            };
            _resultHosts.Add(host);

            var container = new Grid
            {
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#1E1E1E")!
            };
            container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            container.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            container.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });

            Grid.SetColumn(host, 0);
            Grid.SetRow(host, 0);
            Grid.SetColumn(verticalScrollBar, 1);
            Grid.SetRow(verticalScrollBar, 0);
            Grid.SetColumn(horizontalScrollBar, 0);
            Grid.SetRow(horizontalScrollBar, 1);

            container.Children.Add(host);
            container.Children.Add(verticalScrollBar);
            container.Children.Add(horizontalScrollBar);
            var scrollCorner = new Border
            {
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#1E1E1E")!
            };
            Grid.SetColumn(scrollCorner, 1);
            Grid.SetRow(scrollCorner, 1);
            container.Children.Add(scrollCorner);

            verticalScrollBar.ValueChanged += (_, _) =>
            {
                if (synchronizingScrollBars || !dataGrid.IsHandleCreated || dataGrid.RowCount == 0)
                {
                    return;
                }

                int rowIndex = Math.Clamp((int)Math.Round(verticalScrollBar.Value), 0, dataGrid.RowCount - 1);
                if (dataGrid.FirstDisplayedScrollingRowIndex != rowIndex)
                {
                    dataGrid.FirstDisplayedScrollingRowIndex = rowIndex;
                }
            };
            horizontalScrollBar.ValueChanged += (_, _) =>
            {
                if (synchronizingScrollBars || !dataGrid.IsHandleCreated)
                {
                    return;
                }

                int newOffset = Math.Max(0, (int)Math.Round(horizontalScrollBar.Value));
                lastHorizontalOffset = newOffset;
                dataGrid.HorizontalScrollingOffset = newOffset;
            };

            dataGrid.VerticalWheelScrolled += (_, delta) =>
            {
                if ((WinForms.Control.ModifierKeys & WinForms.Keys.Shift) == WinForms.Keys.Shift)
                {
                    double horizontalTarget = horizontalScrollBar.Value - (delta / 120.0 * 48);
                    double clamped = Math.Clamp(
                        horizontalTarget,
                        horizontalScrollBar.Minimum,
                        horizontalScrollBar.Maximum);
                    lastHorizontalOffset = (int)Math.Round(clamped);
                    horizontalScrollBar.Value = clamped;
                    return;
                }

                int wheelLines = WinForms.SystemInformation.MouseWheelScrollLines;
                if (wheelLines <= 0)
                {
                    wheelLines = 3;
                }

                double verticalTarget = verticalScrollBar.Value - (delta / 120.0 * wheelLines);
                verticalScrollBar.Value = Math.Clamp(
                    verticalTarget,
                    verticalScrollBar.Minimum,
                    verticalScrollBar.Maximum);
            };
            dataGrid.HorizontalWheelScrolled += (_, delta) =>
            {
                double target = horizontalScrollBar.Value + (delta / 120.0 * 48);
                double clamped = Math.Clamp(
                    target,
                    horizontalScrollBar.Minimum,
                    horizontalScrollBar.Maximum);
                lastHorizontalOffset = (int)Math.Round(clamped);
                horizontalScrollBar.Value = clamped;
            };

            dataGrid.Scroll += (_, e) =>
            {
                if (synchronizingScrollBars) return;
                if (e.ScrollOrientation == WinForms.ScrollOrientation.HorizontalScroll)
                {
                    if (dataGrid.HorizontalScrollingOffset > 0 || lastHorizontalOffset == 0)
                    {
                        lastHorizontalOffset = dataGrid.HorizontalScrollingOffset;
                    }
                }
                UpdateScrollBars();
            };
            dataGrid.Resize += (_, _) => ScheduleScrollBarUpdate();
            dataGrid.ColumnWidthChanged += (_, _) =>
            {
                if (synchronizingScrollBars || !dataGrid.IsHandleCreated)
                {
                    return;
                }

                int totalColumnWidth = dataGrid.Columns.GetColumnsWidth(
                    WinForms.DataGridViewElementStates.Visible);
                int rowHeaderWidth = dataGrid.RowHeadersVisible ? dataGrid.RowHeadersWidth : 0;
                int viewportWidth = Math.Max(0, dataGrid.DisplayRectangle.Width - rowHeaderWidth);
                int maximumHorizontalOffset = Math.Max(0, totalColumnWidth - viewportWidth);

                int targetOffset = Math.Clamp(lastHorizontalOffset, 0, maximumHorizontalOffset);
                if (dataGrid.HorizontalScrollingOffset != targetOffset)
                {
                    dataGrid.HorizontalScrollingOffset = targetOffset;
                }

                ScheduleScrollBarUpdate();
            };
            dataGrid.DataBindingComplete += (_, _) => ScheduleScrollBarUpdate();
            container.Loaded += (_, _) => ScheduleScrollBarUpdate();
            container.SizeChanged += (_, _) => ScheduleScrollBarUpdate();

            return container;
        }

        private void DisposeDisplayedResults()
        {
            ResultsGridContainer.Children.Clear();
            ResultsGridContainer.RowDefinitions.Clear();
            foreach (var grid in _resultGrids)
            {
                try
                {
                    grid.DataSource = null;
                    grid.ContextMenuStrip?.Dispose();
                    grid.Dispose();
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, "Failed to dispose result grid");
                }
            }
            _resultGrids.Clear();
            foreach (var table in _resultTables)
            {
                table.Dispose();
            }
            _resultTables.Clear();

            foreach (var host in _resultHosts)
            {
                try
                {
                    host.Child = null;
                    host.Dispose();
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, "Failed to dispose WindowsFormsHost");
                }
            }
            _resultHosts.Clear();
        }

        private void ExportResultTable(DataTable table, int resultIndex, ResultExportFormat format)
        {
            try
            {
                string extension = format switch
                {
                    ResultExportFormat.Csv => "csv",
                    ResultExportFormat.Tsv => "txt",
                    ResultExportFormat.Json => "json",
                    ResultExportFormat.Xml => "xml",
                    _ => "txt"
                };
                string filter = format switch
                {
                    ResultExportFormat.Csv => "CSV File (*.csv)|*.csv",
                    ResultExportFormat.Tsv => "Tab-delimited Text (*.txt)|*.txt",
                    ResultExportFormat.Json => "JSON File (*.json)|*.json",
                    ResultExportFormat.Xml => "XML File (*.xml)|*.xml",
                    _ => "All Files (*.*)|*.*"
                };

                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = $"Export Result Set {resultIndex + 1}",
                    FileName = $"QueryResult_{resultIndex + 1}.{extension}",
                    DefaultExt = extension,
                    Filter = filter,
                    AddExtension = true
                };
                if (dialog.ShowDialog() != true) return;

                switch (format)
                {
                    case ResultExportFormat.Csv:
                        File.WriteAllText(dialog.FileName, BuildDelimitedText(table, ','), new System.Text.UTF8Encoding(true));
                        break;
                    case ResultExportFormat.Tsv:
                        File.WriteAllText(dialog.FileName, BuildDelimitedText(table, '\t'), new System.Text.UTF8Encoding(true));
                        break;
                    case ResultExportFormat.Json:
                        File.WriteAllText(dialog.FileName, BuildJson(table), new System.Text.UTF8Encoding(false));
                        break;
                    case ResultExportFormat.Xml:
                        DataTable xmlTable = table.Copy();
                        xmlTable.TableName = $"ResultSet{resultIndex + 1}";
                        xmlTable.WriteXml(dialog.FileName, XmlWriteMode.WriteSchema);
                        xmlTable.Dispose();
                        break;
                }

                if (Window.GetWindow(this) is MainWindow mainWindow)
                {
                    mainWindow.UpdateStatusText($"Result set {resultIndex + 1} exported to {Path.GetFileName(dialog.FileName)}");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Failed to export query result");
                MessageBox.Show($"Failed to export results: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}