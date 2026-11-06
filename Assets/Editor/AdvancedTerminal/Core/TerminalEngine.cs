using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityAdvancedTerminal.Architecture.Commands;
using UnityAdvancedTerminal.Architecture.Observers;
using UnityAdvancedTerminal.Architecture.Pipelines;
using UnityAdvancedTerminal.Utility;
using UnityEditor;
using UnityEngine;

namespace UnityAdvancedTerminal.Core
{
    public sealed class LogFlushedEvent : EventArgs
    {
        public TerminalLogEntry[] Batch { get; }

        public LogFlushedEvent(TerminalLogEntry[] batch)
        {
            Batch = batch;
        }
    }

    public interface ITerminalEngine : ITerminalService
    {
        event Action<LogFlushedEvent> BatchFlushed;
        event Action BufferCleared;
        event Action<string> QuickVariableChanged;
        IReadOnlyList<ITerminalCommand> CommandCatalog { get; }
        IReadOnlyList<TerminalLogEntry> Snapshot();
        void Emit(string message, TerminalLogLevel level);
        void Observe(ITerminalLogObserver observer);
        void Unobserve(string observerId);
        void SubmitInput(string rawLine);
        Task RunPythonAsync(string statement);
        void BindQuickVariable(string name, string value);
        bool ToggleLogMirror(bool enabled);
        bool KillSwitch();
        void ClearBuffers();
        void RegisterMacro(string macroName, string scriptPath);
        void ExecuteMacro(string macroName);
        IReadOnlyDictionary<string, string> MacroBindings { get; }
        string ExportSession(bool asJson);
        void RefreshEnvironmentTree();
        IReadOnlyList<string> SuggestTokens(string prefix);
    }

    [InitializeOnLoad]
    public sealed class TerminalEngine : ITerminalEngine
    {
        private const int FlushIntervalMs = 120;
        private const int RetentionCeiling = 100000;

        private static readonly string WorkspaceRoot =
            Path.Combine(Directory.GetCurrentDirectory(), "Assets", "Editor", "PythonScripts");

        private static readonly string MacroRegistryPath =
            Path.Combine(WorkspaceRoot, "macros.json");

        private readonly object bufferGate = new object();
        private readonly object observerGate = new object();

        private readonly List<TerminalLogEntry> committedBuffer = new List<TerminalLogEntry>();
        private readonly List<TerminalLogEntry> stagingRing = new List<TerminalLogEntry>();
        private readonly List<ITerminalLogObserver> observers = new List<ITerminalLogObserver>();
        private readonly List<ITerminalCommand> commands = new List<ITerminalCommand>();
        private readonly Dictionary<string, string> boundMacros = new Dictionary<string, string>();

        private readonly ITextProcessor textPipeline;
        private readonly IPythonBridge pythonBridge;

        private Timer flushTimer;
        private bool activeLoggingMirror;

        static TerminalEngine()
        {
            EnsureWorkspaceSkeleton();
            TerminalServiceLocator locator = TerminalServiceLocator.Current;

            if (!locator.TryResolve<TerminalEngine>(out _))
            {
                TerminalEngine engine = new TerminalEngine();
                locator.Register<TerminalEngine>(engine);
                locator.Register<IPythonBridge>(engine.pythonBridge);
                locator.Register<ITextProcessor>(engine.textPipeline);
                _ = engine.BootstrapAsync();
            }
        }

        private TerminalEngine()
        {
            ServiceName = "terminal-engine";
            textPipeline = new TextProcessor();
            pythonBridge = new PythonBridge(Relay);
            RegisterDefaultCommands();
            LoadMacroRegistry();
            pythonBridge.ExecutionCompleted += HandleExecutionCompleted;
        }

        public string ServiceName { get; }

        public event Action<LogFlushedEvent> BatchFlushed;
        public event Action BufferCleared;
        public event Action<string> QuickVariableChanged;

        public IReadOnlyList<ITerminalCommand> CommandCatalog => commands;

        public async Task BootstrapAsync()
        {
            await pythonBridge.StartAsync(CancellationToken.None).ConfigureAwait(false);
            Relay($"Advanced Terminal online. Workspace: {WorkspaceRoot}", TerminalLogLevel.System);
        }

