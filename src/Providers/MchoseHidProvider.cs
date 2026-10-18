using BattWatch.Core;
using BattWatch.Native;
using Microsoft.Win32.SafeHandles;

namespace BattWatch.Providers;

/// <summary>
/// MCHOSE 2.4G receivers (VID 0x3837), vendor collection usage page 0xFF01. Both protocols
/// below were taken from MCHOSE's own WebHID driver (hub at mchose.com.cn); each query is a
/// read-only "device info" request the official driver issues on every connect.
///
/// Legacy family (feature reports 0x11–0x14, e.g. A7 V2 Pro+ receiver 0x100A):
///   SetFeature 0x11: [cmd, 0…] with every byte XOR 0xFF; cmd 0x06 = device info.
///   GetFeature 0x11: [1] = ~cmd, [2..] XOR 0xFF → vid u16, pid u16, fw u32, status, battery, charge.
///
/// New family (output/input report 0x4D, 64 bytes):
///   Request [0]=0x4D, [1]=0x01, [2]=flags 0x01, [3]=len, [4..5]=cmd LE, [6]=0, [7]=0, payload,
///   then XOR of [2 .. 7+len]. Reply: [4..5]=cmd, payload from [8].
///   0x0900 device info: payload[10]=connected, [11]=charging, [12]=battery %.
///   0x0600 status push: payload[0] bit1 → battery at payload[3]; payload[1] bit1 → charge at payload[12].
/// </summary>
public sealed class MchoseHidProvider : IBatteryProvider, IDisposable
{
    const ushort Vid = 0x3837;
    const ushort UsagePage = 0xFF01;
    const byte LegacyReportId = 0x11;
    const byte NewReportId = 0x4D;

    enum Family { Legacy, New }

    sealed class Receiver
    {
        public required HidCollection Collection;
        public required SafeFileHandle Handle;
        public required Family Family;
        public volatile bool Stop;
        public DeviceBattery? Latest;
        public readonly AutoResetEvent GotInfo = new(false);
    }

    readonly Dictionary<string, Receiver> _receivers = new();
    readonly object _lock = new();
    DateTime _lastScan = DateTime.MinValue;

    /// <summary>Log raw report headers (set by --dump).</summary>
    public static bool Verbose { get; set; }

    public string Name => "MCHOSE 2.4G";

