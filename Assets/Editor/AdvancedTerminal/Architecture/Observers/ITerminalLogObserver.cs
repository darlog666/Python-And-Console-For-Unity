using System;

namespace UnityAdvancedTerminal.Architecture.Observers
{
    public enum TerminalLogLevel
    {
        Information,
        Input,
        Success,
        Warning,
        Error,
        System,
        PythonBridge
    }

    public readonly struct TerminalLogEntry : IEquatable<TerminalLogEntry>
    {
        private static long sequenceCounter;

        public long SequenceId { get; }

        public DateTimeOffset Timestamp { get; }

        public string Message { get; }

        public TerminalLogLevel Level { get; }

        public string SourceChannel { get; }

        public TerminalLogEntry(string message, TerminalLogLevel level, string sourceChannel)
        {
            SequenceId = ++sequenceCounter;
            Timestamp = DateTimeOffset.Now;
            Message = message ?? string.Empty;
            Level = level;
            SourceChannel = sourceChannel ?? "core";
        }

        public bool Equals(TerminalLogEntry other)
        {
            return SequenceId == other.SequenceId;
        }

        public override bool Equals(object obj)
        {
            return obj is TerminalLogEntry entry && Equals(entry);
        }

        public override int GetHashCode()
        {
            return SequenceId.GetHashCode();
        }

        public override string ToString()
        {
            return $"[{Timestamp:HH:mm:ss.fff}] {Message}";
        }
    }

    public interface ITerminalLogObserver
    {
        string ObserverId { get; }

        bool Accepts(TerminalLogEntry entry);

        void OnLogEnqueued(TerminalLogEntry entry);

        void OnBatchFlushed(TerminalLogEntry[] batch);

        void OnBufferCleared();
    }
}
