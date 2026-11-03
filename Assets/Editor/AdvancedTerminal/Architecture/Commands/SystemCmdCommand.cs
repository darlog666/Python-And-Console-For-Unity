using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using AdvancedTerminal.Architecture.Observers;

namespace AdvancedTerminal.Architecture.Commands
{
    public sealed class SystemCmdCommand : TerminalCommandBase
    {
        private const int TimeoutMilliseconds = 30000;

        public SystemCmdCommand()
            : base("system", "Executes a native OS shell command outside the Python runtime.",
                "system <ls|pwd|whoami|echo ...>", "os", "shell")
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

            RunNativeAsync(context, payload).ConfigureAwait(false);
        }

        private string RebuildPayload(TerminalCommandContext context)
        {
            StringBuilder builder = new StringBuilder();

            for (int i = 0; i < context.Arguments.Count; i++)
            {
                if (context.Arguments[i].StartsWith("-", StringComparison.Ordinal))
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(context.Arguments[i]);
            }

            return builder.ToString();
        }

        private async Task RunNativeAsync(TerminalCommandContext context, string payload)
        {
            ProcessStartInfo startInfo = BuildStartInfo(payload);
            Process process = new Process { StartInfo = startInfo };

            try
            {
                process.Start();
            }
            catch (Exception failure)
            {
                context.EmitLog($"Native launch failed: {failure.Message}", TerminalLogLevel.Error);
                process.Dispose();
                return;
            }

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();

            bool exited = await Task.Run(() => process.WaitForExit(TimeoutMilliseconds))
                .ConfigureAwait(false);

            if (!exited)
            {
                TryTerminate(process, context);
                return;
            }

            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);

            ReportStream(context, stdout, TerminalLogLevel.Information);
            ReportStream(context, stderr, TerminalLogLevel.Error);

            context.EmitLog($"Process exit code: {process.ExitCode}",
                process.ExitCode == 0 ? TerminalLogLevel.Success : TerminalLogLevel.Warning);
        }

        private ProcessStartInfo BuildStartInfo(string payload)
        {
            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "/bin/sh",
                Arguments = isWindows ? $"/c {payload}" : $"-c \"{payload}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Directory.GetCurrentDirectory(),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            return startInfo;
        }

        private void TryTerminate(Process process, TerminalCommandContext context)
        {
            try
            {
                process.Kill();
                context.EmitLog("Native process exceeded timeout window and was terminated.",
                    TerminalLogLevel.Warning);
            }
            catch (Exception failure)
            {
                context.EmitLog($"Kill switch tripped but termination failed: {failure.Message}",
                    TerminalLogLevel.Error);
            }
            finally
            {
                process.Dispose();
            }
        }

        private void ReportStream(TerminalCommandContext context, string stream,
            TerminalLogLevel level)
        {
            if (string.IsNullOrEmpty(stream))
            {
                return;
            }

            string[] lines = stream.Replace("\r\n", "\n").Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(lines[i]))
                {
                    context.EmitLog(lines[i], level);
                }
            }
        }
    }
}
