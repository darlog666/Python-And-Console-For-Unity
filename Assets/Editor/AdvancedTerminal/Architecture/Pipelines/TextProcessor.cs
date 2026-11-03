using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using AdvancedTerminal.Architecture.Observers;

namespace AdvancedTerminal.Architecture.Pipelines
{
    public sealed class TextProcessingResult
    {
        public string ProcessedText { get; }

        public TerminalLogLevel InferredLevel { get; }

        public IReadOnlyList<string> DetectedTokens { get; }

        public TextProcessingResult(
            string processedText,
            TerminalLogLevel inferredLevel,
            IReadOnlyList<string> detectedTokens)
        {
            ProcessedText = processedText;
            InferredLevel = inferredLevel;
            DetectedTokens = detectedTokens;
        }
    }

    public interface ITextProcessor
    {
        TextProcessingResult Process(string rawText, TerminalLogLevel baselineLevel);

        string StripRichTextMarkup(string richText);

        string ToUnityRichText(string plainText, TerminalLogLevel level);

        string SerializeMatrix(IReadOnlyList<TerminalLogEntry> entries);
    }

    public sealed class TextProcessor : ITextProcessor
    {
        private const int MaxCachedLines = 4096;

        private static readonly Regex errorPattern = new Regex(
            @"\b(error|exception|traceback|failed|fatal)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex warningPattern = new Regex(
            @"\b(warning|warn|deprecated|outdated)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex successPattern = new Regex(
            @"\b(done|success|completed|saved|ok)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex markupPattern = new Regex(
            @"<\/?(?:color|b|i|u|s)\b[^>]*>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly Queue<string> normalizedLineCache = new Queue<string>();

        public TextProcessingResult Process(string rawText, TerminalLogLevel baselineLevel)
        {
            if (string.IsNullOrEmpty(rawText))
            {
                return new TextProcessingResult(string.Empty, baselineLevel, Array.Empty<string>());
            }

            string normalized = NormalizeLineEndings(rawText).TrimEnd();
            List<string> tokens = ExtractDiagnosticTokens(normalized);
            TerminalLogLevel resolvedLevel = ClassifySeverity(normalized, baselineLevel);

            return new TextProcessingResult(normalized, resolvedLevel, tokens);
        }

        public string StripRichTextMarkup(string richText)
        {
            if (string.IsNullOrEmpty(richText))
            {
                return string.Empty;
            }

            return markupPattern.Replace(richText, string.Empty);
        }

        public string ToUnityRichText(string plainText, TerminalLogLevel level)
        {
            string escaped = EscapeRichText(plainText);
            string hex = LevelPalette.For(level);
            return $"<color=#{hex}>{escaped}</color>";
        }

        public string SerializeMatrix(IReadOnlyList<TerminalLogEntry> entries)
        {
            if (entries == null || entries.Count == 0)
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder(entries.Count * 64);

            for (int index = 0; index < entries.Count; index++)
            {
                TerminalLogEntry entry = entries[index];
                builder.Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                builder.Append(" | ");
                builder.Append(entry.Level);
                builder.Append(" | ");
                builder.Append(entry.SourceChannel);
                builder.Append(" | ");
                builder.AppendLine(StripRichTextMarkup(entry.Message));
            }

            return builder.ToString();
        }

        private string NormalizeLineEndings(string input)
        {
            return input.Replace("\r\n", "\n").Replace('\r', '\n');
        }

        private TerminalLogLevel ClassifySeverity(string text, TerminalLogLevel baselineLevel)
        {
            if (baselineLevel == TerminalLogLevel.Input ||
                baselineLevel == TerminalLogLevel.System)
            {
                return baselineLevel;
            }

            if (errorPattern.IsMatch(text))
            {
                return TerminalLogLevel.Error;
            }

            if (warningPattern.IsMatch(text))
            {
                return TerminalLogLevel.Warning;
            }

            if (successPattern.IsMatch(text))
            {
                return TerminalLogLevel.Success;
            }

            return baselineLevel;
        }

        private List<string> ExtractDiagnosticTokens(string text)
        {
            List<string> tokens = new List<string>(8);
            AppendMatched(tokens, errorPattern, text);
            AppendMatched(tokens, warningPattern, text);
            AppendMatched(tokens, successPattern, text);
            CacheNormalized(text);
            return tokens;
        }

        private void AppendMatched(List<string> accumulator, Regex pattern, string text)
        {
            MatchCollection matches = pattern.Matches(text);

            foreach (Match match in matches)
            {
                string token = match.Value.ToLowerInvariant();

                if (!accumulator.Contains(token))
                {
                    accumulator.Add(token);
                }
            }
        }

        private void CacheNormalized(string line)
        {
            normalizedLineCache.Enqueue(line);

            while (normalizedLineCache.Count > MaxCachedLines)
            {
                normalizedLineCache.Dequeue();
            }
        }

        private string EscapeRichText(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return string.Empty;
            }

            return input.Replace("&", "\\u0026")
                        .Replace("<", "\\u003c")
                        .Replace(">", "\\u003e");
        }

        private static class LevelPalette
        {
            private const string Cyan = "00CED1";
            private const string Emerald = "2ECC71";
            private const string Crimson = "DC143C";
            private const string Amber = "FFBF00";
            private const string Slate = "B0BEC5";
            private const string Violet = "9B59B6";

            public static string For(TerminalLogLevel level)
            {
                switch (level)
                {
                    case TerminalLogLevel.Input:
                        return Cyan;
                    case TerminalLogLevel.Success:
                    case TerminalLogLevel.PythonBridge:
                        return Emerald;
                    case TerminalLogLevel.Error:
                        return Crimson;
                    case TerminalLogLevel.Warning:
                        return Amber;
                    case TerminalLogLevel.System:
                        return Violet;
                    default:
                        return Slate;
                }
            }
        }
    }
}
