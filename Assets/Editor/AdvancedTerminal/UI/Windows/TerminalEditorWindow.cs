using System;
using System.Collections.Generic;
using System.IO;
using AdvancedTerminal.Architecture.Pipelines;
using AdvancedTerminal.Core;
using AdvancedTerminal.UI.Components;
using AdvancedTerminal.Utility;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AdvancedTerminal.UI.Windows
{
    public sealed class TerminalEditorWindow : EditorWindow, ITerminalLogObserver
    {
        private const string UxmlPath =
            "Assets/Editor/AdvancedTerminal/UI/Styling/TerminalLayout.uxml";

        private static readonly string WorkspaceRoot =
            Path.Combine(Directory.GetCurrentDirectory(), "Assets", "Editor", "PythonScripts");

        private static readonly Dictionary<string, string> ExportFormats =
            new Dictionary<string, string>
            {
                { "JSON (.json)", ".json" },
                { "Formatted Text (.txt)", ".txt" }
            };

        private ITerminalEngine engine;
        private IScriptStorageManager storage;

        private VisualElement macroBinderBar;
        private Toggle logMirrorToggle;
        private DropdownField exportDropdown;
        private Button killSwitchButton;
        private Button addMacroButton;

        private EnvironmentTreeComponent environmentTree;
        private ConsoleViewportComponent consoleViewport;
        private CodeEditorComponent codeEditor;

        [MenuItem("Window/Advanced Terminal %#`")]
        public static void OpenWorkspace()
        {
            TerminalEditorWindow window = GetWindow<TerminalEditorWindow>();
            window.titleContent = new GUIContent("Advanced Terminal");
            window.minSize = new Vector2(1080f, 640f);
            window.Show();
        }

        private void OnEnable()
        {
            VisualTreeAsset tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);

            if (tree != null)
            {
                tree.Clone(rootVisualElement);
                AttachStylesheet();
            }
            else
            {
                BuildLayoutFallback();
            }

            ResolveCoreServices();
            BindToolbar();
            MountPanels();
            RebuildMacroButtons();

            engine.Observe(this);
        }

        private void AttachStylesheet()
        {
            StyleSheet sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Assets/Editor/AdvancedTerminal/UI/Styling/TerminalStyles.uss");

            if (sheet != null)
            {
                rootVisualElement.styleSheets.Add(sheet);
            }
        }

        private void OnDisable()
        {
            engine?.Unobserve(ObserverId);
            environmentTree?.Detach();
            consoleViewport?.Detach();
        }

        public string ObserverId => "terminal-editor-window";

        public bool Accepts(TerminalLogEntry entry)
        {
            return true;
        }

        public void OnLogEnqueued(TerminalLogEntry entry)
        {
        }

        public void OnBatchFlushed(TerminalLogEntry[] batch)
        {
        }

        public void OnBufferCleared()
        {
        }

        private void ResolveCoreServices()
        {
            TerminalServiceLocator locator = TerminalServiceLocator.Current;
            engine = locator.TryResolve<TerminalEngine>(out TerminalEngine resolved)
                ? resolved
                : throw new InvalidOperationException("TerminalEngine has not been initialized.");

            storage = locator.TryResolve<ScriptStorageManager>(out ScriptStorageManager stored)
                ? stored
                : locator.Register<ScriptStorageManager>(new ScriptStorageManager(WorkspaceRoot));
        }

        private void BindToolbar()
        {
            macroBinderBar = rootVisualElement.Q<VisualElement>("macro-binder-bar");
            logMirrorToggle = rootVisualElement.Q<Toggle>("log-mirror-toggle");
            exportDropdown = rootVisualElement.Q<DropdownField>("export-dropdown");
            killSwitchButton = rootVisualElement.Q<Button>("kill-switch-button");
            addMacroButton = rootVisualElement.Q<Button>("add-macro-button");

            logMirrorToggle.RegisterValueChangedCallback(args =>
                engine.ToggleLogMirror(args.newValue));

            exportDropdown.choices = new List<string>(ExportFormats.Keys);
            exportDropdown.RegisterValueChangedCallback(args => RunSessionExport(args.newValue));

            killSwitchButton.clicked += engine.KillSwitch;
            addMacroButton.clicked += PresentMacroWizard;
        }

        private void MountPanels()
        {
            VisualElement leftSidebar = rootVisualElement.Q<VisualElement>("left-sidebar");
            VisualElement centerPanel = rootVisualElement.Q<VisualElement>("center-panel");
            VisualElement rightPanel = rootVisualElement.Q<VisualElement>("right-panel");

            environmentTree = new EnvironmentTreeComponent(leftSidebar, engine, storage);
            consoleViewport = new ConsoleViewportComponent(centerPanel, engine);
            codeEditor = new CodeEditorComponent(rightPanel, engine, storage);

            consoleViewport.FocusInput();
        }

        private void RebuildMacroButtons()
        {
            macroBinderBar.Clear();

            foreach (KeyValuePair<string, string> binding in engine.MacroBindings)
            {
                string capturedName = binding.Key;
                Button macroButton = new Button(() => engine.ExecuteMacro(capturedName))
                {
                    text = binding.Key
                };
                macroButton.AddToClassList("macro-button");
                macroBinderBar.Add(macroButton);
            }
        }

        private void PresentMacroWizard()
        {
            IReadOnlyList<StoredScript> inventory = storage.EnumerateScripts();

            if (inventory.Count == 0)
            {
                engine.Emit("No Python scripts available to bind. Save a script first.",
                    TerminalLogLevel.Warning);
                return;
            }

            GenericMenu menu = new GenericMenu();

            foreach (StoredScript script in inventory)
            {
                StoredScript captured = script;
                menu.AddItem(new GUIContent(script.FileName), false, () =>
                {
                    engine.RegisterMacro(Path.GetFileNameWithoutExtension(captured.FileName),
                        captured.FullPath);
                    RebuildMacroButtons();
                });
            }

            menu.ShowAsContext();
        }

        private void RunSessionExport(string selectionKey)
        {
            if (string.IsNullOrEmpty(selectionKey) ||
                !ExportFormats.TryGetValue(selectionKey, out string extension))
            {
                return;
            }

            bool asJson = extension == ".json";
            string payload = engine.ExportSession(asJson);
            ITextProcessor pipeline = TerminalServiceLocator.Current.Resolve<ITextProcessor>();
            string sanitized = asJson ? payload : pipeline.StripRichTextMarkup(payload);
            string target = EditorUtility.SaveFilePanel(
                "Export Terminal Session", WorkspaceRoot,
                $"terminal-session-{DateTime.Now:yyyyMMdd-HHmmss}", extension.TrimStart('.'));

            if (string.IsNullOrEmpty(target))
            {
                return;
            }

            File.WriteAllText(target, sanitized);
            engine.Emit($"Session exported to {target}", TerminalLogLevel.Success);
            exportDropdown.SetValueWithoutNotify(string.Empty);
        }

        private void BuildLayoutFallback()
        {
            VisualElement root = new VisualElement();
            root.name = "terminal-root";
            root.AddToClassList("terminal-root");

            VisualElement toolbar = new VisualElement();
            toolbar.name = "terminal-toolbar";
            toolbar.AddToClassList("terminal-toolbar");

            macroBinderBar = new VisualElement();
            macroBinderBar.name = "macro-binder-bar";
            macroBinderBar.AddToClassList("macro-binder-bar");
            toolbar.Add(macroBinderBar);

            addMacroButton = new Button { name = "add-macro-button", text = "Add Macro" };
            addMacroButton.AddToClassList("tool-button");
            toolbar.Add(addMacroButton);

            logMirrorToggle = new Toggle { name = "log-mirror-toggle", label = "Unity Log Mirror" };
            logMirrorToggle.AddToClassList("tool-button");
            toolbar.Add(logMirrorToggle);

            exportDropdown = new DropdownField { name = "export-dropdown", label = "Export Session" };
            exportDropdown.AddToClassList("export-dropdown");
            toolbar.Add(exportDropdown);

            killSwitchButton = new Button { name = "kill-switch-button", text = "KILL" };
            killSwitchButton.AddToClassList("kill-switch-button");
            toolbar.Add(killSwitchButton);

            root.Add(toolbar);

            VisualElement body = new VisualElement();
            body.name = "terminal-body";
            body.AddToClassList("terminal-body");

            VisualElement leftSidebar = new VisualElement();
            leftSidebar.name = "left-sidebar";
            leftSidebar.AddToClassList("left-sidebar");

            VisualElement centerPanel = new VisualElement();
            centerPanel.name = "center-panel";
            centerPanel.AddToClassList("center-panel");

            VisualElement rightPanel = new VisualElement();
            rightPanel.name = "right-panel";
            rightPanel.AddToClassList("right-panel");

            body.Add(leftSidebar);
            body.Add(centerPanel);
            body.Add(rightPanel);
            root.Add(body);

            StyleSheet sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Assets/Editor/AdvancedTerminal/UI/Styling/TerminalStyles.uss");

            if (sheet != null)
            {
                root.styleSheets.Add(sheet);
            }

            rootVisualElement.Add(root);
        }
    }
}
