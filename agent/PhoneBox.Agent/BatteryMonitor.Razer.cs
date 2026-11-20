using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PhoneBox.Agent;

public sealed partial class BatteryMonitor
{
    internal const int RazerVendorId = 0x1532;
    internal const int RazerReportLength = 91; // 1-byte HID report ID (0x00) + 90-byte Razer frame
    internal const int RazerPaReportLength = 64;
    internal const byte RazerClassPower = 0x07;
    internal const byte RazerCmdBattery = 0x80;
    internal const byte RazerCmdCharging = 0x84;
    internal const byte RazerStatusNew = 0x00;
    internal const byte RazerStatusBusy = 0x01;
    internal const byte RazerStatusOk = 0x02;
    internal const byte RazerStatusTimeout = 0x04;
    internal const byte RazerStatusNotSupported = 0x05;

    // Overall budget for a Razer scan; the last in-flight HID call started just before the deadline
    // can still overrun by up to ~400 ms, which is fine because WindowsRazer runs on its own task.
    private const long RazerTotalBudgetMs = 1500;

    internal const byte RazerPaReportId = 0x02;
    internal const byte RazerBarracudaReportId = 0x01;
    internal const int RazerBarracudaPid = 0x053A;
    internal const byte RazerPaCmdBattery = 0x21;
    internal const byte RazerPaCmdCharging = 0x2A;

    internal static readonly byte[] RazerTxCandidates = [0x1F, 0x3F, 0xFF];
    // These PA receivers use different frame offsets; both are verified from real hardware captures.
    private static readonly HashSet<int> RazerPaPids = [0x0555, RazerBarracudaPid];
    private static readonly ConcurrentDictionary<int, byte> PreferredRazerTxByPid = new();

    internal static IEnumerable<int> RazerFeaturePids => RazerDevices.Keys.Where(pid => !RazerPaPids.Contains(pid));

