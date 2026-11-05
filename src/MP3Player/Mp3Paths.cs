using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;

namespace MP3Player
{
    internal static class Mp3Paths
    {
        public static string[] Resolve(IEnumerable<string> paths)
        {
            var result = new List<string>();
            foreach (string path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                try
                {
                    result.Add(Path.GetFullPath(path));
                }
                catch (Exception ex) when (IsPathError(ex))
                {
                    Trace.WriteLine(ex);
                }
            }
            return result.ToArray();
        }

        public static IEnumerable<string> Expand(IEnumerable<string> paths)
        {
            var files = new List<string>();
            foreach (string path in Resolve(paths))
            {
                try
                {
                    if (Directory.Exists(path))
                        AddDirectoryFiles(path, files);
                    else if (IsMp3(path) && File.Exists(path))
                        files.Add(path);
                }
                catch (Exception ex) when (IsPathError(ex))
                {
                    Trace.WriteLine(ex);
                }
            }

            return files.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.Ordinal);
        }

        private static void AddDirectoryFiles(string path, List<string> files)
        {
            var pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(path));
            while (pending.Count > 0)
            {
                DirectoryInfo directory = pending.Pop();
                try
                {
                    foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
                    {
                        if (entry is DirectoryInfo child)
                        {
                            // Avoid cycles through junctions and directory symlinks.
                            if ((child.Attributes & FileAttributes.ReparsePoint) == 0)
                                pending.Push(child);
                        }
                        else if (IsMp3(entry.Name))
                        {
                            files.Add(entry.FullName);
                        }
                    }
                }
                catch (Exception ex) when (IsPathError(ex))
                {
                    Trace.WriteLine(ex);
                }
            }
        }

        private static bool IsMp3(string path)
        {
            return string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPathError(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException
                || ex is ArgumentException || ex is NotSupportedException;
        }
    }
}
