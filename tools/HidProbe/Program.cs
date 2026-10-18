using BattWatch.Native;

// Usage: HidProbe [listenSeconds] [vid ...]
// Lists HID collections, standard battery usages, and passively listens on vendor collections.
int listen = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 3;
var vids = args.Skip(1).Select(a => Convert.ToUInt16(a, 16)).ToHashSet();

var all = Hid.Enumerate();
Console.WriteLine($"HID collections: {all.Count}");
foreach (var c in all.OrderBy(c => c.Vid).ThenBy(c => c.Pid).ThenBy(c => c.UsagePage))
{
    if (vids.Count > 0 && !vids.Contains(c.Vid)) continue;
    Console.WriteLine(c);
    foreach (var v in Hid.GetValueCaps(c.Path))
        if (v.UsagePage is 0x06 or 0x84 or 0x85 || v.UsagePage >= 0xFF00)
            Console.WriteLine($"    cap {v.Type} id={v.ReportId} up={v.UsagePage:X4} u={v.Usage:X4} [{v.LogicalMin}..{v.LogicalMax}]");
    if (c.IsVendor)
        foreach (var b in Hid.GetButtonReportIds(c.Path))
            Console.WriteLine($"    btn {b.Type} id={b.ReportId} up={b.UsagePage:X4}");

    if (!c.IsVendor || listen <= 0) continue;
    using var h = Hid.OpenReadWrite(c.Path, overlapped: true);
    if (h.IsInvalid) { Console.WriteLine("    (cannot open rw)"); continue; }
    if (c.FeatureLen > 0)
    {
        foreach (byte id in new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 0x10, 0x11, 0x20, 0x51, 0xBA })
        {
            var buf = new byte[c.FeatureLen]; buf[0] = id;
            if (Hid.GetFeature(h, buf)) Console.WriteLine($"    feat[{id:X2}] {Convert.ToHexString(buf)}");
        }
    }
    if (c.InputLen > 0)
    {
        var until = DateTime.Now.AddSeconds(listen);
        int n = 0;
        while (DateTime.Now < until && n < 20)
        {
            var r = Hid.ReadReport(h, c.InputLen, (int)Math.Max(1, (until - DateTime.Now).TotalMilliseconds));
            if (r is null) break;
            Console.WriteLine($"    in  {Convert.ToHexString(r)}");
            n++;
        }
    }
}