    private static readonly Dictionary<int, (string Name, string Kind)> RazerDevices = new()
    {
        // Mice (PIDs from OpenRazer razermouse_driver.h)
        [0x0045] = ("Razer Mamba Wireless", "mouse"),
        [0x006F] = ("Razer Lancehead Wireless", "mouse"),
        [0x0072] = ("Razer Mamba Wireless", "mouse"),
        [0x0077] = ("Razer Pro Click", "mouse"),
        [0x007A] = ("Razer Viper Ultimate", "mouse"),
        [0x007B] = ("Razer Viper Ultimate", "mouse"),
        [0x007C] = ("Razer DeathAdder V2 Pro", "mouse"),
        [0x007D] = ("Razer DeathAdder V2 Pro", "mouse"),
        [0x0083] = ("Razer Basilisk X HyperSpeed", "mouse"),
        [0x0086] = ("Razer Basilisk Ultimate", "mouse"),
        [0x0088] = ("Razer Basilisk Ultimate", "mouse"),
        [0x008F] = ("Razer Naga Pro", "mouse"),
        [0x0090] = ("Razer Naga Pro", "mouse"),
        [0x0094] = ("Razer Orochi V2", "mouse"),
        [0x009A] = ("Razer Pro Click Mini", "mouse"),
        [0x009C] = ("Razer DeathAdder V2 X HyperSpeed", "mouse"),
        [0x009E] = ("Razer Viper Mini SE", "mouse"),
        [0x009F] = ("Razer Viper Mini SE", "mouse"),
        [0x00A5] = ("Razer Viper V2 Pro", "mouse"),
        [0x00A6] = ("Razer Viper V2 Pro", "mouse"),
        [0x00A7] = ("Razer Naga V2 Pro", "mouse"),
        [0x00A8] = ("Razer Naga V2 Pro", "mouse"),
        [0x00AA] = ("Razer Basilisk V3 Pro", "mouse"),
        [0x00AB] = ("Razer Basilisk V3 Pro", "mouse"),
        [0x00AF] = ("Razer Cobra Pro", "mouse"),
        [0x00B0] = ("Razer Cobra Pro", "mouse"),
        [0x00B4] = ("Razer Naga V2 HyperSpeed", "mouse"),
        [0x00B6] = ("Razer DeathAdder V3 Pro", "mouse"),
        [0x00B7] = ("Razer DeathAdder V3 Pro", "mouse"),
        [0x00B8] = ("Razer Viper V3 HyperSpeed", "mouse"),
        [0x00B9] = ("Razer Basilisk V3 X HyperSpeed", "mouse"),
        [0x00BE] = ("Razer DeathAdder V4 Pro", "mouse"),
        [0x00BF] = ("Razer DeathAdder V4 Pro", "mouse"),
        [0x00C0] = ("Razer Viper V3 Pro", "mouse"),
        [0x00C1] = ("Razer Viper V3 Pro", "mouse"),
        [0x00C2] = ("Razer DeathAdder V3 Pro", "mouse"),
        [0x00C3] = ("Razer DeathAdder V3 Pro", "mouse"),
        [0x00C4] = ("Razer DeathAdder V3 HyperSpeed", "mouse"),
        [0x00C5] = ("Razer DeathAdder V3 HyperSpeed", "mouse"),
        [0x00CC] = ("Razer Basilisk V3 Pro 35K", "mouse"),
        [0x00CD] = ("Razer Basilisk V3 Pro 35K", "mouse"),
        [0x00D0] = ("Razer Pro Click V2", "mouse"),
        [0x00D1] = ("Razer Pro Click V2", "mouse"),
        [0x00DB] = ("Razer Cobra HyperSpeed", "mouse"),
        [0x00DE] = ("Razer Viper V3 Pro SE", "mouse"),
        [0x00DF] = ("Razer Viper V3 Pro SE", "mouse"),
        [0x00E5] = ("Razer Viper V4 Pro", "mouse"),
        [0x00E6] = ("Razer Viper V4 Pro", "mouse"),
        // Keyboards (PIDs from OpenRazer razerkbd_driver.h)
        [0x0258] = ("Razer BlackWidow V3 Mini HyperSpeed", "keyboard"),
        [0x025A] = ("Razer BlackWidow V3 Pro", "keyboard"),
        [0x025C] = ("Razer BlackWidow V3 Pro", "keyboard"),
        [0x0271] = ("Razer BlackWidow V3 Mini HyperSpeed", "keyboard"),
        [0x0290] = ("Razer DeathStalker V2 Pro", "keyboard"),
        [0x0292] = ("Razer DeathStalker V2 Pro", "keyboard"),
        [0x0296] = ("Razer DeathStalker V2 Pro TKL", "keyboard"),
        [0x0298] = ("Razer DeathStalker V2 Pro TKL", "keyboard"),
        [0x02B9] = ("Razer BlackWidow V4 Mini HyperSpeed", "keyboard"),
        [0x02BA] = ("Razer BlackWidow V4 Mini HyperSpeed", "keyboard"),
        [0x02D5] = ("Razer BlackWidow V4 TKL HyperSpeed", "keyboard"),
        [0x02D7] = ("Razer BlackWidow V4 TKL HyperSpeed", "keyboard"),
        // Headsets (PA protocol)
        [RazerBarracudaPid] = ("Razer Barracuda Pro (2.4 GHz)", "headphones"),
        [0x0555] = ("Razer BlackShark V2 Pro", "headphones"),
    };

    /// <summary>Builds a 91-byte HID Feature report querying a Razer power property (0x07 / 0x80 or 0x84).</summary>
    internal static byte[] BuildRazerReport(byte txId, byte commandId)
    {
        var buf = new byte[RazerReportLength];
        buf[0] = 0x00;
        buf[1] = RazerStatusNew;
        buf[2] = txId;
        buf[6] = 0x02;
        buf[7] = RazerClassPower;
        buf[8] = commandId;
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= buf[i];
        buf[89] = crc;
        return buf;
    }

    /// <summary>Extracts battery percentage (1-100) from a 91-byte 0x07/0x80 response, or null when asleep/invalid.</summary>
    internal static int? ParseRazerBattery(ReadOnlySpan<byte> report)
    {
        if (report.Length < RazerReportLength) return null;
        if (report[1] != RazerStatusOk || report[7] != RazerClassPower || report[8] != RazerCmdBattery) return null;
        byte raw = report[10];
        // Wireless dongles report 0 when the peripheral is asleep or out of range.
        if (raw == 0) return null;
        return Math.Clamp((int)Math.Round(raw * 100.0 / 255.0), 0, 100);
    }

