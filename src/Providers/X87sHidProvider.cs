using BattWatch.Core;
using BattWatch.Native;

namespace BattWatch.Providers;

/// <summary>
/// Rongyuan (RY) 2.4G keyboard receivers, VID 0x3151, vendor config collection usage page 0xFFFF
/// usage 0x0002 with a 64-byte feature report (report id 0). EWEADN (前行者) X87S ships with one
/// (receiver PID 0x504E); MonsGeek/Akko/Attack Shark dongles use the same firmware family.
///
/// GET_DONGLE_STATUS "F7" is answered by the receiver itself from its cache (nothing goes over RF),
/// and is what the official RY web driver sends to every dongle:
///   SetFeature 0: [0xF7, 0, 0, 0, 0, 0, 0, chk] with chk = 0xFF - sum(bytes 0..6) = 0x08
///                 (the "Bit7" checksum every RY command carries; without it the receiver ignores it)
///   GetFeature 0: [0] has cached reply, [1] keyboard battery %, [2] reserved, [3] keyboard charging,
///                 [4] always 1, [5] rf ready, [6] always 1, [7]/[8] boot flags.
/// Battery is 0 until the keyboard has reported since the receiver was plugged in.
/// Layout from the firmware reverse engineering in echtzeit-solutions/monsgeek-akko-linux
/// (docs/PROTOCOL.md, monsgeek-transport hid_dongle.rs) and the RY driver's own F7 calls.
/// </summary>
public sealed class X87sHidProvider : IBatteryProvider
{
    const ushort Vid = 0x3151;
    const ushort X87sReceiverPid = 0x504E;
    const ushort UsagePage = 0xFFFF;
    const ushort Usage = 0x0002;
    const byte StatusCmd = 0xF7;

    /// <summary>RY receivers known to answer F7; others are accepted when the product string says 2.4G.</summary>
    static readonly HashSet<ushort> DonglePids = [X87sReceiverPid, 0x5038, 0x503A, 0x503D, 0x502B, 0x5006];

    public string Name => "RY 2.4G (EWEADN X87S)";

    public Task<IReadOnlyList<DeviceBattery>> ReadAsync(CancellationToken ct)
    {
        var result = new List<DeviceBattery>();
        List<HidCollection> found;
        try
        {
            var all = Hid.Enumerate().Where(c => c.Vid == Vid).ToList();
            if (MchoseHidProvider.Verbose)
                foreach (var c in all) Log.Write($"  ry {c}");
            found = all.Where(c => c.UsagePage == UsagePage && c.Usage == Usage && c.FeatureLen >= 10 && IsDongle(c))
                .ToList();
        }
        catch
        {
            return Task.FromResult<IReadOnlyList<DeviceBattery>>(result);
        }

        foreach (var c in found)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var d = Query(c);
                if (d is not null) result.Add(d);
            }
            catch (Exception ex)
            {
                Log.Write($"RY {c.Pid:X4}: {ex.Message}");
            }
        }
        return Task.FromResult<IReadOnlyList<DeviceBattery>>(result);
    }

    static bool IsDongle(HidCollection c)
    {
        if (DonglePids.Contains(c.Pid)) return true;
        var p = c.Product.ToLowerInvariant();
        return p.Contains("2.4g") || p.Contains("wireless") || p.Contains("dongle") || p.Contains("receiver");
    }

    internal static byte[] BuildRequest(int length, byte cmd)
    {
        var req = new byte[length];
        req[1] = cmd;
        int sum = 0;
        for (int i = 1; i <= 7; i++) sum += req[i];
        req[8] = (byte)(0xFF - (sum & 0xFF));
        return req;
    }

    static DeviceBattery? Query(HidCollection c)
    {
        using var h = Hid.OpenReadWrite(c.Path);
        if (h.IsInvalid)
        {
            if (MchoseHidProvider.Verbose)
                Log.Write($"  ry {c.Pid:X4} open failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            return null;
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (!Hid.SetFeature(h, BuildRequest(c.FeatureLen, StatusCmd)))
            {
                if (MchoseHidProvider.Verbose)
                    Log.Write($"  ry F7 SetFeature failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
                return null;
            }
            Thread.Sleep(10 + attempt * 40);

            var resp = new byte[c.FeatureLen];
            if (!Hid.GetFeature(h, resp)) continue;
            var p = resp.AsSpan(1); // skip report id 0
            if (MchoseHidProvider.Verbose) Log.Write($"  ry {c.Pid:X4} F7 reply: {Convert.ToHexString(resp, 0, 12)}");

            int battery = p[1];
            if (battery is <= 0 or > 100) continue; // keyboard off or not reported yet
            return new DeviceBattery
            {
                Id = $"hid:{c.Vid:X4}:{c.Pid:X4}",
                Name = c.Pid == X87sReceiverPid ? "前行者 X87S"
                    : string.IsNullOrEmpty(c.Product) ? $"RY {c.Pid:X4}" : c.Product,
                Kind = DeviceKind.Keyboard,
                Source = BatterySource.Wireless24G,
                Level = battery,
                IsCharging = p[3] != 0,
                Detail = "接收器 · 0xF7",
                UpdatedAt = DateTime.Now,
            };
        }
        return null;
    }
}
