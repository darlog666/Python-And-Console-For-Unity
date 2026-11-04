using System;
using System.Collections.Generic;
using System.IO;
using UnityAdvancedTerminal.Core;
using UnityAdvancedTerminal.Utility;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityAdvancedTerminal.UI.Components
{
    public sealed class EnvironmentTreeComponent
    {
        private const int VariableRetention = 64;
        private const int ProcessRetention = 32;
        private const string WorkspaceFolderName = "PythonScripts";

        private readonly ITerminalEngine engine;
        private readonly IScriptStorageManager storage;

        private readonly TreeView workspaceTree;
        private readonly ListView quickVariableList;
        private readonly ListView processMonitorList;

        private readonly List<TreeViewItemData<string>> treeRoots = new List<TreeViewItemData<string>>();

        private List<string> variableRows = new List<string>();
        private List<string> processRows = new List<string>();

        public EnvironmentTreeComponent(VisualElement host, ITerminalEngine engine,
            IScriptStorageManager storage)
        {
            this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
            this.storage = storage ?? throw new ArgumentNullException(nameof(storage));

            host.Add(BuildSectionHeader("Project Environment Explorer"));

            workspaceTree = new TreeView(MakeWorkspaceRow, 22, BindWorkspaceRow);
            workspaceTree.name = "workspace-tree";
            workspaceTree.selectionType = TreeViewSelectionType.Single;
            workspaceTree.onSelectionChanged += HandleWorkspaceSelection;
            host.Add(workspaceTree);

            Button refreshButton = MakeFlatButton("Refresh Workspace");
            refreshButton.clicked += ReloadInventory;
            host.Add(refreshButton);

            host.Add(BuildSectionHeader("Quick Variables"));
            quickVariableList = new ListView(variableRows, 20, MakeVariableRow, BindVariableRow);
            quickVariableList.selectionType = SelectionType.None;
            quickVariableList.AddToClassList("variable-list");
            host.Add(quickVariableList);

            host.Add(BuildSectionHeader("Process Monitor"));
            processMonitorList = new ListView(processRows, 20, MakeProcessRow, BindProcessRow);
            processMonitorList.selectionType = SelectionType.None;
            processMonitorList.AddToClassList("process-monitor");
            host.Add(processMonitorList);

            engine.QuickVariableChanged += HandleVariableChange;
            ReloadInventory();
        }

        public void Detach()
        {
            engine.QuickVariableChanged -= HandleVariableChange;
        }

        public void ReportExecution(PythonExecutionRecord record)
        {
            string row = $"#{record.ExecutionId} :: {record.Duration.TotalMilliseconds:F0} ms :: " +
                         $"{record.PeakWorkingSetBytes / (1024 * 1024)} MB :: exit {record.ExitCode}";

            processRows.Insert(0, row);

            while (processRows.Count > ProcessRetention)
            {
                processRows.RemoveAt(processRows.Count - 1);
            }

            Rebind(processMonitorList, processRows);
        }

        private void ReloadInventory()
        {
            engine.RefreshEnvironmentTree();
            treeRoots.Clear();
            treeRoots.AddRange(BuildDirectoryNodes());
            workspaceTree.rootElements = treeRoots;
            workspaceTree.Reload();
        }

        private IEnumerable<TreeViewItemData<string>> BuildDirectoryNodes()
        {
            foreach (StoredScript script in storage.EnumerateScripts())
            {
                yield return new TreeViewItemData<string>(
                    script.FullPath.GetHashCode(),
                    $"{script.FileName} ({script.LineCount} lines)");
            }
        }

        private VisualElement MakeWorkspaceRow(int index)
        {
            Label label = new Label();
            label.AddToClassList("tree-row");
            return label;
        }

        private void BindWorkspaceRow(VisualElement element, int index)
        {
            ((Label)element).text = ResolveNodeText(index);
        }

        private string ResolveNodeText(int index)
        {
            if (index < 0 || index >= treeRoots.Count)
            {
                return string.Empty;
            }

            return treeRoots[index].GetData();
        }

        private void HandleWorkspaceSelection(IEnumerable<object> selection)
        {
            foreach (object item in selection)
            {
                string row = item?.ToString() ?? string.Empty;
                int cut = row.IndexOf(' ');
                string fileName = cut > 0 ? row.Substring(0, cut) : row;

                if (!string.IsNullOrEmpty(fileName))
                {
                    OpenScriptInEditor(fileName);
                }
            }
        }

        private void OpenScriptInEditor(string fileName)
        {
            string assetPath = ToAssetPath(Path.Combine(storage.WorkspaceRoot, fileName));

            if (assetPath == null)
            {
                return;
            }

            TextAsset asset = AssetDatabase.LoadAssetAtPath<TextAsset>(assetPath);

            if (asset != null)
            {
                EditorGUIUtility.PingObject(asset);
                AssetDatabase.OpenAsset(asset);
            }
        }

        private static string ToAssetPath(string absolutePath)
        {
            string normalized = absolutePath.Replace('\\', '/');
            string projectRoot = Directory.GetCurrentDirectory().Replace('\\', '/').TrimEnd('/') + "/";

            if (!normalized.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string relative = normalized.Substring(projectRoot.Length);
            int marker = relative.IndexOf(WorkspaceFolderName + "/", StringComparison.Ordinal);

            return marker < 0 ? null : relative.Substring(0, marker + WorkspaceFolderName.Length);
        }

        private VisualElement MakeVariableRow(int index)
        {
            Label label = new Label();
            label.AddToClassList("monitor-row");
            return label;
        }

        private void BindVariableRow(VisualElement element, int index)
        {
            ((Label)element).text = variableRows[index];
        }

        private VisualElement MakeProcessRow(int index)
        {
            Label label = new Label();
            label.AddToClassList("monitor-row");
            return label;
        }

        private void BindProcessRow(VisualElement element, int index)
        {
            ((Label)element).text = processRows[index];
        }

        private void HandleVariableChange(string descriptor)
        {
            string name = descriptor.Split('=')[0];
            variableRows.RemoveAll(existing => existing.Split('=')[0] == name);
            variableRows.Insert(0, descriptor);

            while (variableRows.Count > VariableRetention)
            {
                variableRows.RemoveAt(variableRows.Count - 1);
            }

            Rebind(quickVariableList, variableRows);
        }

        private static void Rebind(ListView list, List<string> rows)
        {
            list.itemsSource = null;
            list.itemsSource = rows;
            list.RefreshItems();
        }

        private static Label BuildSectionHeader(string caption)
        {
            Label header = new Label(caption.ToUpperInvariant());
            header.AddToClassList("section-header");
            return header;
        }

        private static Button MakeFlatButton(string caption)
        {
            Button button = new Button { text = caption };
            button.AddToClassList("flat-button");
            return button;
        }
    }
}
