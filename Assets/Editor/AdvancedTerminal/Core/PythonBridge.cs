using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AdvancedTerminal.Architecture.Observers;

namespace AdvancedTerminal.Core
{
    public enum PythonBackendKind
    {
        SystemProcess,
        PythonNetRuntime
    }

    public sealed class PythonExecutionRecord
    {
        public int ExecutionId { get; set; }

        public string Source { get; set; }

        public DateTimeOffset StartedUtc { get; set; }

        public TimeSpan Duration { get; set; }

        public long PeakWorkingSetBytes { get; set; }

        public int ExitCode { get; set; }

        public bool Terminated { get; set; }
    }

    public interface IPythonBridge : ITerminalService
    {
        bool IsRunning { get; }

        PythonBackendKind ActiveBackend { get; }

        event Action<PythonExecutionRecord> ExecutionCompleted;

        Task StartAsync(CancellationToken externalToken);

        Task StopAsync();

        Task<int> SubmitStatementAsync(string statement, CancellationToken externalToken);

        void KillActiveExecution();

        IReadOnlyList<string> ProbeQuickVariables();
    }

    public sealed class PythonBridge : IPythonBridge
    {
        private const int IpcPortRangeStart = 47000;
        private const int IpcPortRangeEnd = 47100;
        private const int StreamDrainTimeoutMs = 250;

        private readonly object processGate = new object();
        private readonly object variableGate = new object();
        private readonly object socketGate = new object();

        private readonly Action<string, TerminalLogLevel> logSink;
        private readonly Func<string> bootstrapSupplier;
        private readonly List<string> trackedGlobals = new List<string>();

        private readonly SemaphoreSlim executionSlot = new SemaphoreSlim(1, 1);

        private Process hostedProcess;
        private TcpListener ipcListener;
        private CancellationTokenSource loopCancellation;
        private TaskCompletionSource<bool> readySignal;
        private volatile bool awaitingHandshake;
        private int activeExecutionId;
        private int nextExecutionId;
        private int completionProbe;
        private PythonBackendKind backendKind = PythonBackendKind.SystemProcess;

        public PythonBridge(Action<string, TerminalLogLevel> logSink)
        {
            this.logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
            bootstrapSupplier = BootstrapPayloadRepository.Build;
        }

        public string ServiceName => "python-bridge";

        public bool IsRunning
        {
            get
            {
                lock (processGate)
                {
                    return hostedProcess != null && !hostedProcess.HasExited;
                }
            }
        }

        public PythonBackendKind ActiveBackend => backendKind;

        public event Action<PythonExecutionRecord> ExecutionCompleted;

        public async Task StartAsync(CancellationToken externalToken)
        {
            await executionSlot.WaitAsync(externalToken).ConfigureAwait(false);

            try
            {
                if (IsRunning)
                {
                    return;
                }

                PreparePythonNetProbe();

                int assignedPort = ReserveIpcPort();
                StartIpcListener(assignedPort);

                ProcessStartInfo startInfo = BuildProcessStartInfo(assignedPort);
                Process spawned = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

                readySignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                awaitingHandshake = true;

                spawned.Exited += HandleProcessExit;
                spawned.OutputDataReceived += HandleOutputLine;
                spawned.ErrorDataReceived += HandleErrorLine;

                lock (processGate)
                {
                    hostedProcess = spawned;
                }

                spawned.Start();
                spawned.BeginOutputReadLine();
                spawned.BeginErrorReadLine();

                loopCancellation = new CancellationTokenSource();
                _ = PumpSocketAsync(loopCancellation.Token);

                bool handshake = await Task.WhenAny(readySignal.Task,
                        Task.Delay(8000, externalToken))
                    .ConfigureAwait(false) == readySignal.Task &&
                    await readySignal.Task.ConfigureAwait(false);

                if (!handshake)
                {
                    logSink.Invoke("Bootstrap handshake timed out; runtime degraded to stateless mode.",
                        TerminalLogLevel.Warning);
                }
                else
                {
                    logSink.Invoke($"Python runtime online via {backendKind} on IPC port {assignedPort}.",
                        TerminalLogLevel.Success);
                }
            }
            finally
            {
                executionSlot.Release();
            }
        }

        public async Task StopAsync()
        {
            await executionSlot.WaitAsync().ConfigureAwait(false);

            try
            {
                Process target = null;

                lock (processGate)
                {
                    if (hostedProcess != null)
                    {
                        target = hostedProcess;
                        hostedProcess = null;
                    }
                }

                loopCancellation?.Cancel();
                StopIpcListener();

                if (target == null)
                {
                    return;
                }

                target.Exited -= HandleProcessExit;

                try
                {
                    if (!target.HasExited)
                    {
                        target.StandardInput.Close();

                        if (!target.WaitForExit(1500))
                        {
                            target.Kill();
                        }
                    }
                }
                catch (Exception failure)
                {
                    logSink.Invoke($"Graceful shutdown failed: {failure.Message}", TerminalLogLevel.Warning);
                }
                finally
                {
                    target.Dispose();
                    logSink.Invoke("Python runtime stopped.", TerminalLogLevel.System);
                }
            }
            finally
            {
                executionSlot.Release();
            }
        }