        public void Emit(string message, TerminalLogLevel level)
        {
            TextProcessingResult processed = textPipeline.Process(message, level);
            TerminalLogEntry entry = new TerminalLogEntry(
                processed.ProcessedText, processed.InferredLevel, ResolveChannel(processed.InferredLevel));

            lock (bufferGate)
            {
                stagingRing.Add(entry);
            }

            EditorMainThreadDispatcher.Enqueue(() =>
            {
                lock (observerGate)
                {
                    for (int i = 0; i < observers.Count; i++)
                    {
                        if (observers[i].Accepts(entry))
                        {
                            observers[i].OnLogEnqueued(entry);
                        }
                    }
                }
            });
        }

        public IReadOnlyList<TerminalLogEntry> Snapshot()
        {
            lock (bufferGate)
            {
                return committedBuffer.ToArray();
            }
        }

        public void Observe(ITerminalLogObserver observer)
        {
            lock (observerGate)
            {
                if (!observers.Contains(observer))
                {
                    observers.Add(observer);
                }
            }

            flushTimer ??= new Timer(_ => FlushStaging(), null, FlushIntervalMs, FlushIntervalMs);

            if (activeLoggingMirror)
            {
                AttachLogMirror();
            }
        }

        public void Unobserve(string observerId)
        {
            lock (observerGate)
            {
                observers.RemoveAll(existing => existing.ObserverId == observerId);

                if (observers.Count == 0)
                {
                    flushTimer?.Dispose();
                    flushTimer = null;
                    DetachLogMirror();
                }
            }
        }

        public void SubmitInput(string rawLine)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                return;
            }

            Emit(rawLine, TerminalLogLevel.Input);

            if (rawLine.StartsWith("py ", StringComparison.OrdinalIgnoreCase))
            {
                _ = RunPythonAsync(rawLine.Substring(3));
                return;
            }

            if (rawLine.StartsWith("="))
            {
                ApplyQuickVariableAssignment(rawLine);
                return;
            }

