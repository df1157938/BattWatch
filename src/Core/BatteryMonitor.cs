using BattWatch.Providers;

namespace BattWatch.Core;

/// <summary>Polls every provider, merges results, records history and raises low-battery alerts.</summary>
public sealed class BatteryMonitor : IDisposable
{
    readonly Settings _settings;
    readonly HistoryStore _history;
    readonly List<IBatteryProvider> _providers;
    readonly SystemBatteryProvider _system = new();
    readonly MchoseHidProvider _mchose = new();
    readonly HashSet<string> _alerted = new();
    readonly SemaphoreSlim _gate = new(1, 1);
    CancellationTokenSource? _loop;

    public IReadOnlyList<DeviceBattery> Devices { get; private set; } = [];
    public DateTime LastRefresh { get; private set; }

    /// <summary>Raised on a background thread after every refresh.</summary>
    public event Action? Updated;
    /// <summary>Raised once per device each time it drops to the threshold.</summary>
    public event Action<DeviceBattery>? LowBattery;

    public BatteryMonitor(Settings settings, HistoryStore history)
    {
        _settings = settings;
        _history = history;
        _providers = [new BluetoothProvider(), _mchose, new X87sHidProvider(), _system];
    }

    public void Start()
    {
        _loop = new CancellationTokenSource();
        var ct = _loop.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                await RefreshAsync();
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, _settings.PollSeconds)), ct); }
                catch (OperationCanceledException) { }
            }
        }, ct);
    }

    public async Task RefreshAsync()
    {
        if (!await _gate.WaitAsync(0)) return; // a refresh is already running
        try
        {
            var tasks = _providers
                .Where(p => p != _system || _settings.ShowLaptopBattery)
                .Select(ReadWithTimeoutAsync);
            var all = (await Task.WhenAll(tasks)).SelectMany(x => x)
                .GroupBy(d => d.Id).Select(g => g.First())
                .OrderBy(d => d.Kind == DeviceKind.Laptop).ThenBy(d => d.Name)
                .ToList();

            Devices = all;
            LastRefresh = DateTime.Now;
            _history.Record(all);
            CheckAlerts(all);
            Updated?.Invoke();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs a provider on the thread pool with a hard timeout: some WinRT Bluetooth calls can stall
    /// for a long time on flaky radios, and one slow provider must not block the others.
    /// </summary>
    static async Task<IReadOnlyList<DeviceBattery>> ReadWithTimeoutAsync(IBatteryProvider p)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var work = Task.Run(() => p.ReadAsync(cts.Token));
        var done = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(20)));
        if (done != work)
        {
            Log.Write($"{p.Name}: timed out after {sw.Elapsed.TotalSeconds:0.0}s");
            return [];
        }
        try
        {
            var r = await work;
            Log.Write($"{p.Name}: {r.Count} device(s) in {sw.ElapsedMilliseconds} ms");
            return r;
        }
        catch (Exception ex)
        {
            Log.Write($"{p.Name}: {ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }

    public IEnumerable<DeviceBattery> Visible => Devices.Where(d => !_settings.HiddenIds.Contains(d.Id));

    void CheckAlerts(List<DeviceBattery> all)
    {
        foreach (var d in all)
        {
            bool low = d.Level <= _settings.LowThreshold && d.IsCharging != true;
            if (low && !_settings.HiddenIds.Contains(d.Id) && _alerted.Add(d.Id))
                LowBattery?.Invoke(d);
            // Re-arm after charging or a clear recovery, so small jitter doesn't re-notify.
            else if (!low && (d.IsCharging == true || d.Level > _settings.LowThreshold + 5))
                _alerted.Remove(d.Id);
        }
    }

    public void Dispose()
    {
        _loop?.Cancel();
        _mchose.Dispose();
    }
}