        public async Task<int> SubmitStatementAsync(string statement, CancellationToken externalToken)
        {
            if (string.IsNullOrWhiteSpace(statement))
            {
                return -1;
            }

            await executionSlot.WaitAsync(externalToken).ConfigureAwait(false);

            int executionId = Interlocked.Increment(ref nextExecutionId);
            Stopwatch chronometer = Stopwatch.StartNew();
            bool slotOwned = true;

            PythonExecutionRecord record = new PythonExecutionRecord
            {
                ExecutionId = executionId,
                Source = statement,
                StartedUtc = DateTimeOffset.UtcNow
            };

            try
            {
                Interlocked.Exchange(ref completionProbe, 0);
                activeExecutionId = executionId;

                if (backendKind == PythonBackendKind.PythonNetRuntime)
                {
                    record.ExitCode = await RunPythonNetStatementAsync(statement).ConfigureAwait(false);
                }
                else
                {
                    record.ExitCode = await RunSystemStatementAsync(statement, executionId, externalToken)
                        .ConfigureAwait(false);
                }

                RefreshTrackedGlobals();
                return record.ExitCode;
            }
            catch (OperationCanceledException)
            {
                slotOwned = false;
                record.Terminated = true;
                logSink.Invoke($"Execution #{executionId} cancelled by caller.", TerminalLogLevel.Warning);
                return -1;
            }
            catch (Exception failure)
            {
                record.ExitCode = -1;
                logSink.Invoke($"Execution fault: {failure.Message}", TerminalLogLevel.Error);
                return -1;
            }
            finally
            {
                chronometer.Stop();
                record.Duration = chronometer.Elapsed;
                record.PeakWorkingSetBytes = SampleWorkingSet();
                activeExecutionId = 0;
                Interlocked.Exchange(ref completionProbe, 0);
                ExecutionCompleted?.Invoke(record);

                if (slotOwned)
                {
                    executionSlot.Release();
                }
            }
        }

        public void KillActiveExecution()
        {
            lock (processGate)
            {
                if (hostedProcess == null || hostedProcess.HasExited)
                {
                    logSink.Invoke("No live Python process available for termination.",
                        TerminalLogLevel.Warning);
                    return;
                }

                try
                {
                    hostedProcess.Kill();
                    logSink.Invoke($"Kill switch engaged on execution #{activeExecutionId}.",
                        TerminalLogLevel.Error);
                }
                catch (Exception failure)
                {
                    logSink.Invoke($"Kill switch failure: {failure.Message}", TerminalLogLevel.Error);
                }
            }
        }

        public IReadOnlyList<string> ProbeQuickVariables()
        {
            lock (variableGate)
            {
                return trackedGlobals.ToArray();
            }
        }

        public void Teardown()
        {
            KillActiveExecution();
            StopAsync().GetAwaiter().GetResult();
            executionSlot.Dispose();
        }

        private ProcessStartInfo BuildProcessStartInfo(int ipcPort)
        {
            string interpreter = ResolveInterpreterPath();
            string bootstrapPath = BootstrapPayloadRepository.WriteToTemp(bootstrapSupplier.Invoke());

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = interpreter,
                Arguments = $"-u \"{bootstrapPath}\" --ipc-port {ipcPort}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Directory.GetCurrentDirectory(),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            startInfo.Environment["UNITY_IPC_PORT"] = ipcPort.ToString();
            startInfo.Environment["PYTHONUNBUFFERED"] = "1";
            return startInfo;
        }

        private static string ResolveInterpreterPath()
        {
            string configured = Environment.GetEnvironmentVariable("ADVTERM_PYTHON_PATH");

            if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
            {
                return configured;
            }

            return OperatingSystem.IsWindows() ? "python" : "python3";
        }

        private void PreparePythonNetProbe()
        {
            Type engineType = Type.GetType("Python.Runtime.PythonEngine, pythonnet");

            if (engineType == null)
            {
                backendKind = PythonBackendKind.SystemProcess;
                return;
            }

            backendKind = PythonBackendKind.PythonNetRuntime;
            logSink.Invoke("PythonNet assembly detected; in-process backend selected.",
                TerminalLogLevel.System);
        }

