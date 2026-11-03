using System;
using System.Collections.Generic;
using AdvancedTerminal.Architecture.Observers;
using AdvancedTerminal.Core;
using UnityEngine.UIElements;

namespace AdvancedTerminal.UI.Components
{
    public sealed class ConsoleViewportComponent : ITerminalLogObserver
    {
        private const int VirtualRowHeight = 18;
        private const int MaxRenderedSuggestions = 9;

        private readonly ITerminalEngine engine;
        private readonly ListView logList;
        private readonly TextField commandInput;
        private readonly VisualElement suggestionPopup;
        private readonly VisualElement statusStrip;

        private readonly List<TerminalLogEntry> renderedRows = new List<TerminalLogEntry>();
        private readonly List<string> historyRing = new List<string>();

        private string pendingSuggestionPrefix = string.Empty;
        private int historyCursor = -1;
        private bool pinnedToBottom = true;

        public ConsoleViewportComponent(VisualElement host, ITerminalEngine engine)
        {
            this.engine = engine ?? throw new ArgumentNullException(nameof(engine));

            logList = new ListView(renderedRows, VirtualRowHeight, MakeLogRow, BindLogRow);
            logList.name = "console-log-list";
            logList.virtualizationMethod = CollectionVirtualizationMethod.DynamicHeight;
            logList.AddToClassList("console-viewport");
            logList.RegisterCallback<ScrollWheelEvent>(HandleWheelPinning);
            host.Add(logList);

            suggestionPopup = BuildSuggestionPopup();
            host.Add(suggestionPopup);

            commandInput = new TextField { multiline = false };
            commandInput.name = "console-input";
            commandInput.AddToClassList("console-input");
            commandInput.RegisterValueChangedCallback(HandleInputMutation);
            commandInput.RegisterCallback<KeyDownEvent>(HandleInputKey);
            host.Add(commandInput);

            statusStrip = new VisualElement();
            statusStrip.AddToClassList("status-strip");
            statusStrip.Add(new Label("Rows: 0 | Backend: probing"));
            host.Add(statusStrip);

            engine.Observe(this);
            HydrateFromSnapshot();
        }

        public string ObserverId => "console-viewport";

        public void FocusInput()
        {
            commandInput.Focus();
        }

        public void Detach()
        {
            engine.Unobserve(ObserverId);
        }

        public bool Accepts(TerminalLogEntry entry)
        {
            return true;
        }

        public void OnLogEnqueued(TerminalLogEntry entry)
        {
        }

        public void OnBatchFlushed(TerminalLogEntry[] batch)
        {
            for (int i = 0; i < batch.Length; i++)
            {
                renderedRows.Add(batch[i]);
            }

            TrimRetention();
            RebindList();

            if (pinnedToBottom)
            {
                ScrollToEnd();
            }

            UpdateStatus();
        }

        public void OnBufferCleared()
        {
            renderedRows.Clear();
            RebindList();
            UpdateStatus();
        }

        private void HydrateFromSnapshot()
        {
            IReadOnlyList<TerminalLogEntry> snapshot = engine.Snapshot();

            foreach (TerminalLogEntry entry in snapshot)
            {
                renderedRows.Add(entry);
            }

            RebindList();
            ScrollToEnd();
            UpdateStatus();
        }

        private VisualElement MakeLogRow(int index)
        {
            Label row = new Label();
            row.enableRichText = true;
            row.AddToClassList("log-row");
            return row;
        }

        private void BindLogRow(VisualElement element, int index)
        {
            Label row = (Label)element;
            TerminalLogEntry entry = renderedRows[index];
            row.text = $"[{entry.Timestamp:HH:mm:ss}] {entry.Message}";
            row.style.color = PaletteFor(entry.Level);
        }

        private static UnityEngine.Color PaletteFor(TerminalLogLevel level)
        {
            switch (level)
            {
                case TerminalLogLevel.Input:
                    return new UnityEngine.Color(0f, 0.81f, 0.82f);
                case TerminalLogLevel.Success:
                case TerminalLogLevel.PythonBridge:
                    return new UnityEngine.Color(0.18f, 0.8f, 0.44f);
                case TerminalLogLevel.Error:
                    return new UnityEngine.Color(0.86f, 0.08f, 0.24f);
                case TerminalLogLevel.Warning:
                    return new UnityEngine.Color(1f, 0.75f, 0f);
                case TerminalLogLevel.System:
                    return new UnityEngine.Color(0.61f, 0.35f, 0.71f);
                default:
                    return new UnityEngine.Color(0.69f, 0.75f, 0.77f);
            }
        }

