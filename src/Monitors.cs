using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace AutoMonitorBrightness;

/// <summary>A physical monitor controlled over DDC/CI (VCP code 0x10 = luminance).</summary>
public sealed class DisplayMonitor : IDisposable
{
    const byte VcpBrightness = 0x10;
    readonly IntPtr handle;
    bool disposed;

    public string Id { get; }
    public string Name { get; }
    public int MaxRaw { get; private set; } = 100;

    internal DisplayMonitor(IntPtr handle, string id, string name)
    {
        this.handle = handle;
        Id = id;
        Name = name;
    }

    public bool TryGetPercent(out int percent)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (Native.GetVCPFeatureAndVCPFeatureReply(handle, VcpBrightness, IntPtr.Zero, out uint cur, out uint max) && max > 0)
            {
                MaxRaw = (int)max;
                percent = (int)Math.Round(cur * 100.0 / max);
                return true;
            }
            if (Native.GetMonitorBrightness(handle, out uint min, out uint cur2, out uint max2) && max2 > min)
            {
                MaxRaw = (int)max2;
                percent = (int)Math.Round((cur2 - min) * 100.0 / (max2 - min));
                return true;
            }
            Thread.Sleep(80); // DDC/CI needs ~50 ms between commands
        }
        percent = -1;
        return false;
    }

    public bool TrySetPercent(int percent)
    {
        uint raw = (uint)Math.Round(Math.Clamp(percent, 0, 100) * MaxRaw / 100.0);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (Native.SetVCPFeature(handle, VcpBrightness, raw) || Native.SetMonitorBrightness(handle, raw))
            {
                Thread.Sleep(60);
                return true;
            }
            Thread.Sleep(120);
        }
        return false;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Native.DestroyPhysicalMonitor(handle);
    }
}

public static class MonitorManager
{
    /// <summary>Opens every physical monitor. Caller must dispose them. Handles are re-opened on each use
    /// so a monitor that was unplugged, slept or re-enumerated never leaves a stale handle behind.</summary>
    public static List<DisplayMonitor> OpenAll()
    {
        var hmons = new List<IntPtr>();
        Native.MonitorEnumProc proc = (h, _, _, _) => { hmons.Add(h); return true; };
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        GC.KeepAlive(proc);

        var result = new List<DisplayMonitor>();
        var seen = new Dictionary<string, int>();
        foreach (var hmon in hmons)
        {
            var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            Native.GetMonitorInfo(hmon, ref mi);
            var identities = DescribeAttached(mi.szDevice);

            if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(hmon, out uint n) || n == 0) continue;
            var arr = new Native.PHYSICAL_MONITOR[n];
            if (!Native.GetPhysicalMonitorsFromHMONITOR(hmon, n, arr)) continue;

            for (int i = 0; i < n; i++)
            {
                var (id, name) = i < identities.Count ? identities[i] : (null, null);
                name ??= arr[i].szPhysicalMonitorDescription;
                id ??= $"{mi.szDevice}#{i}";
                if (seen.TryGetValue(id, out int c)) { seen[id] = c + 1; id += "#" + (c + 1); }
                else seen[id] = 0;
                result.Add(new DisplayMonitor(arr[i].hPhysicalMonitor, id, name));
            }
        }
        return result;
    }

    static List<(string? id, string? name)> DescribeAttached(string adapterDevice)
    {
        var list = new List<(string?, string?)>();
        if (string.IsNullOrEmpty(adapterDevice)) return list;
        for (uint j = 0; j < 16; j++)
        {
            var dd = new Native.DISPLAY_DEVICE { cb = Marshal.SizeOf<Native.DISPLAY_DEVICE>() };
            if (!Native.EnumDisplayDevices(adapterDevice, j, ref dd, Native.EDD_GET_DEVICE_INTERFACE_NAME)) break;
            if ((dd.StateFlags & Native.DISPLAY_DEVICE_ACTIVE) == 0) continue;
            list.Add(ReadEdid(dd.DeviceID));
        }
        return list;
    }

    /// <summary>Resolves a stable id and the model name ("DELL U2419H") from the monitor's EDID in the registry.</summary>
    static (string? id, string? name) ReadEdid(string interfacePath)
    {
        try
        {
            // \\?\DISPLAY#DELA0F5#5&2a4b1c&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}
            var p = interfacePath.StartsWith(@"\\?\") ? interfacePath[4..] : interfacePath;
            var parts = p.Split('#');
            if (parts.Length < 3) return (null, null);
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Enum\{parts[0]}\{parts[1]}\{parts[2]}\Device Parameters");
            if (key?.GetValue("EDID") is not byte[] edid || edid.Length < 128)
                return ($"{parts[1]}#{parts[2]}", null);

            string? name = null, serial = null;
            for (int off = 54; off <= 108; off += 18)
            {
                if (edid[off] != 0 || edid[off + 1] != 0 || edid[off + 2] != 0) continue;
                string text = Encoding.ASCII.GetString(edid, off + 5, 13).Split('\n')[0].Trim();
                if (edid[off + 3] == 0xFC) name = text;
                else if (edid[off + 3] == 0xFF) serial = text;
            }
            if (serial == null)
            {
                uint sn = BitConverter.ToUInt32(edid, 12);
                if (sn != 0) serial = sn.ToString();
            }
            string id = (name ?? parts[1]) + "#" + (serial ?? parts[2]);
            return (id, name);
        }
        catch
        {
            return (null, null);
        }
    }
}
