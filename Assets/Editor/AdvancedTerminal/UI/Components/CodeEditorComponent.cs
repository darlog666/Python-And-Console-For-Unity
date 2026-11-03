using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AdvancedTerminal.Core;
using AdvancedTerminal.Utility;
using UnityEditor;
using UnityEngine.UIElements;

namespace AdvancedTerminal.UI.Components
{
    public sealed class CodeEditorComponent
    {
        private const int SoftTabWidth = 4;

        private static readonly Dictionary<string, string> ClosingPairs = new Dictionary<string, string>
        {
            { "(", ")" },
            { "[", "]" },
            { "{", "}" },
            { "\"", "\"" },
            { "'", "'" }
        };

        private static readonly Regex keywordPattern = new Regex(
            @"\b(def|class|import|from|return|if|elif|else|for|while|print|try|except|finally|with|as|lambda|yield|pass)\b",
            RegexOptions.Compiled);

        private static readonly Regex builtinPattern = new Regex(
            @"\b(True|False|None|self|len|range|str|int|float|list|dict|set|tuple|open|enumerate|zip|map|filter)\b",
            RegexOptions.Compiled);

        private static readonly Regex literalPattern = new Regex(
            @"(\"\"\"[\s\S]*?\"\"\"|'''[\s\S]*?'''|""[^""]*""|'[^']*'|\b\d+(?:\.\d+)?\b)",
            RegexOptions.Compiled);

        private static readonly Regex commentPattern = new Regex(
            @"#[^\n]*",
            RegexOptions.Compiled);

        private readonly ITerminalEngine engine;
        private readonly IScriptStorageManager storage;

        private readonly VisualElement toolbar;
        private readonly ScrollView gutterScroll;
        private readonly VisualElement highlightLayer;
        private readonly TextField codeField;
        private readonly Label caretReadout;
        private readonly Foldout ideFoldout;

        private string activeFileName = "scratch.py";
        private bool collapsed;

        public CodeEditorComponent(VisualElement host, ITerminalEngine engine,
            IScriptStorageManager storage)
        {
            this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
            this.storage = storage ?? throw new ArgumentNullException(nameof(storage));

            ideFoldout = new Foldout { text = "PYTHON IDE", value = true };
            ideFoldout.AddToClassList("ide-foldout");
            ideFoldout.RegisterValueChangedCallback(HandleCollapseToggle);

            toolbar = BuildToolbar();
            host.Add(ideFoldout);
            ideFoldout.Add(toolbar);

            highlightLayer = new VisualElement();
            highlightLayer.name = "highlight-layer";
            highlightLayer.AddToClassList("highlight-layer");

            codeField = new TextField { multiline = true, isDelayed = false };
            codeField.name = "python-code-field";
            codeField.AddToClassList("code-field");
            codeField.value = ReadActiveFile();
            codeField.RegisterCallback<KeyDownEvent>(HandleCodeKey);
            codeField.RegisterValueChangedCallback(HandleCodeMutation);

            gutterScroll = new ScrollView(Orientation.Vertical);
            gutterScroll.AddToClassList("gutter-scroll");
            gutterScroll.Add(highlightLayer);
            gutterScroll.Add(codeField);
            ideFoldout.Add(gutterScroll);

            caretReadout = new Label("Ln 1 | Col 1 | scratch.py");
            caretReadout.AddToClassList("caret-readout");
            ideFoldout.Add(caretReadout);

            RefreshHighlight();
        }

        public void LoadFile(string fileName)
        {
            activeFileName = fileName;
            codeField.SetValueWithoutNotify(storage.ReadContent(fileName));
            RefreshHighlight();
            UpdateCaretReadout();
        }

        public string ActiveSource => codeField.value;

        public void RunActiveBuffer()
        {
            _ = engine.RunPythonAsync(codeField.value);
        }

        public void SaveActiveBuffer()
        {
            StoredScript saved = storage.SaveContent(activeFileName, codeField.value);
            engine.Emit($"Saved {saved.FileName} ({saved.ByteSize} bytes).", TerminalLogLevelSuccessProxy());
            AssetDatabase.Refresh();
        }

        public void FormatAgainstPep8()
        {
            string source = codeField.value;
            List<string> diagnostics = new List<string>();

            string[] lines = source.Replace("\r\n", "\n").Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains('\t'))
                {
                    diagnostics.Add($"Line {i + 1}: tab character detected, expected soft tabs.");
                }

                if (lines[i].Length > 79)
                {
                    diagnostics.Add($"Line {i + 1}: exceeds PEP8 column ceiling of 79.");
                }

                if (lines[i].EndsWith(" "))
                {
                    diagnostics.Add($"Line {i + 1}: trailing whitespace.");
                }
            }

            if (diagnostics.Count == 0)
            {
                engine.Emit("PEP8 audit clean.", TerminalLogLevelSuccessProxy());
                return;
            }

