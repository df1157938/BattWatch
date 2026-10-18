using System.IO;

namespace BattWatch.Core;

/// <summary>Tiny rolling diagnostic log in the data folder (truncated past 512 KB).</summary>
public static class Log
{
    static readonly object Lock = new();
    static string FilePath => Path.Combine(Settings.DataDir, "battwatch.log");

    public static void Write(string message)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Settings.DataDir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}\n");
            }
        }
        catch
        {
            // logging must never take the app down
        }
    }
}