        private async Task<int> RunSystemStatementAsync(string statement, int executionId,
            CancellationToken externalToken)
        {
            Process target;

            lock (processGate)
            {
                if (hostedProcess == null || hostedProcess.HasExited)
                {
                    throw new InvalidOperationException("Python process is not running.");
                }

                target = hostedProcess;
            }

            string framed = JsonFrameBuilder.BuildCommand(executionId, statement);
            await target.StandardInput.WriteAsync(framed + "\n").ConfigureAwait(false);
            await target.StandardInput.FlushAsync().ConfigureAwait(false);

            DateTime deadline = DateTime.UtcNow.AddSeconds(60);

            while (Interlocked.CompareExchange(ref completionProbe, 0, executionId) != executionId &&
                   DateTime.UtcNow < deadline)
            {
                externalToken.ThrowIfCancellationRequested();
                await Task.Delay(StreamDrainTimeoutMs, externalToken).ConfigureAwait(false);
            }

            bool completed = Interlocked.CompareExchange(ref completionProbe, 0, executionId) == executionId;
            return completed ? 0 : 137;
        }

        private Task<int> RunPythonNetStatementAsync(string statement)
        {
            return Task.Run(() =>
            {
                try
                {
                    dynamic engine = Activator.CreateInstance(
                        Type.GetType("Python.Runtime.PythonEngine, pythonnet"));
                    dynamic result = engine.Exec(statement);
                    logSink.Invoke(result?.ToString() ?? string.Empty, TerminalLogLevel.PythonBridge);
                    return 0;
                }
                catch (Exception failure)
                {
                    logSink.Invoke($"PythonNet evaluation failed: {failure.Message}",
                        TerminalLogLevel.Error);
                    return 1;
                }
            });
        }

        private int ReserveIpcPort()
        {
            for (int candidate = IpcPortRangeStart; candidate <= IpcPortRangeEnd; candidate++)
            {
                try
                {
                    TcpListener probe = new TcpListener(IPAddress.Loopback, candidate);
                    probe.Start();
                    probe.Stop();
                    return candidate;
                }
                catch (SocketException)
                {
                    continue;
                }
            }

            return 0;
        }

        private void StartIpcListener(int port)
        {
            if (port == 0)
            {
                return;
            }

            lock (socketGate)
            {
                ipcListener = new TcpListener(IPAddress.Loopback, port);
                ipcListener.Start();
            }
        }

        private void StopIpcListener()
        {
            lock (socketGate)
            {
                ipcListener?.Stop();
                ipcListener = null;
            }
        }

        private async Task PumpSocketAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client = null;

                try
                {
                    client = await AcceptClientAsync(token).ConfigureAwait(false);

                    if (client == null)
                    {
                        return;
                    }

                    using (NetworkStream stream = client.GetStream())
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string line = await reader.ReadLineAsync().ConfigureAwait(false);

                        if (!string.IsNullOrEmpty(line))
                        {
                            DispatchIncoming(line);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception failure)
                {
                    logSink.Invoke($"IPC pump error: {failure.Message}", TerminalLogLevel.Warning);
                }
                finally
                {
                    client?.Dispose();
                }
            }
        }

        private async Task<TcpClient> AcceptClientAsync(CancellationToken token)
        {
            TcpListener listener;

            lock (socketGate)
            {
                listener = ipcListener;
            }

            if (listener == null)
            {
                return null;
            }

            token.Register(() => listener.Stop());
            return await listener.AcceptTcpClientAsync().ConfigureAwait(false);
        }

        private void HandleOutputLine(object sender, DataReceivedEventArgs args)
        {
            if (args.Data == null)
            {
                return;
            }

            DispatchIncoming(args.Data);
        }

        private void HandleErrorLine(object sender, DataReceivedEventArgs args)
        {
            if (args.Data == null)
            {
                return;
            }

            logSink.Invoke(args.Data, ClassifyErrorLine(args.Data));
        }

        private void DispatchIncoming(string payload)
        {
            ParsedFrame frame = JsonFrameParser.TryParse(payload);

            switch (frame.Kind)
            {
                case FrameKind.Handshake:
                    SignalReady();
                    break;
                case FrameKind.Result:
                    logSink.Invoke(frame.Payload, TerminalLogLevel.PythonBridge);
                    CompleteActive(frame.ExecutionId);
                    break;
                case FrameKind.Variable:
                    TrackVariable(frame.Payload);
                    break;
                case FrameKind.Error:
                    logSink.Invoke(frame.Payload, TerminalLogLevel.Error);
                    CompleteActive(frame.ExecutionId);
                    break;
                default:
                    logSink.Invoke(payload, TerminalLogLevel.Information);
                    break;
            }
        }

        private void SignalReady()
        {
            if (!awaitingHandshake)
            {
                return;
            }

            awaitingHandshake = false;
            readySignal?.TrySetResult(true);
        }