            DispatchCommand(rawLine);
        }

        public Task RunPythonAsync(string statement)
        {
            Emit($">>> {statement}", TerminalLogLevel.Input);
            return pythonBridge.SubmitStatementAsync(statement, CancellationToken.None);
        }

        public void BindQuickVariable(string name, string value)
        {
            string assignment = $"{name}={value}";
            QuickVariableChanged?.Invoke(assignment);

            if (pythonBridge.IsRunning)
            {
                _ = pythonBridge.SubmitStatementAsync($"{PythonBridgeCommandPrefix}{assignment}",
                    CancellationToken.None);
            }
        }

        public string ExportSession(bool asJson)
        {
            IReadOnlyList<TerminalLogEntry> snapshot = Snapshot();
            return asJson ? SerializeAsJson(snapshot) : textPipeline.SerializeMatrix(snapshot);
        }

        public bool ToggleLogMirror(bool enabled)
        {
            activeLoggingMirror = enabled;

            if (enabled)
            {
                AttachLogMirror();
                Emit("Unity Log Mirror engaged.", TerminalLogLevel.System);
                return true;
            }

            DetachLogMirror();
            Emit("Unity Log Mirror disengaged.", TerminalLogLevel.System);
            return false;
        }

        public bool KillSwitch()
        {
            return pythonBridge.KillActiveExecution();
        }

        public void ClearBuffers()
        {
            lock (bufferGate)
            {
                committedBuffer.Clear();
                stagingRing.Clear();
            }

            EditorMainThreadDispatcher.Enqueue(() =>
            {
                lock (observerGate)
                {
                    foreach (ITerminalLogObserver observer in observers)
                    {
                        observer.OnBufferCleared();
                    }
                }
                BufferCleared?.Invoke();
            });
        }

        public void RefreshEnvironmentTree()
        {
            EnsureWorkspaceSkeleton();
            Emit($"Environment tree refreshed for {WorkspaceRoot}", TerminalLogLevel.Information);
        }

        public IReadOnlyList<string> SuggestTokens(string prefix)
        {
            if (string.IsNullOrEmpty(prefix))
            {
                return commands.Select(command => command.Name).ToArray();
            }

            List<string> suggestions = commands
                .Where(command => command.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(command => command.Name)
                .ToList();

            if (Directory.Exists(WorkspaceRoot))
            {
                IEnumerable<string> files = Directory.EnumerateFiles(WorkspaceRoot, "*.py")
                    .Select(Path.GetFileName)
                    .Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

                suggestions.AddRange(files);
            }

            suggestions.AddRange(FilterUnityApiHints(prefix));
            return suggestions.Distinct().Take(24).ToArray();
        }

        public void RegisterMacro(string macroName, string scriptPath)
        {
            boundMacros[macroName] = scriptPath;
            SaveMacroRegistry();
            Emit($"Macro '{macroName}' bound to {scriptPath}", TerminalLogLevel.Success);
        }

        public void ExecuteMacro(string macroName)
        {
            if (!boundMacros.TryGetValue(macroName, out string scriptPath))
            {
                Emit($"Macro '{macroName}' is not registered.", TerminalLogLevel.Warning);
                return;
            }

            if (!File.Exists(scriptPath))
            {
                Emit($"Macro target missing: {scriptPath}", TerminalLogLevel.Error);
                return;
            }

            _ = RunPythonAsync(File.ReadAllText(scriptPath));
        }

        public IReadOnlyDictionary<string, string> MacroBindings => boundMacros;

        public void Teardown()
        {
            pythonBridge.ExecutionCompleted -= HandleExecutionCompleted;
            DetachLogMirror();
            flushTimer?.Dispose();
            pythonBridge.Teardown();
        }

        private void RegisterDefaultCommands()
        {
            commands.Add(new ClearCommand(ClearBuffers));
            commands.Add(new HelpCommand(() => commands));
            commands.Add(new SystemCmdCommand());
        }

        private void DispatchCommand(string rawLine)
        {
            string[] tokens = Tokenize(rawLine);
            if (tokens.Length == 0) return;

            string head = tokens[0].ToLowerInvariant();

            foreach (ITerminalCommand command in commands)
            {
                if (command.Matches(head))
                {
                    TerminalCommandContext context = new TerminalCommandContext(
                        rawLine, head, tokens.Skip(1).ToArray(), Relay);
                    command.Execute(context);
                    return;
                }
            }

            var sysCmd = new SystemCmdCommand();
            var fallbackContext = new TerminalCommandContext(rawLine, "system", tokens, Relay);
            sysCmd.Execute(fallbackContext);
        }

        private void Relay(string message, TerminalLogLevel level)
        {
            Emit(message, level);
        }

        private void FlushStaging()
        {
            TerminalLogEntry[] drained;

            lock (bufferGate)
            {
                if (stagingRing.Count == 0)
                {
                    return;
                }

                committedBuffer.AddRange(stagingRing);

                if (committedBuffer.Count > RetentionCeiling)
                {
                    committedBuffer.RemoveRange(0, committedBuffer.Count - RetentionCeiling);
                }

                drained = stagingRing.ToArray();
                stagingRing.Clear();
            }

            EditorMainThreadDispatcher.Enqueue(() =>
            {
                lock (observerGate)
                {
                    foreach (ITerminalLogObserver observer in observers)
                    {
                        observer.OnBatchFlushed(drained);
                    }
                }
                BatchFlushed?.Invoke(new LogFlushedEvent(drained));
            });
        }

        private static string[] Tokenize(string input)
        {
            List<string> tokens = new List<string>();
            System.Text.StringBuilder current = new System.Text.StringBuilder();
            bool insideQuotes = false;

            foreach (char symbol in input)
            {
                if (symbol == '"')
                {
                    insideQuotes = !insideQuotes;
                    continue;
                }

                if (char.IsWhiteSpace(symbol) && !insideQuotes)
                {
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }
                    continue;
                }

                current.Append(symbol);
            }

            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
            }

            return tokens.ToArray();
        }

        private void ApplyQuickVariableAssignment(string rawLine)
        {
            string payload = rawLine.Substring(1);
            int separator = payload.IndexOf('=');

            if (separator <= 0)
            {
                Emit("Quick variable syntax: =name=value", TerminalLogLevel.Warning);
                return;
            }

            BindQuickVariable(payload.Substring(0, separator), payload.Substring(separator + 1));
        }

        private void HandleExecutionCompleted(PythonExecutionRecord record)
        {
            Emit($"Execution #{record.ExecutionId} finished in {record.Duration.TotalMilliseconds:F1} ms " +
                 $"| peak RSS {record.PeakWorkingSetBytes / 1024 / 1024} MB | exit {record.ExitCode}",
                record.ExitCode == 0 ? TerminalLogLevel.Success : TerminalLogLevel.Warning);
        }

        private void AttachLogMirror()
        {
            Application.logMessageReceivedThreaded -= MirrorUnityLog;
            Application.logMessageReceivedThreaded += MirrorUnityLog;
        }

        private void DetachLogMirror()
        {
            Application.logMessageReceivedThreaded -= MirrorUnityLog;
        }

        private void MirrorUnityLog(string condition, string stackTrace, LogType type)
        {
            TerminalLogLevel mapped = MapUnitySeverity(type);
            string composed = type == LogType.Exception
                ? $"{condition}\n{stackTrace}"
                : condition;
            Emit(composed, mapped);
        }

        private static TerminalLogLevel MapUnitySeverity(LogType type)
        {
            switch (type)
            {
                case LogType.Error:
                case LogType.Assert:
                case LogType.Exception:
                    return TerminalLogLevel.Error;
                case LogType.Warning:
                    return TerminalLogLevel.Warning;
                default:
                    return TerminalLogLevel.Information;
            }
        }

        private string SerializeAsJson(IReadOnlyList<TerminalLogEntry> entries)
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            builder.AppendLine("{");
            builder.AppendLine($"  \"session\": \"{Guid.NewGuid():N}\",");
            builder.AppendLine($"  \"unityVersion\": \"{Application.unityVersion}\",");
            builder.AppendLine($"  \"exportedUtc\": \"{DateTime.UtcNow:O}\",");
            builder.AppendLine("  \"entries\": [");

            for (int i = 0; i < entries.Count; i++)
            {
                TerminalLogEntry entry = entries[i];
                builder.Append("    {");
                builder.Append($"\"seq\":{entry.SequenceId},");
                builder.Append($"\"ts\":\"{entry.Timestamp:O}\",");
                builder.Append($"\"level\":\"{entry.Level}\",");
                builder.Append($"\"channel\":\"{entry.SourceChannel}\",");
                builder.Append($"\"message\":\"{Escape(entry.Message)}\"");
                builder.Append('}');
                builder.AppendLine(i == entries.Count - 1 ? "" : ",");
            }

            builder.AppendLine("  ]");
            builder.AppendLine("}");
            return builder.ToString();
        }

        private static string Escape(string input)
        {
            return input.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
        }

        private string ResolveChannel(TerminalLogLevel level)
        {
            switch (level)
            {
                case TerminalLogLevel.PythonBridge:
                    return "python";
                case TerminalLogLevel.Input:
                    return "stdin";
                case TerminalLogLevel.System:
                    return "engine";
                default:
                    return activeLoggingMirror ? "mirror" : "core";
            }
        }

        private IEnumerable<string> FilterUnityApiHints(string prefix)
        {
            string[] catalogue =
            {
                "UnityEngine.Debug.Log",
                "UnityEditor.AssetDatabase.Refresh",
                "UnityEditor.Selection.activeObject",
                "UnityEditor.EditorUtility.DisplayDialog",
                "UnityEngine.GameObject.FindWithTag"
            };

            return catalogue.Where(hint => hint.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        private static void EnsureWorkspaceSkeleton()
        {
            if (!Directory.Exists(WorkspaceRoot))
            {
                Directory.CreateDirectory(WorkspaceRoot);
            }

            string metaPath = Path.Combine(WorkspaceRoot, "README.md");

            if (!File.Exists(metaPath))
            {
                File.WriteAllText(metaPath, "# Python Workspace\nDrop automation scripts here.\n");
            }
        }

        private void LoadMacroRegistry()
        {
            if (!File.Exists(MacroRegistryPath))
            {
                return;
            }

            foreach (string line in File.ReadAllLines(MacroRegistryPath))
            {
                int separator = line.IndexOf('=');

                if (separator <= 0)
                {
                    continue;
                }

                boundMacros[line.Substring(0, separator)] = line.Substring(separator + 1);
            }
        }

        private void SaveMacroRegistry()
        {
            EnsureWorkspaceSkeleton();
            IEnumerable<string> lines = boundMacros.Select(pair => $"{pair.Key}={pair.Value}");
            File.WriteAllLines(MacroRegistryPath, lines, System.Text.Encoding.UTF8);
        }

        private const string PythonBridgeCommandPrefix = "unity-internal-command:";
    }
}