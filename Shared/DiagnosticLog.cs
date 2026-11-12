using System;
using System.IO;
using System.Text;

namespace HonorControl.Diagnostics
{
    public static class DiagnosticLog
    {
        public const int MaxBytes = 2 * 1024 * 1024;
        private static readonly object Gate = new object();
        private static readonly string[] Names = { "activity.log", "errors.log", "broker-errors.log", "color-watcher.log" };

        public static void Append(string path, string message)
        {
            try
            {
                lock (Gate)
                {
                    CheckName(path);
                    Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                    CleanOne(path);
                    if (message.Length > 65536) message = message.Substring(0, 65536) + "\n[truncated]\n";
                    int bytes = Encoding.UTF8.GetByteCount(message);
                    if (File.Exists(path) && new FileInfo(path).Length + bytes > MaxBytes)
                    {
                        if (File.Exists(path + ".2")) File.Delete(path + ".2");
                        if (File.Exists(path + ".1")) File.Move(path + ".1", path + ".2");
                        File.Move(path, path + ".1");
                    }
                    File.AppendAllText(path, message, new UTF8Encoding(false));
                }
            }
            catch { /* Diagnostics must not interrupt device control. */ }
        }

        public static void Clean(string directory)
        {
            try { lock (Gate) { foreach (string name in Names) CleanOne(Path.Combine(directory, name)); } } catch { }
        }

        private static void CheckName(string path)
        {
            if (Array.IndexOf(Names, Path.GetFileName(path)) < 0) throw new ArgumentException("Unknown diagnostic log.");
        }

        private static void CleanOne(string path)
        {
            CheckName(path);
            DateTime cutoff = DateTime.UtcNow.AddDays(-14);
            foreach (string suffix in new[] { "", ".1", ".2" })
            {
                string file = path + suffix;
                if (!File.Exists(file)) continue;
                if (File.GetLastWriteTimeUtc(file) < cutoff) { File.Delete(file); continue; }
                if (new FileInfo(file).Length > MaxBytes)
                {
                    string tail;
                    using (var input = File.OpenRead(file))
                    {
                        input.Seek(-MaxBytes, SeekOrigin.End);
                        using (var reader = new StreamReader(input, Encoding.UTF8)) { reader.ReadLine(); tail = reader.ReadToEnd(); }
                    }
                    File.WriteAllText(file, tail, new UTF8Encoding(false));
                }
            }
        }
    }
}
