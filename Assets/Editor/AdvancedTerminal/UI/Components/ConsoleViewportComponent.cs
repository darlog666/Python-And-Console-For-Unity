using System;
using System.Collections.Generic;
using UnityAdvancedTerminal.Architecture.Observers;
using UnityAdvancedTerminal.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityAdvancedTerminal.UI.Components
{
    public sealed class ConsoleViewportComponent : ITerminalLogObserver
    {
        private const int VirtualRowHeight = 18;
        private const int MaxRenderedSuggestions = 9;

        private readonly ITerminalEngine engine;
        private readonly ScrollView logScroll;
        private readonly VisualElement rowHost;
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

            logScroll = new ScrollView(Orientation.Vertical);
            logScroll.name = "console-log-scroll";
            logScroll.AddToClassList("console-viewport");
            logScroll.RegisterCallback<WheelEvent>(HandleWheelPinning);
            logScroll.schedule.Execute(EvaluatePinning).Every(250);

            rowHost = new VisualElement();
            rowHost.name = "console-row-host";
            rowHost.AddToClassList("console-row-host");
            logScroll.Add(rowHost);
            host.Add(logScroll);

            suggestionPopup = BuildSuggestionPopup();
            host.Add(suggestionPopup);

            commandInput = new TextField();
            commandInput.name = "console-input";
            commandInput.multiline = false;
            commandInput.isDelayed = false;
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
                rowHost.Add(BuildRow(batch[i]));
            }

            TrimRetention();

            if (pinnedToBottom)
            {
                ScrollToEnd();
            }

            UpdateStatus();
        }

        public void OnBufferCleared()
        {
            renderedRows.Clear();
            rowHost.Clear();
            UpdateStatus();
        }

        private void HydrateFromSnapshot()
        {
            IReadOnlyList<TerminalLogEntry> snapshot = engine.Snapshot();

            foreach (TerminalLogEntry entry in snapshot)
            {
                renderedRows.Add(entry);
                rowHost.Add(BuildRow(entry));
            }

            ScrollToEnd();
            UpdateStatus();
        }

        private static Label BuildRow(TerminalLogEntry entry)
        {
            Label row = new Label($"[{entry.Timestamp:HH:mm:ss}] {entry.Message}");
            row.enableRichText = true;
            row.AddToClassList(PaletteClassFor(entry.Level));
            row.style.minHeight = VirtualRowHeight;
            row.AddToClassList("log-row");
            return row;
        }

        private static string PaletteClassFor(TerminalLogLevel level)
        {
            switch (level)
            {
                case TerminalLogLevel.Input:
                    return "log-row-input";
                case TerminalLogLevel.Success:
                case TerminalLogLevel.PythonBridge:
                    return "log-row-success";
                case TerminalLogLevel.Error:
                    return "log-row-error";
                case TerminalLogLevel.Warning:
                    return "log-row-warning";
                case TerminalLogLevel.System:
                    return "log-row-system";
                default:
                    return "log-row-info";
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

        private void HandleWheelPinning(WheelEvent args)
        {
            pinnedToBottom = args.delta.y < 0f;
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
                rowHost.RemoveAt(0);
            }
        }

        private void ScrollToEnd()
        {
            logScroll.scrollOffset = new Vector2(0f, float.MaxValue);
        }

        private void EvaluatePinning()
        {
            Rect viewport = logScroll.contentViewport;
            Rect content = logScroll.contentRect;

            if (content.height <= viewport.height)
            {
                pinnedToBottom = true;
                return;
            }

            float remaining = content.yMax - viewport.yMax;
            pinnedToBottom = remaining <= VirtualRowHeight * 2;
        }

        private void UpdateStatus()
        {
            statusStrip.Clear();
            statusStrip.Add(new Label($"Rows: {renderedRows.Count:N0} | Engine live"));
        }
    }
}
