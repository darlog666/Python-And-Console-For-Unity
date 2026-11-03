using System;
using System.Collections.Generic;
using AdvancedTerminal.Architecture.Observers;

namespace AdvancedTerminal.Architecture.Commands
{
    public sealed class TerminalCommandContext
    {
        public string RawInput { get; }

        public string CommandName { get; }

        public IReadOnlyList<string> Arguments { get; }

        public Action<string, TerminalLogLevel> EmitLog { get; }

        public TerminalCommandContext(
            string rawInput,
            string commandName,
            IReadOnlyList<string> arguments,
            Action<string, TerminalLogLevel> emitLog)
        {
            RawInput = rawInput ?? string.Empty;
            CommandName = commandName ?? string.Empty;
            Arguments = arguments ?? Array.Empty<string>();
            EmitLog = emitLog ?? throw new ArgumentNullException(nameof(emitLog));
        }

        public bool HasFlag(string flag)
        {
            foreach (string argument in Arguments)
            {
                if (string.Equals(argument, flag, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public string Positional(int index, string fallback = "")
        {
            int offset = 0;

            for (int i = 0; i < Arguments.Count; i++)
            {
                if (Arguments[i].StartsWith("-", StringComparison.Ordinal))
                {
                    continue;
                }

                if (offset == index)
                {
                    return Arguments[i];
                }

                offset++;
            }

            return fallback;
        }
    }

    public interface ITerminalCommand
    {
        string Name { get; }

        string Description { get; }

        string UsageHint { get; }

        IReadOnlyList<string> Aliases { get; }

        bool Matches(string token);

        void Execute(TerminalCommandContext context);
    }

    public abstract class TerminalCommandBase : ITerminalCommand
    {
        private readonly IReadOnlyList<string> aliases;

        protected TerminalCommandBase(string name, string description, string usageHint,
            params string[] aliasTokens)
        {
            Name = name;
            Description = description;
            UsageHint = usageHint;
            aliases = aliasTokens ?? Array.Empty<string>();
        }

        public string Name { get; }

        public string Description { get; }

        public string UsageHint { get; }

        public IReadOnlyList<string> Aliases => aliases;

        public virtual bool Matches(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return false;
            }

            if (string.Equals(token, Name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (string alias in aliases)
            {
                if (string.Equals(token, alias, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public abstract void Execute(TerminalCommandContext context);

        protected void RequireUsage(TerminalCommandContext context, string detail)
        {
            context.EmitLog($"{UsageHint} :: {detail}", TerminalLogLevel.Warning);
        }
    }
}