        private void CompleteActive(int executionId)
        {
            if (executionId != 0 && executionId == activeExecutionId)
            {
                Interlocked.Exchange(ref completionProbe, executionId);
                activeExecutionId = 0;
            }
        }

        private void TrackVariable(string descriptor)
        {
            lock (variableGate)
            {
                if (!trackedGlobals.Contains(descriptor))
                {
                    trackedGlobals.Add(descriptor);
                }
            }
        }

        private void RefreshTrackedGlobals()
        {
            lock (variableGate)
            {
                if (trackedGlobals.Count > 256)
                {
                    trackedGlobals.RemoveRange(0, trackedGlobals.Count - 256);
                }
            }
        }

        private long SampleWorkingSet()
        {
            lock (processGate)
            {
                try
                {
                    return hostedProcess != null && !hostedProcess.HasExited
                        ? hostedProcess.WorkingSet64
                        : 0L;
                }
                catch (Exception)
                {
                    return 0L;
                }
            }
        }

        private TerminalLogLevel ClassifyErrorLine(string line)
        {
            if (line.Contains("SyntaxError") || line.Contains("IndentationError"))
            {
                return TerminalLogLevel.Error;
            }

            return line.Contains("Warning") ? TerminalLogLevel.Warning : TerminalLogLevel.Error;
        }

        private void HandleProcessExit(object sender, EventArgs args)
        {
            logSink.Invoke("Python process terminated by the operating system.",
                TerminalLogLevel.System);
            readySignal?.TrySetResult(false);
        }

        private enum FrameKind
        {
            Raw,
            Handshake,
            Result,
            Variable,
            Error
        }

        private readonly struct ParsedFrame
        {
            public FrameKind Kind { get; }

            public int ExecutionId { get; }

            public string Payload { get; }

            public ParsedFrame(FrameKind kind, int executionId, string payload)
            {
                Kind = kind;
                ExecutionId = executionId;
                Payload = payload;
            }
        }

        private static class JsonFrameParser
        {
            public static ParsedFrame TryParse(string payload)
            {
                if (payload.StartsWith("{\"evt\":\"ready\"", StringComparison.Ordinal))
                {
                    return new ParsedFrame(FrameKind.Handshake, 0, payload);
                }

                if (payload.StartsWith("{\"evt\":\"out\"", StringComparison.Ordinal))
                {
                    return new ParsedFrame(FrameKind.Result, ExtractId(payload), ExtractData(payload));
                }

                if (payload.StartsWith("{\"evt\":\"var\"", StringComparison.Ordinal))
                {
                    return new ParsedFrame(FrameKind.Variable, 0, ExtractData(payload));
                }

                if (payload.StartsWith("{\"evt\":\"err\"", StringComparison.Ordinal))
                {
                    return new ParsedFrame(FrameKind.Error, ExtractId(payload), ExtractData(payload));
                }

                return new ParsedFrame(FrameKind.Raw, 0, payload);
            }

            private static int ExtractId(string json)
            {
                int marker = json.IndexOf("\"id\":", StringComparison.Ordinal);

                if (marker < 0)
                {
                    return 0;
                }

                int start = marker + 5;
                int length = 0;

                while (start + length < json.Length && char.IsDigit(json[start + length]))
                {
                    length++;
                }

                return length == 0 ? 0 : int.Parse(json.Substring(start, length));
            }

            private static string ExtractData(string json)
            {
                int marker = json.IndexOf("\"data\":\"", StringComparison.Ordinal);

                if (marker < 0)
                {
                    return json;
                }

                int start = marker + 8;
                int end = json.LastIndexOf('"');

                if (end <= start)
                {
                    return json;
                }

                return json.Substring(start, end - start).Replace("\\n", "\n");
            }
        }

        private static class JsonFrameBuilder
        {
            public static string BuildCommand(int executionId, string statement)
            {
                string escaped = statement.Replace("\\", "\\\\")
                                          .Replace("\"", "\\\"")
                                          .Replace("\n", "\\n")
                                          .Replace("\r", "");

                return $"{{\"cmd\":\"exec\",\"id\":{executionId},\"src\":\"{escaped}\"}}";
            }
        }

        private static class BootstrapPayloadRepository
        {
            public const string ResourcePath =
                "Assets/Editor/AdvancedTerminal/Resources/unity_bootstrap.py";

            public static string Build()
            {
                return File.ReadAllText(ResourcePath, Encoding.UTF8);
            }

            public static string WriteToTemp(string payload)
            {
                string path = Path.Combine(Path.GetTempPath(), "unity_bootstrap.py");
                File.WriteAllText(path, payload, Encoding.UTF8);
                return path;
            }
        }
    }
}