    /// <summary>Extracts charging state from a 91-byte 0x07/0x84 response, or null when invalid.</summary>
    internal static bool? ParseRazerCharging(ReadOnlySpan<byte> report)
    {
        if (report.Length < RazerReportLength) return null;
        if (report[1] != RazerStatusOk || report[7] != RazerClassPower || report[8] != RazerCmdCharging) return null;
        return report[10] != 0;
    }

    /// <summary>
    /// Builds the 64-byte "PA" remote/telemetry mode frame (command 0xE1: 1 = on, 0 = off).
    /// Decoded from Razer Synapse USBPcap captures in OpenRazer (razerblackshark_driver, PR #2862) and
    /// HeyOkay/HaloBattery (providers/blackshark.py): the BlackShark V2 Pro 2023 (0x0555) 2.4 GHz receiver
    /// dozes after ~300 ms idle and ignores 0x21/0x2A queries unless woken and placed into remote mode first.
    /// </summary>
    internal static byte[] BuildRazerPaRemoteFrame(bool on)
    {
        var buf = new byte[RazerPaReportLength];
        buf[0] = RazerPaReportId;
        buf[1] = 0x80;
        buf[2] = 0x07;
        buf[5] = (byte)'P';
        buf[6] = (byte)'A';
        buf[7] = 0x0E;
        buf[9] = 0x02;
        buf[10] = 0xE1;
        buf[11] = (byte)(on ? 1 : 0);
        return buf;
    }

    internal static byte[] BuildRazerPaQueryFrame(byte cmdId)
    {
        var buf = new byte[RazerPaReportLength];
        buf[0] = RazerPaReportId;
        buf[1] = 0x80;
        buf[2] = 0x08;
        buf[5] = (byte)'P';
        buf[6] = (byte)'A';
        buf[7] = 0x08;
        buf[9] = 0x03;
        buf[10] = cmdId;
        return buf;
    }

    internal static byte? ParseRazerPaReply(ReadOnlySpan<byte> report, byte cmdId)
    {
        if (report.Length >= 16 && report[0] == RazerPaReportId && report[12] == cmdId && report[13] == 0x01)
            return report[15];
        return null;
    }

    /// <summary>
    /// Barracuda Pro PA frames, decoded from a Synapse USBPcap capture and independently documented by
    /// HeyOkay/HaloBattery (providers/barracuda.py). Its request and reply fields are two bytes earlier than
    /// the BlackShark layout, so it deliberately has separate builders and parsing.
    /// </summary>
    internal static byte[] BuildRazerBarracudaRemoteFrame(bool on)
    {
        var buf = new byte[RazerPaReportLength];
        buf[0] = RazerBarracudaReportId; buf[1] = 0x80; buf[2] = 0x07;
        buf[3] = (byte)'P'; buf[4] = (byte)'A'; buf[5] = 0x0E; buf[6] = 0x08;
        buf[7] = 0x02; buf[8] = 0xE1; buf[9] = (byte)(on ? 1 : 0);
        return buf;
    }

    internal static byte[] BuildRazerBarracudaQueryFrame(byte cmdId)
    {
        var buf = new byte[RazerPaReportLength];
        buf[0] = RazerBarracudaReportId; buf[1] = 0x80; buf[2] = 0x08;
        buf[3] = (byte)'P'; buf[4] = (byte)'A'; buf[5] = 0x08; buf[6] = 0x08;
        buf[7] = 0x03; buf[8] = cmdId;
        return buf;
    }

    internal static byte? ParseRazerBarracudaReply(ReadOnlySpan<byte> report, byte cmdId)
    {
        foreach (var offset in new[] { 0, -1 })
        {
            if (offset == -1 && report.Length > 0 && report[0] == RazerBarracudaReportId) continue;
            var marker = 3 + offset;
            var command = 13 + offset;
            if (marker < 0 || command < 0 || report.Length <= command + 3) continue;
            if (report[marker] != (byte)'P' || report[marker + 1] != (byte)'I') continue;
            if (report[command] != cmdId || report[command + 1] is not (0x01 or 0x02)) continue;
            var length = report[command + 2];
            if (length is > 0 and <= 32 && report.Length >= command + 3 + length)
                return report[command + 3];
        }
        return null;
    }

    internal static (string? Name, string? Kind) RazerDeviceInfo(int pid)
        => RazerDevices.TryGetValue(pid, out var info) ? info : (null, null);

