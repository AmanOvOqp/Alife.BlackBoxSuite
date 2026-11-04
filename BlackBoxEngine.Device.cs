using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Alife.BlackBoxSuite;

public partial class BlackBoxEngine
{
    // 6. 外设与音频端点状态
    private bool _lastGamepadConnected = false;
    private bool _isFirstDeviceCheck = true;
    private string? _lastAudioDeviceId = null;
    private string _lastAudioDeviceFriendlyName = string.Empty;
    private HashSet<string>? _lastConnectedBtDevices = null;

    public string CurrentAudioDeviceFriendlyName => _lastAudioDeviceFriendlyName;
    public string ConnectedBluetoothDevicesSummary => _lastConnectedBtDevices != null && _lastConnectedBtDevices.Count > 0
        ? string.Join(", ", _lastConnectedBtDevices)
        : "无已连接蓝牙设备";


    #region 6. 外设与音频输出端点切换
    private void CheckDeviceChanges()
    {
        bool gamepadConnected = DeviceDetectorNative.IsGamepadConnected();
        if (_isFirstDeviceCheck)
        {
            _lastGamepadConnected = gamepadConnected;
        }
        else if (gamepadConnected != _lastGamepadConnected)
        {
            _lastGamepadConnected = gamepadConnected;
            string desc = gamepadConnected ? "游戏手柄已连接 (Xbox/兼容手柄)" : "游戏手柄已断开连接";

            _storage.AddTransientEvent(new BlackBoxEvent
            {
                Time = DateTime.Now,
                Category = "Device",
                Summary = desc,
                Details = "输入外设状态变化",
                DurationSeconds = 0
            }, _config.MaxTransientEventsCount);

            NoteTriggerEvent(gamepadConnected ? TriggerSignal.DeviceAttached : TriggerSignal.DeviceDetached);

            if (_config.DeviceNotifyPrompt)
            {
                DispatchNotification($"【外设变动提醒】{desc}", false, BlackBoxNotificationType.Device);
            }
        }

        var (audioId, audioFriendly) = DeviceDetectorNative.GetDefaultAudioEndpointInfo();
        if (!string.IsNullOrEmpty(audioId))
        {
            if (_lastAudioDeviceId == null)
            {
                _lastAudioDeviceId = audioId;
                _lastAudioDeviceFriendlyName = audioFriendly;
            }
            else if (!audioId.Equals(_lastAudioDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                string oldName = _lastAudioDeviceFriendlyName ?? "未知设备";
                string newName = audioFriendly;
                _lastAudioDeviceId = audioId;
                _lastAudioDeviceFriendlyName = audioFriendly;

                bool oldIsHeadphone = DeviceDetectorNative.IsHeadphoneDevice(oldName);
                bool newIsHeadphone = DeviceDetectorNative.IsHeadphoneDevice(newName);

                string desc = $"默认音频输出设备已切换为：{newName}";
                _storage.AddTransientEvent(new BlackBoxEvent
                {
                    Time = DateTime.Now,
                    Category = "AudioEndpoint",
                    Summary = desc,
                    Details = $"物理音频输出端点切换: {oldName} -> {newName} (切为耳机: {newIsHeadphone})",
                    DurationSeconds = 0
                }, _config.MaxTransientEventsCount);
                NoteTriggerEvent(TriggerSignal.AudioEndpointChanged);

                if (_config.EnableAudioEndpointNotifyPrompt)
                {
                    string prompt;
                    if (newIsHeadphone && !oldIsHeadphone)
                    {
                        prompt = (_config.HeadphonePluggedPromptTemplate ?? "阿瞒已连接/切换到耳机（{DeviceName}），私密听音模式已就绪，可正常进行语音互动。")
                            .Replace("{DeviceName}", newName);
                    }
                    else if (!newIsHeadphone && oldIsHeadphone)
                    {
                        prompt = (_config.HeadphoneUnpluggedPromptTemplate ?? "阿瞒已切换为扬声器外放（{DeviceName}），请注意周围环境，建议转为静默纯文本模式防外放打扰。")
                            .Replace("{DeviceName}", newName);
                    }
                    else
                    {
                        prompt = (_config.AudioEndpointGenericPromptTemplate ?? "默认音频输出设备已切换为：{DeviceName}。")
                            .Replace("{DeviceName}", newName);
                    }
                    DispatchNotification(prompt, false, BlackBoxNotificationType.AudioEndpoint);
                }
            }
        }

        var currentBtDevs = DeviceDetectorNative.GetConnectedBluetoothDevices();
        if (_lastConnectedBtDevices == null)
        {
            _lastConnectedBtDevices = currentBtDevs;
        }
        else
        {
            foreach (var dev in currentBtDevs)
            {
                if (!_lastConnectedBtDevices.Contains(dev))
                {
                    string desc = $"蓝牙外设已连接：{dev}";
                    _storage.AddTransientEvent(new BlackBoxEvent
                    {
                        Time = DateTime.Now,
                        Category = "Device",
                        Summary = desc,
                        Details = "蓝牙外设连入系统",
                        DurationSeconds = 0
                    }, _config.MaxTransientEventsCount);
                    NoteTriggerEvent(TriggerSignal.DeviceAttached);

                    if (_config.DeviceNotifyPrompt)
                    {
                        DispatchNotification($"【蓝牙设备已连接】阿瞒连接了蓝牙设备：{dev}", false, BlackBoxNotificationType.Device);
                    }
                }
            }

            foreach (var dev in _lastConnectedBtDevices)
            {
                if (!currentBtDevs.Contains(dev))
                {
                    string desc = $"蓝牙外设已断开：{dev}";
                    _storage.AddTransientEvent(new BlackBoxEvent
                    {
                        Time = DateTime.Now,
                        Category = "Device",
                        Summary = desc,
                        Details = "蓝牙外设断开连接",
                        DurationSeconds = 0
                    }, _config.MaxTransientEventsCount);
                    NoteTriggerEvent(TriggerSignal.DeviceDetached);

                    if (_config.DeviceNotifyPrompt)
                    {
                        DispatchNotification($"【蓝牙设备已断开】阿瞒断开了蓝牙设备：{dev}", false, BlackBoxNotificationType.Device);
                    }
                }
            }

            _lastConnectedBtDevices = currentBtDevs;
        }

        if (_isFirstDeviceCheck)
        {
            _isFirstDeviceCheck = false;
        }
    }
    #endregion
}

#region 原生外设、音频端点与蓝牙 SetupAPI 辅助类
internal static class DeviceDetectorNative
{
    public static bool IsHeadphoneDevice(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string n = name.ToLowerInvariant();
        return n.Contains("headphone") || n.Contains("headset") || n.Contains("earphone") ||
               n.Contains("耳机") || n.Contains("耳麦") || n.Contains("arctis") ||
               n.Contains("buds") || n.Contains("pods") || n.Contains("wh-1000") ||
               n.Contains("freebuds") || n.Contains("quietcomfort") || n.Contains("airpods");
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState_1_4(int dwUserIndex, ref XINPUT_STATE pState);

    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState_9_1_0(int dwUserIndex, ref XINPUT_STATE pState);

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    public static bool IsGamepadConnected()
    {
        for (int i = 0; i < 4; i++)
        {
            var state = new XINPUT_STATE();
            try
            {
                if (XInputGetState_1_4(i, ref state) == 0) return true;
            }
            catch
            {
                try
                {
                    if (XInputGetState_9_1_0(i, ref state) == 0) return true;
                }
                catch { }
            }
        }
        return false;
    }

    public static (string Id, string FriendlyName) GetDefaultAudioEndpointInfo()
    {
        try
        {
            return AudioMeterNativeHelper.GetDefaultEndpointIdAndName();
        }
        catch
        {
            return (string.Empty, string.Empty);
        }
    }

    public static HashSet<string> GetConnectedBluetoothDevices()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            return SetupApiNative.GetCurrentlyConnectedBluetoothNames();
        }
        catch { }
        return result;
    }
}

