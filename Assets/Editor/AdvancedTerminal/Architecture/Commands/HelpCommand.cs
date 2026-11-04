using System;
using System.Collections.Generic;
using System.Text;
using UnityAdvancedTerminal.Architecture.Observers;

namespace UnityAdvancedTerminal.Architecture.Commands
{
    public sealed class HelpCommand : TerminalCommandBase
    {
        private readonly Func<IReadOnlyList<ITerminalCommand>> catalogSupplier;

        public HelpCommand(Func<IReadOnlyList<ITerminalCommand>> catalogSupplier)
            : base("help", "Lists every registered terminal command.", "help [command]", "?")
        {
            this.catalogSupplier = catalogSupplier
                ?? throw new ArgumentNullException(nameof(catalogSupplier));
        }

        public override void Execute(TerminalCommandContext context)
        {
            IReadOnlyList<ITerminalCommand> catalog = catalogSupplier.Invoke();
            string target = context.Positional(0);

            if (!string.IsNullOrEmpty(target))
            {
                EmitFocusedHelp(context, catalog, target);
                return;
            }

            EmitFullCatalog(context, catalog);
        }

        private void EmitFocusedHelp(TerminalCommandContext context,
            IReadOnlyList<ITerminalCommand> catalog, string target)
        {
            foreach (ITerminalCommand command in catalog)
            {
                if (command.Matches(target))
                {
                    context.EmitLog($"{command.Name} -> {command.UsageHint}", TerminalLogLevel.Information);
                    context.EmitLog(command.Description, TerminalLogLevel.Information);
                    return;
                }
            }

            context.EmitLog($"No command mapped to '{target}'.", TerminalLogLevel.Warning);
        }

        private void EmitFullCatalog(TerminalCommandContext context,
            IReadOnlyList<ITerminalCommand> catalog)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("Registered terminal commands:");

            foreach (ITerminalCommand command in catalog)
            {
                builder.Append("  ");
                builder.Append(command.Name.PadRight(12));
                builder.Append(" :: ");
                builder.Append(command.Description);

                if (command.Aliases.Count > 0)
                {
                    builder.Append(" [alias: ");
                    builder.Append(string.Join(", ", command.Aliases));
                    builder.Append(']');
                }

                builder.AppendLine();
            }

            context.EmitLog(builder.ToString().TrimEnd(), TerminalLogLevel.Information);
        }
    }
}
