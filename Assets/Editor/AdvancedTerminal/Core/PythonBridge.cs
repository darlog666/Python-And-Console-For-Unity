using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityAdvancedTerminal.Architecture.Observers;
using UnityAdvancedTerminal.Utility;

namespace UnityAdvancedTerminal.Core
{
    public enum PythonBackendKind { SystemProcess, PythonNetRuntime }

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
        bool KillActiveExecution();
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
            get { lock (processGate) { return hostedProcess != null && !hostedProcess.HasExited; } }
        }
        public PythonBackendKind ActiveBackend => backendKind;
        public event Action<PythonExecutionRecord> ExecutionCompleted;

        public async Task StartAsync(CancellationToken externalToken)
        {
            await executionSlot.WaitAsync(externalToken).ConfigureAwait(false);
            try
            {
                if (IsRunning) return;
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

                lock (processGate) { hostedProcess = spawned; }

                spawned.Start();
                spawned.BeginOutputReadLine();
                spawned.BeginErrorReadLine();

                loopCancellation = new CancellationTokenSource();
                _ = PumpSocketAsync(loopCancellation.Token);

                bool handshake = await Task.WhenAny(readySignal.Task, Task.Delay(8000, externalToken)).ConfigureAwait(false) == readySignal.Task && await readySignal.Task.ConfigureAwait(false);

                if (!handshake)
                {
                    EditorMainThreadDispatcher.Enqueue(() => logSink.Invoke("Bootstrap handshake timed out; runtime degraded to stateless mode.", TerminalLogLevel.Warning));
                }
                else
                {
                    EditorMainThreadDispatcher.Enqueue(() => logSink.Invoke($"Python runtime online via {backendKind} on IPC port {assignedPort}.", TerminalLogLevel.Success));
                }
            }
            finally { executionSlot.Release(); }
        }

        public async Task StopAsync()
        {
            await executionSlot.WaitAsync().ConfigureAwait(false);
            try
            {
                Process target;
                lock (processGate)
                {
                    target = hostedProcess;
                    hostedProcess = null;
                }

                loopCancellation?.Cancel();
                StopIpcListener();

                if (target == null) return;
                target.Exited -= HandleProcessExit;

                try
                {
                    if (!target.HasExited)
                    {
                        target.StandardInput.Close();
                        if (!target.WaitForExit(1500)) target.Kill();
                    }
                }
                catch (Exception failure)
                {
                    EditorMainThreadDispatcher.Enqueue(() => logSink.Invoke($"Graceful shutdown failed: {failure.Message}", TerminalLogLevel.Warning));
                }
                finally
                {
                    target.Dispose();
                    EditorMainThreadDispatcher.Enqueue(() => logSink.Invoke("Python runtime stopped.", TerminalLogLevel.System));
                }
            }
            finally { executionSlot.Release(); }
        }

        public async Task<int> SubmitStatementAsync(string statement, CancellationToken externalToken)
        {
            if (string.IsNullOrWhiteSpace(statement)) return -1;
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
                    record.ExitCode = await RunSystemStatementAsync(statement, executionId, externalToken).ConfigureAwait(false);
                }

                RefreshTrackedGlobals();
                return record.ExitCode;
            }
            catch (OperationCanceledException)
            {
                slotOwned = false;
                record.Terminated = true;
                EditorMainThreadDispatcher.Enqueue(() => logSink.Invoke($"Execution #{executionId} cancelled by caller.", TerminalLogLevel.Warning));
                return -1;
            }
            catch (Exception failure)
            {
                record.ExitCode = -1;
                EditorMainThreadDispatcher.Enqueue(() => logSink.Invoke($"Execution fault: {failure.Message}", TerminalLogLevel.Error));
                return -1;
            }
            finally
            {
                chronometer.Stop();
                record.Duration = chronometer.Elapsed;
                record.PeakWorkingSetBytes = SampleWorkingSet();
                activeExecutionId = 0;
                Interlocked.Exchange(ref completionProbe, 0);
                
                PythonExecutionRecord finalRecord = record;
                EditorMainThreadDispatcher.Enqueue(() => ExecutionCompleted?.Invoke(finalRecord));

                if (slotOwned) executionSlot.Release();
            }
        }

        public bool KillActiveExecution()
        {
            lock (processGate)
            {
                if (hostedProcess == null || hostedProcess.HasExited)
                {
                    EditorMainThreadDispatcher.Enqueue(() => logSink.Invoke("No live Python process available for termination.", TerminalLogLevel.Warning));
                    return false;
                }
                try
                {
                    TerminateProcessTree(hostedProcess);
                    int currentId = activeExecutionId;
                    EditorMainThreadDispatcher.Enqueue(() => logSink.Invoke($"Kill switch engaged on execution #{currentId}.", TerminalLogLevel.Error));
                    return true;
                }
                catch (Exception failure)
                {
                    EditorMainThreadDispatcher.Enqueue(() => logSink.Invoke($"Kill switch failure: {failure.Message}", TerminalLogLevel.Error));
                    return false;
                }
            }
        }

        private static void TerminateProcessTree(Process target)
        {
            try { target.Kill(); }
            catch (Exception) { }
        }

        public IReadOnlyList<string> ProbeQuickVariables()
        {
            lock (variableGate) { return trackedGlobals.ToArray(); }
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
            if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return configured;
            return IsWindowsHost() ? "python" : "python3";
        }

        private static bool IsWindowsHost() => Path.DirectorySeparatorChar == '\\';

        private void PreparePythonNetProbe()
        {
            Type engineType = Type.GetType("Python.Runtime.PythonEngine, pythonnet");
            if (engineType == null) { backendKind = PythonBackendKind.SystemProcess; return; }
            backendKind = PythonBackendKind.PythonNetRuntime;
            EditorMainThreadDispatcher.Enqueue(() => logSink.Invoke("PythonNet assembly detected; in-process backend selected.", TerminalLogLevel.System));
        }

        private async Task<int> RunSystemStatementAsync(string statement, int executionId, CancellationToken externalToken)
        {
            Process target;
            lock (processGate)
            {
                if (hostedProcess == null || hostedProcess.HasExited) throw new InvalidOperationException("Python process is not running.");
                target = hostedProcess;
            }

            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(statement));
            string command = $"{{\"cmd\":\"exec\",\"id\":{executionId},\"src_b64\":\"{b64}\"}}\n";
            
            await target.StandardInput.WriteAsync(command).ConfigureAwait(false);
            await target.StandardInput.FlushAsync().ConfigureAwait(false);

            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            while (Interlocked.CompareExchange(ref completionProbe, 0, executionId) != executionId && DateTime.UtcNow < deadline)
            {
                externalToken.ThrowIfCancellationRequested();
                await Task.Delay(StreamDrainTimeoutMs, externalToken).ConfigureAwait(false);
            }

            return Interlocked.CompareExchange(ref completionProbe, 0, executionId) == executionId ? 0 : 137;
        }

        private Task<int> RunPythonNetStatementAsync(string statement)
        {
            return Task.Run(() =>
            {
                try
                {
                    dynamic engine = Activator.CreateInstance(Type.GetType("Python.Runtime.PythonEngine, pythonnet"));
                    dynamic result = engine.Exec(statement);
                    string output = result?.ToString() ?? string.Empty;
                    EditorMainThreadDispatcher.Enqueue(() => logSink.Invoke(output, TerminalLogLevel.PythonBridge));
                    return 0;
                }
                catch (Exception failure)
                {
                    EditorMainThreadDispatcher.Enqueue(() => logSink.Invoke($"PythonNet evaluation failed: {failure.Message}", TerminalLogLevel.Error));
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
                catch (SocketException) { continue; }
            }
            return 0;
        }

        private void StartIpcListener(int port)
        {
            if (port == 0) return;
            lock (socketGate)
            {
                ipcListener = new TcpListener(IPAddress.Loopback, port);
                ipcListener.Start();
            }
        }

        private void StopIpcListener()
        {
            lock (socketGate) { ipcListener?.Stop(); ipcListener = null; }
        }

        private async Task PumpSocketAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client = null;
                try
                {
                    client = await AcceptClientAsync(token).ConfigureAwait(false);
                    if (client == null) return;

                    using (NetworkStream stream = client.GetStream())
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(line)) DispatchIncoming(line);
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception failure)
                {
                    EditorMainThreadDispatcher.Enqueue(() => logSink.Invoke($"IPC pump error: {failure.Message}", TerminalLogLevel.Warning));
                }
                finally { client?.Dispose(); }
            }
        }

        private async Task<TcpClient> AcceptClientAsync(CancellationToken token)
        {
            TcpListener listener;
            lock (socketGate) { listener = ipcListener; }
            if (listener == null) return null;
            token.Register(() => listener.Stop());
            return await listener.AcceptTcpClientAsync().ConfigureAwait(false);
        }

        private void HandleOutputLine(object sender, DataReceivedEventArgs args)
        {
            if (args.Data == null) return;
            EditorMainThreadDispatcher.Enqueue(() => DispatchIncoming(args.Data));
        }

        private void HandleErrorLine(object sender, DataReceivedEventArgs args)
        {
            if (args.Data == null) return;
            EditorMainThreadDispatcher.Enqueue(() => DispatchIncoming(args.Data));
        }

        private void DispatchIncoming(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return;

            if (payload.StartsWith("{"))
            {
                string evt = ExtractJsonString(payload, "evt");
                int id = ExtractJsonInt(payload, "id");
                string dataB64 = ExtractJsonString(payload, "data_b64");
                
                string data = string.Empty;
                if (!string.IsNullOrEmpty(dataB64))
                {
                    try
                    {
                        byte[] bytes = Convert.FromBase64String(dataB64);
                        data = Encoding.UTF8.GetString(bytes);
                    }
                    catch
                    {
                        data = dataB64;
                    }
                }

                if (evt == "out")
                {
                    EditorMainThreadDispatcher.Enqueue(() => logSink.Invoke(data, TerminalLogLevel.PythonBridge));
                }
                else if (evt == "err")
                {
                    EditorMainThreadDispatcher.Enqueue(() => logSink.Invoke(data, TerminalLogLevel.Error));
                }
                else if (evt == "done")
                {
                    CompleteActive(id);
                }
                else if (evt == "ready")
                {
                    SignalReady();
                }
                else
                {
                    EditorMainThreadDispatcher.Enqueue(() => logSink.Invoke(payload, TerminalLogLevel.Information));
                }
            }
            else if (payload == "READY")
            {
                SignalReady();
            }
            else
            {
                EditorMainThreadDispatcher.Enqueue(() => logSink.Invoke(payload, TerminalLogLevel.Information));
            }
        }

        private string ExtractJsonString(string json, string key)
        {
            string search = "\"" + key + "\"";
            int start = json.IndexOf(search, StringComparison.Ordinal);
            if (start < 0) return string.Empty;
            start += search.Length;
            
            while (start < json.Length && (json[start] == ' ' || json[start] == ':' || json[start] == '\t'))
            {
                start++;
            }
            
            if (start >= json.Length || json[start] != '"') return string.Empty;
            start++;
            
            int end = json.IndexOf("\"", start, StringComparison.Ordinal);
            if (end < 0) return string.Empty;
            return json.Substring(start, end - start);
        }

        private int ExtractJsonInt(string json, string key)
        {
            string search = "\"" + key + "\"";
            int start = json.IndexOf(search, StringComparison.Ordinal);
            if (start < 0) return 0;
            start += search.Length;
            
            while (start < json.Length && (json[start] == ' ' || json[start] == ':' || json[start] == '\t'))
            {
                start++;
            }
            
            int end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-'))
            {
                end++;
            }
            if (end == start) return 0;
            int.TryParse(json.Substring(start, end - start), out int result);
            return result;
        }

        private void SignalReady()
        {
            if (!awaitingHandshake) return;
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
                if (!trackedGlobals.Contains(descriptor)) trackedGlobals.Add(descriptor);
            }
        }

        private void RefreshTrackedGlobals()
        {
            lock (variableGate)
            {
                if (trackedGlobals.Count > 256) trackedGlobals.RemoveRange(0, trackedGlobals.Count - 256);
            }
        }

        private long SampleWorkingSet()
        {
            lock (processGate)
            {
                try { return hostedProcess != null && !hostedProcess.HasExited ? hostedProcess.WorkingSet64 : 0L; }
                catch { return 0L; }
            }
        }

        private void HandleProcessExit(object sender, EventArgs args)
        {
            EditorMainThreadDispatcher.Enqueue(() =>
            {
                logSink.Invoke("Python process terminated by the operating system.", TerminalLogLevel.System);
                readySignal?.TrySetResult(false);
            });
        }

        private static class BootstrapPayloadRepository
        {
            public const string ResourcePath = "Assets/Editor/AdvancedTerminal/Resources/unity_bootstrap.py";

            public static string Build()
            {
                if (File.Exists(ResourcePath)) return File.ReadAllText(ResourcePath, Encoding.UTF8);

                return @"import sys
import os
import traceback
import io
import json
import base64

_scope = {""__name__"": ""__main__""}

def _send_json(evt, exec_id=0, data=""""):
    b64 = base64.b64encode(data.encode('utf-8')).decode('ascii')
    msg = {""evt"": evt, ""id"": exec_id, ""data_b64"": b64}
    print(json.dumps(msg))
    sys.stdout.flush()

def main():
    _send_json(""ready"")

    for raw_line in sys.stdin:
        raw_line = raw_line.strip()
        if not raw_line:
            continue

        try:
            frame = json.loads(raw_line)
            cmd = frame.get(""cmd"")
            exec_id = frame.get(""id"", 0)
            
            if cmd == ""exec"":
                b64_src = frame.get(""src_b64"", """")
                src = base64.b64decode(b64_src).decode('utf-8')
                
                out_capture = io.StringIO()
                err_capture = io.StringIO()
                old_out, old_err = sys.stdout, sys.stderr
                sys.stdout, sys.stderr = out_capture, err_capture
                
                try:
                    compiled_code = compile(src, ""<string>"", ""exec"")
                    exec(compiled_code, _scope)
                except Exception:
                    traceback.print_exc(file=err_capture)
                finally:
                    sys.stdout, sys.stderr = old_out, old_err
                    
                out_val = out_capture.getvalue()
                err_val = err_capture.getvalue()
                
                if out_val:
                    _send_json(""out"", exec_id, out_val)
                if err_val:
                    _send_json(""err"", exec_id, err_val)
                    
                _send_json(""done"", exec_id)
                
        except Exception as e:
            _send_json(""err"", 0, f""Bootstrap parse error: {str(e)}"")

if __name__ == ""__main__"":
    main()";
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