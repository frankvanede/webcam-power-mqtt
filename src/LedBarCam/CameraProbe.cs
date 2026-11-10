using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace LedBarCam;

/// <summary>
/// Reads camera "in use" from the device power state (PD_MostRecentPowerState in
/// DEVPKEY_Device_PowerData). D0 = streaming, D3 = idle. Read-only CfgMgr32 calls;
/// no admin rights needed. Works on Windows 11 26H2, where the CapabilityAccessManager
/// ConsentStore registry keys are no longer updated (HASS.Agent issue #498).
/// </summary>
public sealed class CameraProbe
{
    private readonly Regex _match;

    public CameraProbe(string deviceIdRegex) =>
        _match = new Regex(deviceIdRegex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    public sealed record CameraState(string InstanceId, string Name, int DState);

    /// <summary>All present USB devices whose instance ID matches and whose class is Camera or Image.</summary>
    public IReadOnlyList<CameraState> Read()
    {
        var result = new List<CameraState>();
        foreach (var id in PresentUsbDeviceIds())
        {
            if (!_match.IsMatch(id)) continue;
            if (Native.CM_Locate_DevNodeW(out var devInst, id, 0) != 0) continue;

            var cls = GetGuid(devInst, Native.DEVPKEY_Device_ClassGuid);
            if (cls != Native.GUID_CLASS_CAMERA && cls != Native.GUID_CLASS_IMAGE) continue;

            var power = GetBytes(devInst, Native.DEVPKEY_Device_PowerData);
            // CM_POWER_DATA: DWORD PD_Size; DEVICE_POWER_STATE PD_MostRecentPowerState; ...
            // DEVICE_POWER_STATE: 1 = D0 ... 4 = D3
            var d = power is { Length: >= 8 } ? BitConverter.ToInt32(power, 4) - 1 : -1;
            var name = GetString(devInst, Native.DEVPKEY_Device_FriendlyName)
                       ?? GetString(devInst, Native.DEVPKEY_Device_DeviceDesc) ?? id;
            result.Add(new CameraState(id, name, d));
        }
        return result;
    }

    private static IEnumerable<string> PresentUsbDeviceIds()
    {
        const uint flags = Native.CM_GETIDLIST_FILTER_ENUMERATOR | Native.CM_GETIDLIST_FILTER_PRESENT;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (Native.CM_Get_Device_ID_List_SizeW(out var len, "USB", flags) != 0) yield break;
            var buf = new char[len];
            var cr = Native.CM_Get_Device_ID_ListW("USB", buf, len, flags);
            if (cr == Native.CR_BUFFER_SMALL) continue; // device list changed between calls
            if (cr != 0) yield break;
            foreach (var s in new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries))
                yield return s;
            yield break;
        }
    }

    private static byte[]? GetBytes(uint devInst, Native.DEVPROPKEY key)
    {
        uint size = 0;
        Native.CM_Get_DevNode_PropertyW(devInst, ref key, out _, null, ref size, 0);
        if (size == 0 || size > 4096) return null;
        var buf = new byte[size];
        return Native.CM_Get_DevNode_PropertyW(devInst, ref key, out _, buf, ref size, 0) == 0 ? buf : null;
    }

    private static Guid? GetGuid(uint devInst, Native.DEVPROPKEY key)
    {
        var b = GetBytes(devInst, key);
        return b is { Length: 16 } ? new Guid(b) : null;
    }

    private static string? GetString(uint devInst, Native.DEVPROPKEY key)
    {
        var b = GetBytes(devInst, key);
        return b is null ? null : System.Text.Encoding.Unicode.GetString(b).TrimEnd('\0');
    }

    private static class Native
    {
        public const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x00000001;
        public const uint CM_GETIDLIST_FILTER_PRESENT = 0x00000100;
        public const uint CR_BUFFER_SMALL = 0x0000001A;

        public static readonly Guid GUID_CLASS_CAMERA = new("ca3e7ab9-b4c3-4ae6-8251-579ef933890f");
        public static readonly Guid GUID_CLASS_IMAGE = new("6bdd1fc6-810f-11d0-bec7-08002be2092f");

        [StructLayout(LayoutKind.Sequential)]
        public struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
            public DEVPROPKEY(string g, uint p) { fmtid = new Guid(g); pid = p; }
        }

        private const string DevKeys = "a45c254e-df1c-4efd-8020-67d146a850e0";
        public static readonly DEVPROPKEY DEVPKEY_Device_DeviceDesc = new(DevKeys, 2);
        public static readonly DEVPROPKEY DEVPKEY_Device_ClassGuid = new(DevKeys, 10);
        public static readonly DEVPROPKEY DEVPKEY_Device_FriendlyName = new(DevKeys, 14);
        public static readonly DEVPROPKEY DEVPKEY_Device_PowerData = new(DevKeys, 32);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern uint CM_Get_Device_ID_List_SizeW(out uint pulLen, string? pszFilter, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern uint CM_Get_Device_ID_ListW(string? pszFilter, [Out] char[] buffer, uint bufferLen, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern uint CM_Get_DevNode_PropertyW(uint dnDevInst, ref DEVPROPKEY propertyKey,
            out uint propertyType, [Out] byte[]? propertyBuffer, ref uint propertyBufferSize, uint ulFlags);
    }
}