internal static class AudioMeterNativeHelper
{
    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    public class MMDeviceEnumeratorComObject { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr ppDevices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        int Activate(ref Guid id, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object pInterface);
        int OpenPropertyStore(int stgmAccess, out IntPtr ppProperties);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        int GetState(out int pdwState);
    }

    public static (string Id, string Name) GetDefaultEndpointIdAndName()
    {
        IMMDevice? dev = null;
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            int hr = enumerator.GetDefaultAudioEndpoint(0, 0, out dev);
            if (hr != 0 || dev == null) return (string.Empty, string.Empty);

            dev.GetId(out string id);
            if (string.IsNullOrEmpty(id)) return (string.Empty, string.Empty);

            string friendly = LookupEndpointNameFromRegistry(id);
            return (id, string.IsNullOrEmpty(friendly) ? id : friendly);
        }
        catch
        {
            return (string.Empty, string.Empty);
        }
        finally
        {
            if (dev != null && Marshal.IsComObject(dev)) Marshal.ReleaseComObject(dev);
        }
    }

    private static string LookupEndpointNameFromRegistry(string deviceId)
    {
        try
        {
            int braceIdx = deviceId.LastIndexOf('{');
            string guidPart = braceIdx >= 0 ? deviceId.Substring(braceIdx) : deviceId;
            string regPath = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\{guidPart}\Properties";

            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(regPath);
            if (key != null)
            {
                string? devDesc = key.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2")?.ToString();
                string? ifaceName = key.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6")?.ToString();

                if (!string.IsNullOrEmpty(devDesc) && !string.IsNullOrEmpty(ifaceName))
                    return $"{devDesc} ({ifaceName})";
                if (!string.IsNullOrEmpty(devDesc))
                    return devDesc;
                if (!string.IsNullOrEmpty(ifaceName))
                    return ifaceName;
            }
        }
        catch { }
        return "AudioOutput";
    }
}

