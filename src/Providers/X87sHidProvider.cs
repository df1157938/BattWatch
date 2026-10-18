using BattWatch.Core;
using BattWatch.Native;

namespace BattWatch.Providers;

/// <summary>
/// EWEADN (前行者) X87S 2.4G receiver (VID 0x3151 PID 0x504E), vendor collection usage page 0xFFFF
/// usage 0x0002 with a 64-byte feature report (report id 0). The keyboard is built on a Rongyuan
/// (RY) controller; the protocol below comes from EWEADN's official E HUB 2.0 web driver
/// (hub.eweadn.cn, device table deviceJson/ry.json → ./ry/web/), class for "dongle" devices.
///
/// Status query "F7", which the official driver polls every 2 s while a receiver is connected:
///   SetFeature 0: [0xF7, 0…] (zero-padded to 64 bytes, no checksum)
///   GetFeature 0: [0] canRead, [1] keyboard battery %, [2] mouse battery %,
///                 [3] keyboard link (0 = online), [4] mouse link (0 = online), [5] canSend,
///                 [6] paired devices (bit0 keyboard, bit1 mouse), [7]/[8] RF boot flags.
/// Nothing else is sent; the driver's F6/FC/FE commands route config traffic and are not used here.
/// </summary>
public sealed class X87sHidProvider : IBatteryProvider
{
    const ushort Vid = 0x3151;
    const ushort ReceiverPid = 0x504E;
    const ushort UsagePage = 0xFFFF;
    const ushort Usage = 0x0002;
    const byte StatusCmd = 0xF7;

    public string Name => "EWEADN X87S 2.4G";

    public Task<IReadOnlyList<DeviceBattery>> ReadAsync(CancellationToken ct)
    {
        var result = new List<DeviceBattery>();
        List<HidCollection> found;
        try
        {
            found = Hid.Enumerate()
                .Where(c => c.Vid == Vid && c.Pid == ReceiverPid && c.UsagePage == UsagePage && c.Usage == Usage
                            && c.FeatureLen >= 10)
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
                Log.Write($"X87S {c.Pid:X4}: {ex.Message}");
            }
        }
        return Task.FromResult<IReadOnlyList<DeviceBattery>>(result);
    }

    static DeviceBattery? Query(HidCollection c)
    {
        using var h = Hid.OpenReadWrite(c.Path);
        if (h.IsInvalid) return null;

        // The driver waits 10 ms around each transfer; give the receiver a few tries to settle.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var req = new byte[c.FeatureLen];
            req[1] = StatusCmd;
            if (!Hid.SetFeature(h, req))
            {
                if (MchoseHidProvider.Verbose)
                    Log.Write($"  x87s SetFeature failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
                return null;
            }
            Thread.Sleep(10 + attempt * 40);

            var resp = new byte[c.FeatureLen];
            if (!Hid.GetFeature(h, resp)) continue;
            var p = resp.AsSpan(1); // skip report id 0
            if (MchoseHidProvider.Verbose) Log.Write($"  x87s F7 reply: {Convert.ToHexString(resp, 0, 12)}");

            bool keyboardPaired = (p[6] & 0x01) != 0;
            if (!keyboardPaired) continue;
            if (p[3] != 0) return null;           // keyboard off / asleep
            int battery = p[1];
            if (battery is <= 0 or > 100) continue;
            return new DeviceBattery
            {
                Id = $"hid:{c.Vid:X4}:{c.Pid:X4}",
                Name = string.IsNullOrEmpty(c.Product) ? "X87S" : c.Product,
                Kind = DeviceKind.Keyboard,
                Source = BatterySource.Wireless24G,
                Level = battery,
                Detail = "接收器 · 0xF7",
                UpdatedAt = DateTime.Now,
            };
        }
        return null;
    }
}