        private void HandleInputMutation(ChangeEvent<string> args)
        {
            RefreshSuggestions(args.newValue);
        }

        private void HandleInputKey(KeyDownEvent args)
        {
            switch (args.keyCode)
            {
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    SubmitCurrentInput();
                    args.StopPropagation();
                    break;
                case KeyCode.UpArrow:
                    RecallHistory(-1);
                    args.StopPropagation();
                    break;
                case KeyCode.DownArrow:
                    RecallHistory(1);
                    args.StopPropagation();
                    break;
                case KeyCode.Tab:
                    AcceptTopSuggestion();
                    args.StopPropagation();
                    break;
                case KeyCode.Escape:
                    CollapseSuggestions();
                    args.StopPropagation();
                    break;
            }
        }

        private void SubmitCurrentInput()
        {
            string payload = commandInput.value;

            if (string.IsNullOrWhiteSpace(payload))
            {
                return;
            }

            historyRing.Add(payload);
            historyCursor = historyRing.Count;
            commandInput.SetValueWithoutNotify(string.Empty);
            CollapseSuggestions();
            engine.SubmitInput(payload);
        }

        private void RecallHistory(int delta)
        {
            if (historyRing.Count == 0)
            {
                return;
            }

            historyCursor = Math.Clamp(historyCursor + delta, 0, historyRing.Count - 1);
            commandInput.SetValueWithoutNotify(historyRing[historyCursor]);
        }

        private void RefreshSuggestions(string rawInput)
        {
            suggestionPopup.Clear();
            pendingSuggestionPrefix = ExtractActiveToken(rawInput);
            IReadOnlyList<string> candidates = engine.SuggestTokens(pendingSuggestionPrefix);

            if (candidates.Count == 0 || string.IsNullOrEmpty(rawInput))
            {
                CollapseSuggestions();
                return;
            }

            for (int i = 0; i < Math.Min(candidates.Count, MaxRenderedSuggestions); i++)
            {
                Label option = new Label(candidates[i]);
                option.AddToClassList("suggestion-item");
                string captured = candidates[i];
                option.RegisterCallback<MouseDownEvent>(_ => ApplySuggestion(captured));
                suggestionPopup.Add(option);
            }

            suggestionPopup.style.display = DisplayStyle.Flex;
        }

        private void AcceptTopSuggestion()
        {
            if (suggestionPopup.childCount == 0)
            {
                return;
            }

            ApplySuggestion(((Label)suggestionPopup[0]).text);
        }

        private void ApplySuggestion(string token)
        {
            string current = commandInput.value;
            string head = current.EndsWith(pendingSuggestionPrefix, StringComparison.Ordinal)
                ? current.Substring(0, current.Length - pendingSuggestionPrefix.Length)
                : current;
            commandInput.SetValueWithoutNotify(head + token);
            CollapseSuggestions();
        }

        private void HandleWheelPinning(ScrollWheelEvent args)
        {
            pinnedToBottom = args.delta.y < 0f || IsNearBottom();
        }

        private bool IsNearBottom()
        {
            return renderedRows.Count > 0 &&
                   renderedRows.Count <= Math.Max(logList.visibleItemCount, 1) + 4;
        }

        private void CollapseSuggestions()
        {
            suggestionPopup.style.display = DisplayStyle.None;
        }

        private static string ExtractActiveToken(string rawInput)
        {
            int boundary = rawInput.LastIndexOf(' ');
            return boundary >= 0 ? rawInput.Substring(boundary + 1) : rawInput;
        }

        private VisualElement BuildSuggestionPopup()
        {
            VisualElement popup = new VisualElement();
            popup.name = "intellisense-popup";
            popup.AddToClassList("suggestion-popup");
            popup.style.display = DisplayStyle.None;
            return popup;
        }

        private void TrimRetention()
        {
            const int viewportCeiling = 100000;

            while (renderedRows.Count > viewportCeiling)
            {
                renderedRows.RemoveAt(0);
            }
        }

        private void RebindList()
        {
            logList.itemsSource = null;
            logList.itemsSource = renderedRows;
            logList.RefreshItems();
        }

        private void ScrollToEnd()
        {
            if (renderedRows.Count > 0)
            {
                logList.ScrollToItem(renderedRows.Count - 1);
            }
        }

        private void UpdateStatus()
        {
            statusStrip.Clear();
            statusStrip.Add(new Label($"Rows: {renderedRows.Count:N0} | Engine live"));
        }
    }
}
