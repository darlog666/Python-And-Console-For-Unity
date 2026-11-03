using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using AdvancedTerminal.Architecture.Observers;
using AdvancedTerminal.Core;
using AdvancedTerminal.Utility;
using UnityEditor;
using UnityEngine.UIElements;

namespace AdvancedTerminal.UI.Components
{
    public sealed class CodeEditorComponent
    {
        private const int SoftTabWidth = 4;
        private const string DefaultScratchName = "scratch.py";
        private const char QuoteCharacter = '"';
        private const char ApostropheCharacter = '\'';

        private static readonly Dictionary<string, string> ClosingPairs = new Dictionary<string, string>
        {
            { "(", ")" },
            { "[", "]" },
            { "{", "}" },
            { QuoteCharacter.ToString(), QuoteCharacter.ToString() },
            { ApostropheCharacter.ToString(), ApostropheCharacter.ToString() }
        };

        private static readonly Regex keywordPattern = new Regex(
            @"\b(def|class|import|from|return|if|elif|else|for|while|print|try|except|finally|with|as|lambda|yield|pass|global|nonlocal|assert|raise|del|in|is|not|and|or|break|continue|async|await)\b",
            RegexOptions.Compiled);

        private static readonly Regex builtinPattern = new Regex(
            @"\b(True|False|None|self|len|range|str|int|float|list|dict|set|tuple|open|enumerate|zip|map|filter|sum|min|max|abs|round|type|isinstance|super|__init__)\b",
            RegexOptions.Compiled);

        private static readonly Regex literalPattern = new Regex(
            "(\"\"\"[\\s\\S]*?\"\"\"|'''[\\s\\S]*?'''|\"[^\"\\n]*\"|'[^'\\n]*'|\\b\\d+(?:\\.\\d+)?\\b)",
            RegexOptions.Compiled);

        private static readonly Regex commentPattern = new Regex(
            @"#[^\n]*",
            RegexOptions.Compiled);

        private static readonly Regex decoratorPattern = new Regex(
            @"(?m)^[\t ]*@[A-Za-z_][A-Za-z0-9_.]*",
            RegexOptions.Compiled);

        private static readonly Regex functionPattern = new Regex(
            @"\b(?:def|class)\s+([A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.Compiled);

        private const string KeywordHex = "#C586C0";
        private const string BuiltinHex = "#DCDCAA";
        private const string LiteralHex = "#CE9178";
        private const string CommentHex = "#5C6370";
        private const string DecoratorHex = "#D7BA7D";
        private const string FunctionHex = "#4EC9B0";

        private readonly ITerminalEngine engine;
        private readonly IScriptStorageManager storage;

        private readonly VisualElement toolbar;
        private readonly ScrollView editorScroll;
        private readonly VisualElement highlightLayer;
        private readonly TextField codeField;
        private readonly Label caretReadout;
        private readonly Foldout ideFoldout;

        private string activeFileName = DefaultScratchName;
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

            editorScroll = new ScrollView(Orientation.Vertical);
            editorScroll.AddToClassList("gutter-scroll");
            editorScroll.Add(highlightLayer);
            editorScroll.Add(codeField);
            ideFoldout.Add(editorScroll);

            caretReadout = new Label(BuildCaretText(1, 1));
            caretReadout.AddToClassList("caret-readout");
            ideFoldout.Add(caretReadout);

            RefreshHighlight();
        }

        public string ActiveSource => codeField.value;

        public string ActiveFileName => activeFileName;

        public void LoadFile(string fileName)
        {
            activeFileName = fileName;
            codeField.SetValueWithoutNotify(ReadActiveFile());
            RefreshHighlight();
            UpdateCaretReadout();
        }

        public void RunActiveBuffer()
        {
            _ = engine.RunPythonAsync(codeField.value);
        }

        public void SaveActiveBuffer()
        {
            StoredScript saved = storage.SaveContent(activeFileName, codeField.value);
            engine.Emit($"Saved {saved.FileName} ({saved.ByteSize} bytes).", TerminalLogLevel.Success);
            AssetDatabase.Refresh();
        }

        public void FormatAgainstPep8()
        {
            string source = codeField.value;
            List<string> diagnostics = new List<string>();

            string[] lines = NormalizeNewlines(source);

            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("\t"))
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
                engine.Emit("PEP8 audit clean.", TerminalLogLevel.Success);
                return;
            }

            foreach (string diagnostic in diagnostics)
            {
                engine.Emit(diagnostic, TerminalLogLevel.Warning);
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
            editorScroll.style.display = collapsed ? DisplayStyle.None : DisplayStyle.Flex;
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
            int length = (codeField.value ?? string.Empty).Length;
            return Math.Clamp(codeField.cursorIndex, 0, length);
        }

        private void MoveCursor(int target)
        {
            try
            {
                codeField.cursorIndex = target;
                codeField.selectIndex = target;
            }
            catch (ArgumentOutOfRangeException)
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
            string[] lines = NormalizeNewlines(codeField.value ?? string.Empty);

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
            guarded = commentPattern.Replace(guarded, match => Tint(match.Value, CommentHex));
            guarded = literalPattern.Replace(guarded, match => Tint(match.Value, LiteralHex));
            guarded = decoratorPattern.Replace(guarded, match => Tint(match.Value, DecoratorHex));
            guarded = functionPattern.Replace(guarded, match => TintFunctionName(match.Value));
            guarded = keywordPattern.Replace(guarded, match => Tint(match.Value, KeywordHex));
            guarded = builtinPattern.Replace(guarded, match => Tint(match.Value, BuiltinHex));
            return guarded;
        }

        private static string TintFunctionName(string declaration)
        {
            Match nameMatch = functionPattern.Match(declaration);

            if (!nameMatch.Success)
            {
                return Tint(declaration, KeywordHex);
            }

            string head = declaration.Substring(0, nameMatch.Groups[1].Index);
            string name = nameMatch.Groups[1].Value;
            string tail = declaration.Substring(nameMatch.Groups[1].Index + name.Length);
            return Tint(head.TrimEnd(), KeywordHex) + " " + Tint(name, FunctionHex) + tail;
        }

        private static string Tint(string token, string hex)
        {
            return $"<color={hex}>{token}</color>";
        }

        private static string EscapeMarkup(string input)
        {
            return input.Replace("&", "&amp;")
                        .Replace("<", "&lt;")
                        .Replace(">", "&gt;");
        }

        private static string[] NormalizeNewlines(string source)
        {
            return source.Replace("\r\n", "\n").Split('\n');
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

            caretReadout.text = BuildCaretText(line, column);
        }

        private string BuildCaretText(int line, int column)
        {
            return $"Ln {line} | Col {column} | {activeFileName}";
        }

        private string ReadActiveFile()
        {
            string content = storage.ReadContent(activeFileName);
            return string.IsNullOrEmpty(content) ? "print('Hello Unity Terminal')" : content;
        }
    }
}