internal static class SetupApiNative
{
    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_ALLCLASSES = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    private static readonly DEVPROPKEY DEVPKEY_Device_FriendlyName = new()
    {
        fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        pid = 14
    };

    private static readonly DEVPROPKEY DEVPKEY_Device_BusReportedDeviceDesc = new()
    {
        fmtid = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"),
        pid = 4
    };

    private static readonly DEVPROPKEY DEVPKEY_Device_IsConnected = new()
    {
        fmtid = new Guid("83da6326-97a6-4088-9453-a1923f573b29"),
        pid = 15
    };

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr ClassGuid, string? Enumerator, IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, uint MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDevicePropertyW(
        IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData,
        ref DEVPROPKEY PropertyKey,
        out uint PropertyType,
        byte[]? PropertyBuffer,
        uint PropertyBufferSize,
        out uint RequiredSize,
        uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    public static HashSet<string> GetCurrentlyConnectedBluetoothNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        QueryEnumerator("BTHENUM", names);
        QueryEnumerator("BTHLE", names);
        return names;
    }

    private static void QueryEnumerator(string enumerator, HashSet<string> results)
    {
        IntPtr hDevInfo = SetupDiGetClassDevsW(IntPtr.Zero, enumerator, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (hDevInfo == IntPtr.Zero || hDevInfo == new IntPtr(-1)) return;

        try
        {
            var devData = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            uint index = 0;
            while (SetupDiEnumDeviceInfo(hDevInfo, index++, ref devData))
            {
                var keyConnected = DEVPKEY_Device_IsConnected;
                byte[] boolBuf = new byte[4];
                if (SetupDiGetDevicePropertyW(hDevInfo, ref devData, ref keyConnected, out uint propType, boolBuf, 4, out _, 0))
                {
                    if (propType == 17 && boolBuf[0] != 0)
                    {
                        string name = GetStringProperty(hDevInfo, ref devData, DEVPKEY_Device_FriendlyName);
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            name = GetStringProperty(hDevInfo, ref devData, DEVPKEY_Device_BusReportedDeviceDesc);
                        }
                        if (!string.IsNullOrWhiteSpace(name) &&
                            !name.Contains("Enumerator", StringComparison.OrdinalIgnoreCase) &&
                            !name.Contains("枚举器") &&
                            !name.Contains("Service", StringComparison.OrdinalIgnoreCase) &&
                            !name.Contains("服务") &&
                            !name.Contains("Avrcp", StringComparison.OrdinalIgnoreCase) &&
                            !name.Contains("传输"))
                        {
                            results.Add(name.Trim());
                        }
                    }
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(hDevInfo);
        }
    }

    private static string GetStringProperty(IntPtr hDevInfo, ref SP_DEVINFO_DATA devData, DEVPROPKEY key)
    {
        byte[] buf = new byte[512];
        if (SetupDiGetDevicePropertyW(hDevInfo, ref devData, ref key, out uint propType, buf, (uint)buf.Length, out uint reqSize, 0))
        {
            if (propType == 18 && reqSize > 2)
            {
                return Encoding.Unicode.GetString(buf, 0, (int)reqSize - 2);
            }
        }
        return string.Empty;
    }
}
#endregion