    public Task<IReadOnlyList<DeviceBattery>> ReadAsync(CancellationToken ct)
    {
        if (DateTime.Now - _lastScan > TimeSpan.FromSeconds(30)) Rescan();
        List<Receiver> rx;
        lock (_lock) rx = _receivers.Values.ToList();

        var result = new List<DeviceBattery>();
        foreach (var r in rx)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (r.Family == Family.Legacy) QueryLegacy(r);
                else QueryNew(r);
            }
            catch (Exception ex)
            {
                Log.Write($"MCHOSE {r.Collection.Pid:X4}: {ex.Message}");
            }
            lock (_lock)
                if (r.Latest is not null) result.Add(r.Latest);
        }
        return Task.FromResult<IReadOnlyList<DeviceBattery>>(result);
    }

    // ---------- legacy feature-report protocol ----------

    void QueryLegacy(Receiver r)
    {
        int len = r.Collection.FeatureLen;
        var req = new byte[len];
        Array.Fill(req, (byte)0xFF);          // XOR-0xFF encoded zeros
        req[0] = LegacyReportId;
        req[1] = 0x06 ^ 0xFF;
        if (!Hid.SetFeature(r.Handle, req))
        {
            if (Verbose) Log.Write($"  mchose legacy SetFeature failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            return;
        }

        // Until the receiver has fetched the answer from the mouse, GetFeature just echoes the
        // request back (vid = 0), so poll for up to ~1 s for a reply carrying the real VID.
        for (int attempt = 0; attempt < 20; attempt++)
        {
            Thread.Sleep(50);
            var resp = new byte[len];
            resp[0] = LegacyReportId;
            if (!Hid.GetFeature(r.Handle, resp)) continue;
            if ((byte)~resp[1] != 0x06) continue;

            var p = new byte[len - 2];
            for (int i = 0; i < p.Length; i++) p[i] = (byte)(resp[i + 2] ^ 0xFF);
            if ((p[0] | p[1] << 8) != Vid) continue;
            if (Verbose) Log.Write($"  mchose legacy reply after {attempt + 1} polls: {Convert.ToHexString(p, 0, 12)}");
            // vid(2) pid(2) fw(4) status(1) battery(1) charge(1)
            int battery = p[9];
            bool charging = p[10] != 0;
            lock (_lock)
                r.Latest = battery is > 0 and <= 100 ? Make(r.Collection, battery, charging, "接收器 · 0x11/06") : null;
            return;
        }
        if (Verbose) Log.Write("  mchose legacy: no matching reply");
    }

    // ---------- new output/input-report protocol ----------

    static byte[] BuildNewRequest(ushort command, params byte[] payload)
    {
        var buf = new byte[65];
        buf[0] = NewReportId;
        buf[1] = 0x01;
        buf[2] = 0x01;
        buf[3] = (byte)payload.Length;
        buf[4] = (byte)(command & 0xFF);
        buf[5] = (byte)(command >> 8);
        payload.CopyTo(buf, 8);
        byte x = 0;
        for (int i = 2; i <= 7 + payload.Length; i++) x ^= buf[i];
        buf[8 + payload.Length] = x;
        return buf;
    }

    void QueryNew(Receiver r)
    {
        // The driver retries while the mouse reports 0% (just woke / link not settled).
        for (int attempt = 0; attempt < 3; attempt++)
        {
            r.GotInfo.Reset();
            if (!Hid.WriteReport(r.Handle, BuildNewRequest(0x0900))) return;
            if (!r.GotInfo.WaitOne(1500)) return;
            if (r.Latest is not null) return;
            Thread.Sleep(100);
        }
    }

    void Listen(Receiver r)
    {
        var c = r.Collection;
        try
        {
            while (!r.Stop)
            {
                var buf = Hid.ReadReport(r.Handle, c.InputLen, 1000);
                if (buf is null || buf.Length < 21 || buf[0] != NewReportId) continue;
                ushort cmd = (ushort)(buf[4] | buf[5] << 8);
                if (Verbose) Log.Write($"  mchose rx {Convert.ToHexString(buf, 0, 24)}");
                var p = buf.AsSpan(8);
                if (cmd == 0x0900)
                {
                    lock (_lock)
                        r.Latest = p[10] != 0 && p[12] > 0 ? Make(c, p[12], p[11] != 0, "接收器 · 0x0900") : null;
                    r.GotInfo.Set();
                }
                else if (cmd == 0x0600)
                {
                    lock (_lock)
                    {
                        var prev = r.Latest;
                        int? level = (p[0] & 0x02) != 0 ? p[3] : prev?.Level;
                        bool? charging = (p[1] & 0x02) != 0 ? p[12] != 0 : prev?.IsCharging;
                        if (level is not null)
                            r.Latest = level > 0 ? Make(c, level.Value, charging, "接收器主动上报") : null; // 0 = asleep
                    }
                }
            }
        }
        catch
        {
            // receiver unplugged; Rescan drops it
        }
    }

    // ---------- discovery ----------

    void Rescan()
    {
        _lastScan = DateTime.Now;
        List<(HidCollection C, Family F)> found;
        try
        {
            found = Hid.Enumerate()
                .Where(c => c.Vid == Vid && c.UsagePage == UsagePage)
                .Select(c => (c, Classify(c)))
                .Where(x => x.Item2 is not null)
                .Select(x => (x.c, x.Item2!.Value))
                .ToList();
        }
        catch
        {
            return;
        }

        lock (_lock)
        {
            foreach (var gone in _receivers.Keys.Except(found.Select(f => f.C.Path)).ToList())
            {
                var r = _receivers[gone];
                r.Stop = true;
                if (r.Family == Family.Legacy) r.Handle.Dispose();
                _receivers.Remove(gone);
            }
            foreach (var (c, family) in found)
            {
                if (_receivers.ContainsKey(c.Path)) continue;
                var h = Hid.OpenReadWrite(c.Path, overlapped: family == Family.New);
                if (h.IsInvalid) { h.Dispose(); continue; }
                var r = new Receiver { Collection = c, Handle = h, Family = family };
                _receivers[c.Path] = r;
                if (Verbose) Log.Write($"  mchose receiver {c.Pid:X4} family={family}");
                if (family == Family.New)
                    new Thread(() => { Listen(r); h.Dispose(); })
                        { IsBackground = true, Name = "mchose-" + c.Pid.ToString("X4") }.Start();
            }
        }
    }

    static Family? Classify(HidCollection c)
    {
        var featureIds = Hid.GetValueCaps(c.Path).Where(v => v.Type == Hid.HIDP_REPORT_TYPE.Feature).Select(v => v.ReportId)
            .Concat(Hid.GetButtonReportIds(c.Path).Where(b => b.Type == Hid.HIDP_REPORT_TYPE.Feature).Select(b => b.ReportId));
        if (c.FeatureLen >= 13 && featureIds.Contains(LegacyReportId)) return Family.Legacy;

        var outputIds = Hid.GetValueCaps(c.Path).Where(v => v.Type == Hid.HIDP_REPORT_TYPE.Output).Select(v => v.ReportId)
            .Concat(Hid.GetButtonReportIds(c.Path).Where(b => b.Type == Hid.HIDP_REPORT_TYPE.Output).Select(b => b.ReportId));
        if (c.InputLen == 65 && c.OutputLen == 65 && outputIds.Contains(NewReportId)) return Family.New;
        return null;
    }

    static DeviceBattery Make(HidCollection c, int level, bool? charging, string detail) => new()
    {
        Id = $"hid:{c.Vid:X4}:{c.Pid:X4}",
        Name = string.IsNullOrEmpty(c.Product) ? $"MCHOSE {c.Pid:X4}" : c.Product,
        Kind = DeviceKind.Mouse,
        Source = BatterySource.Wireless24G,
        Level = Math.Clamp(level, 0, 100),
        IsCharging = charging,
        Detail = detail,
        UpdatedAt = DateTime.Now,
    };

    public void Dispose()
    {
        lock (_lock)
            foreach (var r in _receivers.Values)
            {
                r.Stop = true;
                if (r.Family == Family.Legacy) r.Handle.Dispose();
            }
    }
}