    internal static string? RazerKindFromPid(int pid) => RazerDeviceInfo(pid).Kind;

    /// <summary>
    /// Pure 91-byte Razer feature query policy: never probes unknown or PA-headset PIDs, tries the remembered
    /// transaction id first, stops trying other transaction ids on OK or Timeout, and respects the overall deadline.
    /// </summary>
    internal static (int? Level, bool? Charging, byte? WorkingTxId) QueryRazerFeatureDevice(
        int pid,
        byte? preferredTx,
        Func<byte[], byte[]?> sendFeature,
        long? deadlineMs = null,
        Func<long>? clock = null)
    {
        if (!RazerDevices.ContainsKey(pid) || RazerPaPids.Contains(pid))
            return (null, null, null);

        var now = clock ?? (() => Environment.TickCount64);
        IEnumerable<byte> txOrder = preferredTx is { } pref
            ? RazerTxCandidates.OrderBy(t => t == pref ? 0 : 1)
            : RazerTxCandidates;

        foreach (var tx in txOrder)
        {
            if (deadlineMs is { } dl && now() >= dl) break;

            var batResp = QueryRazerFeatureCommand(tx, RazerCmdBattery, sendFeature, deadlineMs, now);
            if (batResp is null) continue;
            if (batResp[1] == RazerStatusTimeout) return (null, null, tx);
            if (batResp[1] == RazerStatusNotSupported) continue;
            if (batResp[1] != RazerStatusOk) continue;

            var level = ParseRazerBattery(batResp);
            if (level is null) return (null, null, tx);

            var chgResp = QueryRazerFeatureCommand(tx, RazerCmdCharging, sendFeature, deadlineMs, now);
            var charging = chgResp is not null ? ParseRazerCharging(chgResp) : null;
            return (level, charging, tx);
        }

        return (null, null, null);
    }

