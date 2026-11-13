using BattWatch.Core;
using BattWatch.Native;

namespace BattWatch.Providers;

/// <summary>
/// Flydigi (飞智) gamepads in DInput mode, wired or through the 2.4G dongle: VID 0x04B4 PID 0x2412
/// (Vader 4 Pro / 黑武士4 Pro, also Vader 3 Pro and Apex 4), vendor collection usage page 0xFFA0
/// on HID interface 2. That collection streams the pad state as input report 0x04 ("04 FE …") and
/// takes commands as output report 0x05.
///
/// "Get device info" is the read-only query Space Station and SDL send on every connect:
///   Output 0x05: [0x05, 0xEC, 0…]
///   Input  0x04: not "04 FE", [15] = 0xEC, [3] device id (85 = Vader 4 Pro), [5..8] MAC,
///                [9]/[10] firmware nibbles, [11] battery, [12] cpu, [13] 1 = wired / 0 = dongle.
/// The dongle drops roughly every other request and reports battery 0 for a few seconds after the
/// pad powers on, so the query is retried and 0 is treated as "not ready".
///
/// The battery byte, as Space Station 4.2 decodes it (HeartBeatCommandFactory): high nibble
/// 1 = charging (SDL also knows 2 = full), low nibble 0…5 bars. Space Station 3 read older
/// firmware as a raw 98…114 value mapped linearly to 0…100 %, which is accepted as a fallback.
///
/// In XInput mode the dongle enumerates as 045E:028E and this collection does not exist;
/// see <see cref="FlydigiServiceProvider"/> for that case.
///
/// Sources: SDL src/joystick/hidapi/SDL_hidapi_flydigi.c (V1 protocol), pipe01/flydigictl,
/// JochemKuipers/flydigi-spacestation-linux docs/protocol.md,
/// uiltonlopes/flydigi-space-station-mac docs/protocol.md.
/// </summary>
public sealed class FlydigiHidProvider : IBatteryProvider
{
    const ushort Vid = 0x04B4;
    const ushort Pid = 0x2412;
    const ushort UsagePage = 0xFFA0;
    const byte CmdReportId = 0x05;
    const byte InputReportId = 0x04;
    const byte StateMagic = 0xFE;
    const byte GetInfoCmd = 0xEC;
    const int MinCommandLength = 12; // shorter reports are silently ignored by the firmware

    public string Name => "Flydigi DInput";

    public Task<IReadOnlyList<DeviceBattery>> ReadAsync(CancellationToken ct)
    {
        var result = new List<DeviceBattery>();
        List<HidCollection> found;
        try
        {
            var everything = Hid.Enumerate();
            var all = everything.Where(c => c.Vid == Vid && c.Pid == Pid).ToList();
            if (MchoseHidProvider.Verbose)
            {
                foreach (var c in all) Log.Write($"  flydigi {c}");
                // XInput mode shows up as an Xbox 360 pad; logged so a dump tells the two cases apart.
                foreach (var c in everything.Where(c => c.Vid == 0x045E && c.Pid == 0x028E))
                    Log.Write($"  xbox360 pad (Flydigi in XInput mode?) {c}");
            }
            found = all.Where(c => c.UsagePage == UsagePage && c.OutputLen >= MinCommandLength && c.InputLen > 15)
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
                var d = Query(c, ct);
                if (d is not null) result.Add(d);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Write($"Flydigi {c.Pid:X4}: {ex.Message}");
            }
        }
        return Task.FromResult<IReadOnlyList<DeviceBattery>>(result);
    }

    static DeviceBattery? Query(HidCollection c, CancellationToken ct)
    {
        using var h = Hid.OpenReadWrite(c.Path, overlapped: true);
        if (h.IsInvalid)
        {
            if (MchoseHidProvider.Verbose)
                Log.Write($"  flydigi open failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            return null;
        }

        var req = new byte[c.OutputLen];
        req[0] = CmdReportId;
        req[1] = GetInfoCmd;

        for (int attempt = 0; attempt < 4; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (!Hid.WriteReport(h, req) && MchoseHidProvider.Verbose)
                Log.Write($"  flydigi EC write failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");

            // The reply is interleaved with the pad's state stream (up to 1000 reports/s).
            var until = DateTime.UtcNow.AddMilliseconds(250);
            while (DateTime.UtcNow < until)
            {
                var r = Hid.ReadReport(h, c.InputLen, 100);
                if (r is null) break;
                if (!IsInfoReply(r)) continue;
                if (MchoseHidProvider.Verbose) Log.Write($"  flydigi EC reply: {Convert.ToHexString(r, 0, 16)}");

                var level = DecodeBattery(r[11]);
                if (level is null)
                {
                    if (r[11] != 0) Log.Write($"Flydigi: unknown battery byte 0x{r[11]:X2}");
                    break; // not reported yet; ask again
                }
                bool wired = r[13] == 1;
                return new DeviceBattery
                {
                    Id = DeviceId(Convert.ToHexString([r[8], r[7], r[6], r[5]])),
                    Name = DeviceName(r[3], c.Product),
                    Kind = DeviceKind.Gamepad,
                    Source = wired ? BatterySource.Usb : BatterySource.Wireless24G,
                    Level = level.Value.Percent,
                    IsCharging = level.Value.Charging,
                    Detail = (wired ? "有线" : "接收器") + " · 0xEC" + (level.Value.Bars ? " · 5 格" : ""),
                    UpdatedAt = DateTime.Now,
                };
            }
            Thread.Sleep(100 + attempt * 200);
        }
        return null;
    }

    internal static bool IsInfoReply(ReadOnlySpan<byte> r) =>
        r.Length > 15 && r[0] == InputReportId && r[1] != StateMagic && r[15] == GetInfoCmd;

    /// <summary>Null when the byte is 0 (pad has not reported yet) or in neither known encoding.</summary>
    internal static (int Percent, bool Charging, bool Bars)? DecodeBattery(byte raw)
    {
        if (raw == 0) return null;
        int status = raw >> 4, bars = raw & 0x0F;
        if (status <= 2 && bars <= 5)
            return (status == 2 ? 100 : bars * 20, status != 0, true);
        if (raw is >= 90 and <= 125)
            return ((Math.Clamp((int)raw, 98, 114) - 98) * 100 / 16, false, false);
        return null;
    }

    internal const string Vader4ProName = "飞智黑武士4 Pro";

    /// <summary>Same id whichever way the pad is read; mac as "AABBCCDD" or "AA:BB:CC:DD".</summary>
    internal static string DeviceId(string mac) => "flydigi:" + mac.Replace(":", "").ToUpperInvariant();

    static string DeviceName(byte deviceId, string product) => deviceId switch
    {
        85 => Vader4ProName,
        _ => string.IsNullOrEmpty(product) ? $"飞智手柄 #{deviceId}" : product,
    };
}
