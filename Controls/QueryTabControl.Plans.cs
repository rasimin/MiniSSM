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

        private void DisplayExecutionPlans(IReadOnlyList<string> plans, QueryExecutionMode mode)
        {
            for (int i = 0; i < plans.Count; i++)
            {
                string planXml = plans[i];
                var planTab = new TabItem
                {
                    Header = $"{(mode == QueryExecutionMode.EstimatedPlan ? "Estimated" : "Actual")} Plan {i + 1}",
                    Tag = new ExecutionPlanTabInfo(planXml),
                    ToolTip = "Execution plan XML. Save as .sqlplan to open graphically in SSMS."
                };

                var viewer = new ICSharpCode.AvalonEdit.TextEditor
                {
                    Text = planXml,
                    IsReadOnly = true,
                    ShowLineNumbers = true,
                    SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("XML"),
                    WordWrap = false,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#1E1E1E")!,
                    Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#D4D4D4")!,
                    LineNumbersForeground = (SolidColorBrush)new BrushConverter().ConvertFromString("#858585")!,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    Padding = new Thickness(10),
                    BorderThickness = new Thickness(0)
                };
                planTab.Content = CreateExecutionPlanContent(planXml, viewer);

                var contextMenu = new ContextMenu();
                var saveItem = new MenuItem { Header = "Save as .sqlplan..." };
                saveItem.Click += (_, _) => SaveExecutionPlan(planXml, i + 1);
                contextMenu.Items.Add(saveItem);
                var copyItem = new MenuItem { Header = "Copy XML" };
                copyItem.Click += (_, _) => Clipboard.SetText(planXml);
                contextMenu.Items.Add(copyItem);
                contextMenu.Items.Add(new Separator());
                var closeItem = new MenuItem { Header = "Close" };
                closeItem.Click += (_, _) => TabResults.Items.Remove(planTab);
                contextMenu.Items.Add(closeItem);
                planTab.ContextMenu = contextMenu;

                TabResults.Items.Add(planTab);
                if (i == 0)
                {
                    TabResults.SelectedItem = planTab;
                }
            }
        }

        private void SaveExecutionPlan(string planXml, int planNumber)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save Execution Plan",
                Filter = "SQL Server Execution Plan (*.sqlplan)|*.sqlplan|XML File (*.xml)|*.xml",
                FileName = $"ExecutionPlan_{planNumber}.sqlplan",
                AddExtension = true
            };
            if (dialog.ShowDialog() == true)
            {
                File.WriteAllText(dialog.FileName, planXml, System.Text.Encoding.UTF8);
            }
        }

        private void ClearExecutionPlanTabs()
        {
            foreach (TabItem tab in TabResults.Items.OfType<TabItem>()
                         .Where(item => item.Tag is ExecutionPlanTabInfo).ToList())
            {
                TabResults.Items.Remove(tab);
            }
        }
    }
}