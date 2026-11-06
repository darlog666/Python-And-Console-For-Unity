using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using UnityAdvancedTerminal.Architecture.Commands;
using UnityAdvancedTerminal.Architecture.Observers;
using UnityAdvancedTerminal.Utility;

namespace UnityAdvancedTerminal.Architecture.Commands
{
    public sealed class SystemCmdCommand : TerminalCommandBase
    {
        private const int TimeoutMilliseconds = 60000;

        public SystemCmdCommand()
             : base("system", "Executes a native OS shell command.", "system <command>", "os", "shell")
        {
        }

        public override void Execute(TerminalCommandContext context)
        {
            string payload = RebuildPayload(context);
            if (string.IsNullOrWhiteSpace(payload))
            {
                RequireUsage(context, "a native executable token is required");
                return;
            }
            _ = RunNativeAsync(context, payload);
        }

        private string RebuildPayload(TerminalCommandContext context)
        {
            return string.Join(" ", context.Arguments);
        }

        private async Task RunNativeAsync(TerminalCommandContext context, string payload)
        {
            ProcessStartInfo startInfo = BuildStartInfo(payload);
            Process process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            TaskCompletionSource<bool> exitTcs = new TaskCompletionSource<bool>();
            
            process.OutputDataReceived += (sender, args) =>
            {
                if (args.Data != null)
                {
                    EditorMainThreadDispatcher.Enqueue(() => context.EmitLog(args.Data, TerminalLogLevel.Information));
                }
            };
            process.ErrorDataReceived += (sender, args) =>
            {
                if (args.Data != null)
                {
                    EditorMainThreadDispatcher.Enqueue(() => context.EmitLog(args.Data, TerminalLogLevel.Error));
                }
            };
            process.Exited += (sender, args) => exitTcs.TrySetResult(true);
            
            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                
                Task delayTask = Task.Delay(TimeoutMilliseconds);
                Task completedTask = await Task.WhenAny(exitTcs.Task, delayTask);
                
                if (completedTask == delayTask)
                {
                    TryTerminate(process, context);
                    process.Dispose();
                    return;
                }
                
                process.WaitForExit(); 
                
                int code = process.ExitCode;
                EditorMainThreadDispatcher.Enqueue(() => 
                    context.EmitLog($"Process exit code: {code}",
                        code == 0 ? TerminalLogLevel.Success : TerminalLogLevel.Warning));
            }
            catch (Exception failure)
            {
                EditorMainThreadDispatcher.Enqueue(() => 
                    context.EmitLog($"Native launch failed: {failure.Message}", TerminalLogLevel.Error));
            }
            finally
            {
                process.Dispose();
            }
        }

        private ProcessStartInfo BuildStartInfo(string payload)
        {
            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            return new ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "/bin/bash",
                Arguments = isWindows ? $"/c {payload}" : $"-c \"{payload}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Directory.GetCurrentDirectory(),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
        }

        private void TryTerminate(Process process, TerminalCommandContext context)
        {
            try
            {
                if (!process.HasExited) process.Kill();
                EditorMainThreadDispatcher.Enqueue(() => 
                    context.EmitLog("Native process exceeded timeout window and was terminated.", TerminalLogLevel.Warning));
            }
            catch (Exception failure)
            {
                EditorMainThreadDispatcher.Enqueue(() => 
                    context.EmitLog($"Termination failed: {failure.Message}", TerminalLogLevel.Error));
            }
        }
    }
}