    private static byte[]? QueryRazerFeatureCommand(
        byte txId,
        byte commandId,
        Func<byte[], byte[]?> sendFeature,
        long? deadlineMs,
        Func<long> now)
    {
        var req = BuildRazerReport(txId, commandId);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (deadlineMs is { } dl && now() >= dl) return null;
            var resp = sendFeature(req);
            if (resp is null || resp.Length < RazerReportLength) return null;
            byte status = resp[1];
            if (status is RazerStatusTimeout or RazerStatusNotSupported) return resp;
            if (status == RazerStatusOk && resp[7] == RazerClassPower && resp[8] == commandId) return resp;
            if (status != RazerStatusBusy && status != RazerStatusOk) return resp;
        }
        return null;
    }

    /// <summary>
    /// Merges Razer HID readings by product name (preferring an entry with charging state when both a cable
    /// and a wireless dongle for the same model are connected) into the Bluetooth list without collapsing
    /// duplicate Bluetooth entries.
    /// </summary>
    internal static List<DeviceBattery> MergeRazerIntoBluetooth(IEnumerable<DeviceBattery> bluetooth, IEnumerable<DeviceBattery> razer)
    {
        var list = bluetooth.ToList();
        foreach (var r in razer)
        {
            var idx = list.FindIndex(b => string.Equals(b.Name, r.Name, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
                list.Add(r);
            else if (PreferDevice(r, list[idx]))
                list[idx] = r;
        }
        return list;
    }

    private static bool PreferDevice(DeviceBattery candidate, DeviceBattery existing)
        => (candidate.Charging is not null && existing.Charging is null)
            || (candidate.Charging == true && existing.Charging != true)
            || (candidate.Charging == existing.Charging && candidate.Level > existing.Level);

#if WINDOWS
    private static readonly Guid HidInterfaceGuid = new("4D1E55B2-F16F-11CF-88CB-001111000030");
    // Synapse 4 runs as RazerAppEngine; Synapse 3 runs as Razer Synapse 3 / Razer Synapse Service.
    private static readonly string[] SynapseProcessNames =
        ["RazerAppEngine", "Razer Synapse 3", "Razer Synapse Service", "RazerSynapse"];

    private static (List<DeviceBattery> Devices, string? Error) WindowsRazer()
    {
        var readings = new List<DeviceBattery>();
        var handledDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deadline = Environment.TickCount64 + RazerTotalBudgetMs;
        bool? synapseRunning = null;
        string? lastError = null;

        try
        {
            var guid = HidInterfaceGuid;
            var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x02 | 0x10); // DIGCF_PRESENT | DIGCF_DEVICEINTERFACE
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return ([], null);

            try
            {
                var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                var devData = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                for (uint i = 0; Environment.TickCount64 < deadline && SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref ifData); i++)
                {
                    SetupDiGetDeviceInterfaceDetailW(set, ref ifData, IntPtr.Zero, 0, out var req, IntPtr.Zero);
                    if (req == 0) continue;

                    var mem = Marshal.AllocHGlobal((int)req);
                    try
                    {
                        Marshal.WriteInt32(mem, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetailW(set, ref ifData, mem, req, out _, ref devData)) continue;
                        var path = Marshal.PtrToStringUni(IntPtr.Add(mem, 4));
                        if (string.IsNullOrEmpty(path) || !path.Contains("vid_1532", StringComparison.OrdinalIgnoreCase)) continue;

                        var physicalId = GetPhysicalDeviceId(path, devData.DevInst);
                        if (handledDevices.Contains(physicalId)) continue;

                        using var handle = CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                        if (handle.IsInvalid) continue;

                        var attrs = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                        if (!HidD_GetAttributes(handle, ref attrs) || attrs.VendorID != RazerVendorId) continue;

                        int pid = attrs.ProductID;
                        if (!RazerDevices.ContainsKey(pid)) continue;
                        if (!TryGetHidCaps(handle, out var caps)) continue;

                        DeviceBattery? dev = null;
                        if (RazerPaPids.Contains(pid))
                        {
                            if (caps.UsagePage is 0xFF00 or 0xFF14
                                && caps.InputReportByteLength == RazerPaReportLength
                                && caps.OutputReportByteLength == RazerPaReportLength)
                            {
                                synapseRunning ??= IsRazerSynapseRunning();
                                // The Barracuda vendor collection is shareable; keep the established BlackShark
                                // protection because Synapse can still own that receiver exclusively.
                                if (pid == RazerBarracudaPid || !synapseRunning.Value)
                                    dev = ReadRazerPaDevice(path, handle, pid, deadline);
                            }
                        }
                        else if (caps.FeatureReportByteLength == RazerReportLength)
                        {
                            dev = ReadRazerDevice(handle, pid, deadline);
                        }

                        if (dev is not null)
                        {
                            handledDevices.Add(physicalId);
                            readings.Add(dev);
                        }
                    }
                    catch (Exception ex) { lastError ??= ex.Message; }
                    finally { Marshal.FreeHGlobal(mem); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }
        catch (Exception ex) { lastError ??= ex.Message; }

        return (MergeRazerIntoBluetooth([], readings), lastError);
    }

    private static bool IsRazerSynapseRunning()
    {
        foreach (var name in SynapseProcessNames)
        {
            var procs = Process.GetProcessesByName(name);
            try
            {
                if (procs.Length > 0) return true;
            }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
        }
        return false;
    }

    private static string GetPhysicalDeviceId(string path, uint devInst)
    {
        // Walk up from HID\... to the parent USB device node so multiple interfaces on one receiver share one ID,
        // while two identical receivers on different USB ports stay distinct.
        uint current = devInst;
        for (int hop = 0; hop < 3; hop++)
        {
            if (CM_Get_Parent(out var parent, current, 0) != 0) break;
            current = parent;
            var buf = new char[256];
            if (CM_Get_Device_IDW(current, buf, buf.Length, 0) == 0)
            {
                var id = new string(buf).TrimEnd('\0');
                if (id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)
                    && !id.Contains("&MI_", StringComparison.OrdinalIgnoreCase))
                    return id;
            }
        }
        return devInst != 0 ? $"devinst:{devInst}" : path;
    }

    private static bool TryGetHidCaps(SafeFileHandle handle, out HIDP_CAPS caps)
    {
        caps = default;
        if (!HidD_GetPreparsedData(handle, out var pp)) return false;
        try { return HidP_GetCaps(pp, out caps) >= 0; }
        finally { HidD_FreePreparsedData(pp); }
    }

    private static (string Name, string? Kind) ResolveRazerNameAndKind(SafeFileHandle handle, int pid)
    {
        var (knownName, knownKind) = RazerDeviceInfo(pid);
        var sb = new StringBuilder(256);
        var name = HidD_GetProductString(handle, sb, (uint)(sb.Capacity * 2)) && sb.Length > 0
            ? sb.ToString().Trim()
            : knownName ?? $"Razer Device ({pid:X4})";
        return (name, knownKind);
    }

    private static DeviceBattery? ReadRazerDevice(SafeFileHandle handle, int pid, long deadline)
    {
        byte? preferred = PreferredRazerTxByPid.TryGetValue(pid, out var p) ? p : null;
        var respBuf = new byte[RazerReportLength];

        var (level, charging, workingTx) = QueryRazerFeatureDevice(pid, preferred, req =>
        {
            if (!HidD_SetFeature(handle, req, (uint)req.Length)) return null;
            Thread.Sleep(15);
            Array.Clear(respBuf);
            if (!HidD_GetFeature(handle, respBuf, (uint)respBuf.Length)) return null;
            if (respBuf[1] == RazerStatusBusy) Thread.Sleep(25);
            return respBuf;
        }, deadline);

        if (workingTx is { } tx) PreferredRazerTxByPid[pid] = tx;
        if (level is null) return null;

        var (name, knownKind) = ResolveRazerNameAndKind(handle, pid);
        return new DeviceBattery(name, level.Value, knownKind ?? KindOf(name), charging);
    }

    private static DeviceBattery? ReadRazerPaDevice(string path, SafeFileHandle infoHandle, int pid, long deadline)
    {
        if (Environment.TickCount64 >= deadline) return null;

        // GENERIC_READ | GENERIC_WRITE (0xC0000000), FILE_SHARE_READ | FILE_SHARE_WRITE (3), FILE_FLAG_OVERLAPPED (0x40000000)
        using var rwHandle = CreateFileW(path, 0xC0000000u, 3, IntPtr.Zero, 3, 0x40000000u, IntPtr.Zero);
        if (rwHandle.IsInvalid) return null;

        for (int i = 0; i < 16 && Environment.TickCount64 < deadline; i++)
        {
            var (drained, fatal) = ReadPaOverlapped(rwHandle, RazerPaReportLength, 10);
            if (fatal || drained is null) break;
        }

        var barracuda = pid == RazerBarracudaPid;
        byte[] Remote(bool on) => barracuda ? BuildRazerBarracudaRemoteFrame(on) : BuildRazerPaRemoteFrame(on);

        if (Environment.TickCount64 >= deadline) return null;
        var woke = false;
        for (int attempt = 0; attempt < (barracuda ? 3 : 1) && Environment.TickCount64 < deadline; attempt++)
        {
            if (WritePaOverlapped(rwHandle, Remote(true), 200)) { woke = true; break; }
            Thread.Sleep(150 * (attempt + 1));
        }
        if (!woke) return null;
        Thread.Sleep(35);

        try
        {
            var bat = QueryRazerPa(rwHandle, RazerPaCmdBattery, deadline, barracuda);
            if (bat is null || bat.Value > 100) return null;
            var chg = QueryRazerPa(rwHandle, RazerPaCmdCharging, deadline, barracuda);
            bool? charging = chg is not null ? chg.Value != 0 : null;

            var (name, knownKind) = ResolveRazerNameAndKind(infoHandle, pid);
            return new DeviceBattery(name, bat.Value, knownKind ?? KindOf(name), charging);
        }
        finally
        {
            // Restore remote mode off only when our initial "on" write succeeded.
            WritePaOverlapped(rwHandle, Remote(false), 150);
        }
    }

    private static byte? QueryRazerPa(SafeFileHandle handle, byte cmdId, long deadline, bool barracuda)
    {
        var query = barracuda ? BuildRazerBarracudaQueryFrame(cmdId) : BuildRazerPaQueryFrame(cmdId);
        for (int attempt = 0; attempt < (barracuda ? 4 : 2) && Environment.TickCount64 < deadline; attempt++)
        {
            if (!WritePaOverlapped(handle, query, 200)) break;

            var replyDeadline = Math.Min(deadline, Environment.TickCount64 + 180);
            while (Environment.TickCount64 < replyDeadline)
            {
                var (reply, fatal) = ReadPaOverlapped(handle, RazerPaReportLength, 50);
                if (fatal) return null;
                if (reply is null) continue;
                var val = barracuda ? ParseRazerBarracudaReply(reply, cmdId) : ParseRazerPaReply(reply, cmdId);
                if (val is not null) return val;
            }
        }
        return null;
    }

    private static bool WritePaOverlapped(SafeFileHandle handle, byte[] data, int timeoutMs)
        => PaOverlappedIo(isWrite: true, handle, data, timeoutMs) == data.Length;

    private static (byte[]? Data, bool FatalError) ReadPaOverlapped(SafeFileHandle handle, int len, int timeoutMs)
    {
        var buf = new byte[len];
        var read = PaOverlappedIo(isWrite: false, handle, buf, timeoutMs);
        if (read == -2) return (null, true);
        if (read <= 0) return (null, false);
        if (read < len) Array.Resize(ref buf, read);
        return (buf, false);
    }

    private static int PaOverlappedIo(bool isWrite, SafeFileHandle handle, byte[] buf, int timeoutMs)
    {
        var ev = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        if (ev == IntPtr.Zero) return -2;
        var pinBuf = GCHandle.Alloc(buf, GCHandleType.Pinned);
        var ovMem = Marshal.AllocHGlobal(Marshal.SizeOf<OVERLAPPED>());
        var ioPending = false;
        try
        {
            Marshal.StructureToPtr(new OVERLAPPED { hEvent = ev }, ovMem, false);
            var ok = isWrite
                ? WriteFile(handle, pinBuf.AddrOfPinnedObject(), (uint)buf.Length, IntPtr.Zero, ovMem)
                : ReadFile(handle, pinBuf.AddrOfPinnedObject(), (uint)buf.Length, IntPtr.Zero, ovMem);
            if (!ok)
            {
                if (Marshal.GetLastWin32Error() != 997) return -2; // immediate error (e.g. unplugged)
                ioPending = true;
                if (WaitForSingleObject(ev, (uint)timeoutMs) == 0)
                {
                    ioPending = false;
                }
                else
                {
                    CancelIoEx(handle, ovMem);
                    if (WaitForSingleObject(ev, 200) != 0)
                        return -2; // driver did not complete cancellation in time; keep buffer pinned in finally
                    ioPending = false;
                    // If the operation completed just before CancelIoEx took effect, preserve the completion.
                    if (GetOverlappedResult(handle, ovMem, out var lateTransferred, false))
                        return (int)lateTransferred;
                    return -1; // timeout
                }
            }
            if (!GetOverlappedResult(handle, ovMem, out var transferred, false)) return -2;
            return (int)transferred;
        }
        finally
        {
            if (!ioPending)
            {
                Marshal.FreeHGlobal(ovMem);
                pinBuf.Free();
                CloseHandle(ev);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA { public uint cbSize; public Guid InterfaceClassGuid; public uint Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES { public uint Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage; public ushort UsagePage;
        public ushort InputReportByteLength; public ushort OutputReportByteLength; public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes; public ushort NumberInputButtonCaps; public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices; public ushort NumberOutputButtonCaps; public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices; public ushort NumberFeatureButtonCaps; public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OVERLAPPED { public IntPtr Internal; public IntPtr InternalHigh; public int Offset; public int OffsetHigh; public IntPtr hEvent; }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr data, ref Guid classGuid, uint index, ref SP_DEVICE_INTERFACE_DATA ifData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA ifData, IntPtr detail, uint size, out uint required, IntPtr data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA ifData, IntPtr detail, uint size, out uint required, ref SP_DEVINFO_DATA data);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parent, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_IDW(uint devInst, char[] buffer, int bufferLen, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle handle, IntPtr buf, uint len, IntPtr bytesRead, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle handle, IntPtr buf, uint len, IntPtr bytesWritten, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(SafeFileHandle handle, IntPtr overlapped, out uint transferred, [MarshalAs(UnmanagedType.Bool)] bool wait);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEventW(IntPtr sec, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, IntPtr name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HIDD_ATTRIBUTES attrs);
    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetProductString(SafeFileHandle handle, StringBuilder buf, uint len);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr pp);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll", SetLastError = true)] private static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_SetFeature(SafeFileHandle handle, byte[] buf, uint len);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetFeature(SafeFileHandle handle, byte[] buf, uint len);
#endif
}
