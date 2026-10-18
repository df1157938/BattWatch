using System.Globalization;
using System.IO;
using System.Text;

namespace BattWatch.Core;

public readonly record struct HistoryPoint(DateTime Time, string Id, string Name, int Level, bool Charging);

/// <summary>
/// Append-only CSV, one file per month. A row is written when a device's level or charging
/// state changes, or at most every 10 minutes otherwise, so files stay small.
/// </summary>
public sealed class HistoryStore
{
    static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(10);
    readonly Dictionary<string, HistoryPoint> _lastWritten = new();
    readonly object _lock = new();

    public static string Dir => Path.Combine(Settings.DataDir, "history");

    public void Record(IEnumerable<DeviceBattery> readings)
    {
        var now = DateTime.Now;
        var sb = new StringBuilder();
        lock (_lock)
        {
            foreach (var d in readings)
            {
                bool charging = d.IsCharging == true;
                if (_lastWritten.TryGetValue(d.Id, out var last)
                    && last.Level == d.Level && last.Charging == charging && now - last.Time < Heartbeat)
                    continue;
                var p = new HistoryPoint(now, d.Id, d.Name, d.Level, charging);
                _lastWritten[d.Id] = p;
                sb.Append(now.ToString("s", CultureInfo.InvariantCulture)).Append(',')
                  .Append(Escape(d.Id)).Append(',').Append(Escape(d.Name)).Append(',')
                  .Append(d.Level).Append(',').Append(charging ? 1 : 0).Append('\n');
            }
            if (sb.Length == 0) return;
            Directory.CreateDirectory(Dir);
            File.AppendAllText(FileFor(now), sb.ToString(), Encoding.UTF8);
        }
    }

    public List<HistoryPoint> Load(DateTime from)
    {
        var list = new List<HistoryPoint>();
        if (!Directory.Exists(Dir)) return list;
        for (var m = new DateTime(from.Year, from.Month, 1); m <= DateTime.Now; m = m.AddMonths(1))
        {
            var f = FileFor(m);
            if (!File.Exists(f)) continue;
            foreach (var line in File.ReadLines(f, Encoding.UTF8))
            {
                var parts = SplitCsv(line);
                if (parts.Count < 5) continue;
                if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) || t < from) continue;
                if (!int.TryParse(parts[3], out var lvl)) continue;
                list.Add(new HistoryPoint(t, parts[1], parts[2], lvl, parts[4] == "1"));
            }
        }
        return list;
    }

    /// <summary>Deletes every history file; returns how many were removed.</summary>
    public int Clear()
    {
        lock (_lock)
        {
            _lastWritten.Clear();
            if (!Directory.Exists(Dir)) return 0;
            var files = Directory.GetFiles(Dir, "*.csv");
            foreach (var f in files) File.Delete(f);
            return files.Length;
        }
    }

    static string FileFor(DateTime t) => Path.Combine(Dir, $"{t:yyyy-MM}.csv");

    static string Escape(string s) =>
        s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    static List<string> SplitCsv(string line)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else sb.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { parts.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        parts.Add(sb.ToString());
        return parts;
    }
}