            foreach (string diagnostic in diagnostics)
            {
                engine.Emit(diagnostic, TerminalLogLevelWarningProxy());
            }
        }

        private VisualElement BuildToolbar()
        {
            VisualElement bar = new VisualElement();
            bar.AddToClassList("ide-toolbar");

            bar.Add(MakeToolButton("Run (Ctrl+Enter)", RunActiveBuffer));
            bar.Add(MakeToolButton("Save (Ctrl+S)", SaveActiveBuffer));
            bar.Add(MakeToolButton("Format Code (PEP8 check)", FormatAgainstPep8));
            bar.Add(MakeToolButton("Collapse Panel", ToggleCollapse));

            return bar;
        }

        private Button MakeToolButton(string caption, Action action)
        {
            Button button = new Button(action) { text = caption };
            button.AddToClassList("tool-button");
            return button;
        }

        private void HandleCollapseToggle(ChangeEvent<bool> args)
        {
            collapsed = !args.newValue;
            gutterScroll.style.display = collapsed ? DisplayStyle.None : DisplayStyle.Flex;
            caretReadout.style.display = collapsed ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private void ToggleCollapse()
        {
            ideFoldout.value = !ideFoldout.value;
        }

        private void HandleCodeKey(KeyDownEvent args)
        {
            if (args.ctrl && args.keyCode == KeyCode.Return)
            {
                RunActiveBuffer();
                args.StopPropagation();
                return;
            }

            if (args.ctrl && args.keyCode == KeyCode.S)
            {
                SaveActiveBuffer();
                args.StopPropagation();
                return;
            }

            if (args.keyCode == KeyCode.Tab)
            {
                InsertSoftTab();
                args.StopPropagation();
                return;
            }

            if (ClosingPairs.TryGetValue(args.character.ToString(), out string closing))
            {
                InsertBalancedPair(args.character.ToString(), closing);
                args.StopPropagation();
            }
        }

        private void InsertSoftTab()
        {
            MutateSelection(new string(' ', SoftTabWidth), 0);
        }

        private void InsertBalancedPair(string opening, string closing)
        {
            MutateSelection(opening + closing, closing.Length);
        }

        private void MutateSelection(string insertion, int reverseOffset)
        {
            int cursor = ResolveCursor();
            string source = codeField.value ?? string.Empty;
            string updated = source.Insert(cursor, insertion);
            codeField.SetValueWithoutNotify(updated);
            MoveCursor(cursor + insertion.Length - reverseOffset);
            RefreshHighlight();
        }

        private int ResolveCursor()
        {
            return Math.Clamp(codeField.cursorIndex, 0, (codeField.value ?? string.Empty).Length);
        }

        private void MoveCursor(int target)
        {
            try
            {
                codeField.CursorPositionForNewlyFocusedTextField(target);
            }
            catch (InvalidOperationException)
            {
                caretReadout.tooltip = "Cursor reposition requires field focus.";
            }
        }

        private void HandleCodeMutation(ChangeEvent<string> args)
        {
            RefreshHighlight();
            UpdateCaretReadout();
        }

        private void RefreshHighlight()
        {
            highlightLayer.Clear();
            string source = codeField.value ?? string.Empty;
            string[] lines = source.Replace("\r\n", "\n").Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                Label row = new Label(TokenizeLine(lines[i]));
                row.enableRichText = true;
                row.AddToClassList("highlight-row");
                highlightLayer.Add(row);
            }
        }

        private string TokenizeLine(string line)
        {
            string guarded = EscapeMarkup(line);
            guarded = commentPattern.Replace(guarded, match => Tint(match.Value, "#5C6370"));
            guarded = literalPattern.Replace(guarded, match => Tint(match.Value, "#CE9178"));
            guarded = keywordPattern.Replace(guarded, match => Tint(match.Value, "#C586C0"));
            guarded = builtinPattern.Replace(guarded, match => Tint(match.Value, "#DCDCAA"));
            return guarded;
        }

        private static string Tint(string token, string hex)
        {
            return $"<color={hex}>{token}</color>";
        }

        private static string EscapeMarkup(string input)
        {
            return input.Replace("&", "\\u0026")
                        .Replace("<", "\\u003c")
                        .Replace(">", "\\u003e");
        }

        private void UpdateCaretReadout()
        {
            string source = codeField.value ?? string.Empty;
            int cursor = ResolveCursor();
            int line = 1;
            int column = 1;

            for (int i = 0; i < cursor && i < source.Length; i++)
            {
                if (source[i] == '\n')
                {
                    line++;
                    column = 1;
                    continue;
                }

                column++;
            }

            caretReadout.text = $"Ln {line} | Col {column} | {activeFileName}";
        }

        private string ReadActiveFile()
        {
            string content = storage.ReadContent(activeFileName);
            return string.IsNullOrEmpty(content) ? "print('Hello Unity Terminal')" : content;
        }

        private static Architecture.Observers.TerminalLogLevel TerminalLogLevelSuccessProxy()
        {
            return Architecture.Observers.TerminalLogLevel.Success;
        }

        private static Architecture.Observers.TerminalLogLevel TerminalLogLevelWarningProxy()
        {
            return Architecture.Observers.TerminalLogLevel.Warning;
        }
    }
}
