using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BattWatch.Native;

/// <summary>One HID top-level collection (one \\?\hid#... interface path).</summary>
public sealed record HidCollection(
    string Path, ushort Vid, ushort Pid, ushort Version,
    ushort UsagePage, ushort Usage,
    int InputLen, int OutputLen, int FeatureLen,
    string Product, string Manufacturer)
{
    public bool IsVendor => UsagePage >= 0xFF00;
    public override string ToString() =>
        $"{Vid:X4}:{Pid:X4} up={UsagePage:X4} u={Usage:X4} in={InputLen} out={OutputLen} feat={FeatureLen} \"{Product}\"";
}

public static unsafe partial class Hid
{
    // ---------- enumeration ----------

    public static List<HidCollection> Enumerate()
    {
        var result = new List<HidCollection>();
        HidD_GetHidGuid(out var hidGuid);
        var set = SetupDiGetClassDevsW(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1)) return result;
        try
        {
            var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref ifData); i++)
            {
                var path = GetInterfacePath(set, ref ifData);
                if (path is null) continue;
                var info = Describe(path);
                if (info is not null) result.Add(info);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return result;
    }

    static string? GetInterfacePath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA ifData)
    {
        SetupDiGetDeviceInterfaceDetailW(set, ref ifData, IntPtr.Zero, 0, out var size, IntPtr.Zero);
        if (size == 0) return null;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W: 8 on x64, 6 on x86
            Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetailW(set, ref ifData, buf, size, out _, IntPtr.Zero)) return null;
            return Marshal.PtrToStringUni(buf + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>Opens with zero access (works even for system-owned keyboards/mice) to read metadata.</summary>
    public static HidCollection? Describe(string path)
    {
        using var h = Open(path, 0);
        if (h.IsInvalid) return null;
        var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        if (!HidD_GetAttributes(h, ref attr)) return null;
        if (!HidD_GetPreparsedData(h, out var pp)) return null;
        try
        {
            if (HidP_GetCaps(pp, out var caps) != HIDP_STATUS_SUCCESS) return null;
            return new HidCollection(path, attr.VendorID, attr.ProductID, attr.VersionNumber,
                caps.UsagePage, caps.Usage,
                caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength,
                GetString(h, HidD_GetProductString), GetString(h, HidD_GetManufacturerString));
        }
        finally
        {
            HidD_FreePreparsedData(pp);
        }
    }

    delegate bool StringGetter(SafeFileHandle h, char* buf, int len);

    static string GetString(SafeFileHandle h, StringGetter getter)
    {
        char* buf = stackalloc char[128];
        return getter(h, buf, 256) ? new string(buf).Trim() : "";
    }

    // ---------- value caps (for standard battery usages) ----------

    public readonly record struct ValueCap(HIDP_REPORT_TYPE Type, byte ReportId, ushort UsagePage, ushort Usage,
        int LogicalMin, int LogicalMax);

    public static List<ValueCap> GetValueCaps(string path)
    {
        var list = new List<ValueCap>();
        using var h = Open(path, 0);
        if (h.IsInvalid || !HidD_GetPreparsedData(h, out var pp)) return list;
        try
        {
            if (HidP_GetCaps(pp, out var caps) != HIDP_STATUS_SUCCESS) return list;
            foreach (var (type, count) in new[]
            {
                (HIDP_REPORT_TYPE.Input, caps.NumberInputValueCaps),
                (HIDP_REPORT_TYPE.Output, caps.NumberOutputValueCaps),
                (HIDP_REPORT_TYPE.Feature, caps.NumberFeatureValueCaps),
            })
            {
                if (count == 0) continue;
                var arr = new HIDP_VALUE_CAPS[count];
                ushort n = count;
                fixed (HIDP_VALUE_CAPS* p = arr)
                    if (HidP_GetValueCaps(type, p, ref n, pp) != HIDP_STATUS_SUCCESS) continue;
                for (int i = 0; i < n; i++)
                {
                    var c = arr[i];
                    list.Add(new ValueCap(type, c.ReportID, c.UsagePage, c.UsageMin, c.LogicalMin, c.LogicalMax));
                }
            }
        }
        finally
        {
            HidD_FreePreparsedData(pp);
        }
        return list;
    }

    /// <summary>Report ids declared by button (array) caps, per report type.</summary>
    public static List<(HIDP_REPORT_TYPE Type, byte ReportId, ushort UsagePage)> GetButtonReportIds(string path)
    {
        var list = new List<(HIDP_REPORT_TYPE, byte, ushort)>();
        using var h = Open(path, 0);
        if (h.IsInvalid || !HidD_GetPreparsedData(h, out var pp)) return list;
        try
        {
            if (HidP_GetCaps(pp, out var caps) != HIDP_STATUS_SUCCESS) return list;
            foreach (var (type, count) in new[]
            {
                (HIDP_REPORT_TYPE.Input, caps.NumberInputButtonCaps),
                (HIDP_REPORT_TYPE.Output, caps.NumberOutputButtonCaps),
                (HIDP_REPORT_TYPE.Feature, caps.NumberFeatureButtonCaps),
            })
            {
                if (count == 0) continue;
                var arr = new HIDP_VALUE_CAPS[count]; // HIDP_BUTTON_CAPS has the same 72-byte size and header
                ushort n = count;
                fixed (HIDP_VALUE_CAPS* p = arr)
                    if (HidP_GetButtonCaps(type, p, ref n, pp) != HIDP_STATUS_SUCCESS) continue;
                for (int i = 0; i < n; i++) list.Add((type, arr[i].ReportID, arr[i].UsagePage));
            }
        }
        finally
        {
            HidD_FreePreparsedData(pp);
        }
        return list;
    }

    /// <summary>Reads a scaled usage value out of a report buffer using the collection's preparsed data.</summary>
    public static int? GetUsageValue(string path, HIDP_REPORT_TYPE type, ushort usagePage, ushort usage, byte[] report)
    {
        using var h = Open(path, 0);
        if (h.IsInvalid || !HidD_GetPreparsedData(h, out var pp)) return null;
        try
        {
            fixed (byte* r = report)
                return HidP_GetUsageValue(type, usagePage, 0, usage, out var v, pp, r, (uint)report.Length) == HIDP_STATUS_SUCCESS
                    ? (int)v : null;
        }
        finally
        {
            HidD_FreePreparsedData(pp);
        }
    }

    // ---------- I/O ----------

    public static SafeFileHandle Open(string path, uint access, bool overlapped = false) =>
        CreateFileW(path, access, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
            overlapped ? FILE_FLAG_OVERLAPPED : 0, IntPtr.Zero);

    public static SafeFileHandle OpenReadWrite(string path, bool overlapped = false) =>
        Open(path, GENERIC_READ | GENERIC_WRITE, overlapped);

    public static bool GetFeature(SafeFileHandle h, byte[] buf)
    {
        fixed (byte* p = buf) return HidD_GetFeature(h, p, buf.Length);
    }

    public static bool SetFeature(SafeFileHandle h, byte[] buf)
    {
        fixed (byte* p = buf) return HidD_SetFeature(h, p, buf.Length);
    }

    public static bool GetInputReport(SafeFileHandle h, byte[] buf)
    {
        fixed (byte* p = buf) return HidD_GetInputReport(h, p, buf.Length);
    }

    public static bool SetOutputReport(SafeFileHandle h, byte[] buf)
    {
        fixed (byte* p = buf) return HidD_SetOutputReport(h, p, buf.Length);
    }

    /// <summary>
    /// Reads one interrupt input report with a timeout. Needs a handle opened with overlapped=true.
    /// Returns null on timeout.
    /// </summary>
    public static byte[]? ReadReport(SafeFileHandle h, int length, int timeoutMs)
    {
        var buf = new byte[length];
        using var evt = new ManualResetEvent(false);
        var ov = new NativeOverlapped { EventHandle = evt.SafeWaitHandle.DangerousGetHandle() };
        fixed (byte* p = buf)
        {
            if (!ReadFile(h, p, (uint)length, out var read, &ov))
            {
                int err = Marshal.GetLastWin32Error();
                if (err != ERROR_IO_PENDING) throw new Win32Exception(err);
                if (!evt.WaitOne(timeoutMs))
                {
                    CancelIoEx(h, &ov);
                    GetOverlappedResult(h, &ov, out _, true);
                    return null;
                }
                if (!GetOverlappedResult(h, &ov, out read, false)) return null;
            }
            return read == length ? buf : buf[..(int)read];
        }
    }

    /// <summary>Writes one interrupt output report (buf[0] = report id). Needs an overlapped handle.</summary>
    public static bool WriteReport(SafeFileHandle h, byte[] buf, int timeoutMs = 1000)
    {
        using var evt = new ManualResetEvent(false);
        var ov = new NativeOverlapped { EventHandle = evt.SafeWaitHandle.DangerousGetHandle() };
        fixed (byte* p = buf)
        {
            if (WriteFile(h, p, (uint)buf.Length, out _, &ov)) return true;
            if (Marshal.GetLastWin32Error() != ERROR_IO_PENDING) return false;
            if (!evt.WaitOne(timeoutMs))
            {
                CancelIoEx(h, &ov);
                GetOverlappedResult(h, &ov, out _, true);
                return false;
            }
            return GetOverlappedResult(h, &ov, out _, false);
        }
    }

    // ---------- P/Invoke ----------

    const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
    const int ERROR_IO_PENDING = 997;
    const int HIDP_STATUS_SUCCESS = 0x00110000;

    public enum HIDP_REPORT_TYPE { Input = 0, Output = 1, Feature = 2 }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        public fixed ushort Reserved[17];
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    struct HIDP_VALUE_CAPS
    {
        [FieldOffset(0)] public ushort UsagePage;
        [FieldOffset(2)] public byte ReportID;
        [FieldOffset(3)] public byte IsAlias;
        [FieldOffset(12)] public byte IsRange;
        [FieldOffset(40)] public int LogicalMin;
        [FieldOffset(44)] public int LogicalMax;
        [FieldOffset(56)] public ushort UsageMin; // Range.UsageMin / NotRange.Usage
    }

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);
    [DllImport("hid.dll")] static extern int HidP_GetValueCaps(HIDP_REPORT_TYPE t, HIDP_VALUE_CAPS* caps, ref ushort len, IntPtr pp);
    [DllImport("hid.dll")] static extern int HidP_GetButtonCaps(HIDP_REPORT_TYPE t, HIDP_VALUE_CAPS* caps, ref ushort len, IntPtr pp);
    [DllImport("hid.dll")] static extern int HidP_GetUsageValue(HIDP_REPORT_TYPE t, ushort page, ushort link, ushort usage, out uint value, IntPtr pp, byte* report, uint len);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetProductString(SafeFileHandle h, char* buf, int len);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetManufacturerString(SafeFileHandle h, char* buf, int len);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetFeature(SafeFileHandle h, byte* buf, int len);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_SetFeature(SafeFileHandle h, byte* buf, int len);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetInputReport(SafeFileHandle h, byte* buf, int len);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_SetOutputReport(SafeFileHandle h, byte* buf, int len);

    [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevsW(ref Guid g, IntPtr e, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, uint idx, ref SP_DEVICE_INTERFACE_DATA d);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, uint size, out uint req, IntPtr info);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadFile(SafeFileHandle h, byte* buf, uint len, out uint read, NativeOverlapped* ov);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(SafeFileHandle h, byte* buf, uint len, out uint written, NativeOverlapped* ov);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetOverlappedResult(SafeFileHandle h, NativeOverlapped* ov, out uint n, bool wait);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CancelIoEx(SafeFileHandle h, NativeOverlapped* ov);
}
