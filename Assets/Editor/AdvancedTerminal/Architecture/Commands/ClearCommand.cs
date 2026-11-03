using System;
using AdvancedTerminal.Architecture.Observers;

namespace AdvancedTerminal.Architecture.Commands
{
    public sealed class ClearCommand : TerminalCommandBase
    {
        private readonly Action bufferResetSignal;

        public ClearCommand(Action bufferResetSignal)
            : base("clear", "Purges the console viewport buffer.", "clear [--silent]", "cls")
        {
            this.bufferResetSignal = bufferResetSignal
                ?? throw new ArgumentNullException(nameof(bufferResetSignal));
        }

        public override void Execute(TerminalCommandContext context)
        {
            bufferResetSignal.Invoke();

            if (!context.HasFlag("--silent"))
            {
                context.EmitLog("Console buffer purged.", TerminalLogLevel.Success);
            }
        }
    }
}
