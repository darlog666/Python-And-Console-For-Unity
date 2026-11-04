using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UnityAdvancedTerminal.Utility
{
    public sealed class StoredScript
    {
        public string FileName { get; set; }

        public string FullPath { get; set; }

        public DateTimeOffset LastWriteUtc { get; set; }

        public int LineCount { get; set; }

        public long ByteSize { get; set; }
    }

    public interface IScriptStorageManager
    {
        string WorkspaceRoot { get; }

        IReadOnlyList<StoredScript> EnumerateScripts();

        string ReadContent(string fileName);

        StoredScript SaveContent(string fileName, string content);

        bool DeleteScript(string fileName);

        string ResolveWorkspaceRelative(string absolutePath);
    }

    public sealed class ScriptStorageManager : IScriptStorageManager
    {
        private const int MaxEnumeratedEntries = 512;

        public ScriptStorageManager(string workspaceRoot)
        {
            WorkspaceRoot = workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot));
            EnsureDirectory();
        }

        public string WorkspaceRoot { get; }

        public IReadOnlyList<StoredScript> EnumerateScripts()
        {
            EnsureDirectory();
            List<StoredScript> inventory = new List<StoredScript>();
            DirectoryInfo root = new DirectoryInfo(WorkspaceRoot);

            foreach (FileInfo file in root.EnumerateFiles("*.py", SearchOption.AllDirectories))
            {
                inventory.Add(Hydrate(file));

                if (inventory.Count >= MaxEnumeratedEntries)
                {
                    break;
                }
            }

            inventory.Sort((left, right) =>
                string.Compare(left.FileName, right.FileName, StringComparison.OrdinalIgnoreCase));

            return inventory;
        }

        public string ReadContent(string fileName)
        {
            string path = SanitizeAndCombine(fileName);

            if (!File.Exists(path))
            {
                return string.Empty;
            }

            return File.ReadAllText(path, Encoding.UTF8);
        }

        public StoredScript SaveContent(string fileName, string content)
        {
            string path = SanitizeAndCombine(fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content ?? string.Empty, Encoding.UTF8);
            return Hydrate(new FileInfo(path));
        }

        public bool DeleteScript(string fileName)
        {
            string path = SanitizeAndCombine(fileName);

            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }

        public string ResolveWorkspaceRelative(string absolutePath)
        {
            string normalizedRoot = WorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar) +
                                    Path.DirectorySeparatorChar;

            if (absolutePath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return absolutePath.Substring(normalizedRoot.Length);
            }

            return absolutePath;
        }

        private string SanitizeAndCombine(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new ArgumentException("Script identifier cannot be empty.", nameof(fileName));
            }

            string sanitized = fileName.Replace("..", string.Empty)
                                       .Replace(Path.DirectorySeparatorChar.ToString(), "_")
                                       .Replace(Path.AltDirectorySeparatorChar.ToString(), "_");

            if (!sanitized.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            {
                sanitized += ".py";
            }

            return Path.Combine(WorkspaceRoot, sanitized);
        }

        private StoredScript Hydrate(FileInfo file)
        {
            return new StoredScript
            {
                FileName = file.Name,
                FullPath = file.FullName,
                LastWriteUtc = file.LastWriteTimeUtc,
                ByteSize = file.Length,
                LineCount = CountLines(file.FullName)
            };
        }

        private int CountLines(string path)
        {
            int counter = 0;

            using (StreamReader reader = new StreamReader(path, Encoding.UTF8))
            {
                while (reader.ReadLine() != null)
                {
                    counter++;
                }
            }

            return counter;
        }

        private void EnsureDirectory()
        {
            if (!Directory.Exists(WorkspaceRoot))
            {
                Directory.CreateDirectory(WorkspaceRoot);
            }
        }
    }
}
