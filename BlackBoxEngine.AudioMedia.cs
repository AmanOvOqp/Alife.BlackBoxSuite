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
    // 4. 声卡与进程级音频会话状态 (Module 5 & Upgrade 6)
    private bool _isAudioPlaying = false;
    private DateTime _audioStateStart = DateTime.Now;
    private float _lastPeak = 0f;
    private float _audioStateMaxPeak = 0f;
    private string _activeAudioProcessesSummary = string.Empty;

    public bool IsAudioPlaying => _isAudioPlaying;
    public float CurrentAudioPeak => _lastPeak;
    public float CurrentAudioMaxPeak => _audioStateMaxPeak;
    public string ActiveAudioProcessesSummary => string.IsNullOrEmpty(_activeAudioProcessesSummary) ? "无发声进程" : _activeAudioProcessesSummary;

    // 5. 媒体状态
    private string _currentMedia = string.Empty;
    public string CurrentMediaTitle => _currentMedia;


    #region 4. 声卡与进程级音频会话检测 (Module 5 & Upgrade 6)
    private void CheckAudioPeak()
    {
        float peak = AudioMeterNative.GetMasterPeak();
        _lastPeak = peak;
        bool isPlaying = peak >= _config.AudioPeakThreshold;
        DateTime now = DateTime.Now;

        if (isPlaying)
        {
            var activeSessions = AudioMeterNative.GetActiveAudioSessions(_config.AudioPeakThreshold * 0.5f);
            if (activeSessions.Count > 0)
            {
                _activeAudioProcessesSummary = string.Join(", ", activeSessions.Select(s => $"{s.ProcessName}({s.Peak:F2})"));
            }
        }
        else
        {
            _activeAudioProcessesSummary = string.Empty;
        }

        if (_isAudioPlaying)
        {
            if (isPlaying)
            {
                if (peak > _audioStateMaxPeak)
                {
                    _audioStateMaxPeak = peak;
                }
            }
            else
            {
                double dur = (now - _audioStateStart).TotalSeconds;
                int filterDur = Math.Max(1, _config.AudioFilterDurationSeconds);
                if (dur >= filterDur)
                {
                    _storage.AddTransientEvent(new BlackBoxEvent
                    {
                        Time = _audioStateStart,
                        Category = "Audio",
                        Summary = $"音频播放完成：持续 {BlackBoxEvent.FormatDuration(dur)} (最大峰值 {_audioStateMaxPeak:F2})",
                        Details = "音频输出结束，恢复静音",
                        DurationSeconds = dur
                    }, _config.MaxTransientEventsCount);
                }

                _isAudioPlaying = false;
                _audioStateMaxPeak = 0f;
            }
        }
        else
        {
            if (isPlaying)
            {
                _isAudioPlaying = true;
                _audioStateStart = now;
                _audioStateMaxPeak = peak;
            }
        }
    }
    #endregion

    #region 5. 媒体播放检测 (SMTC + Core Audio 进程级会话联动)
    private void CheckMediaPlayback()
    {
        double continuousAudioSeconds = _isAudioPlaying ? (DateTime.Now - _audioStateStart).TotalSeconds : 0;
        string detectedMedia = MediaDetectorNative.DetectActiveMedia(
            _config.AudioPeakThreshold * 0.5f,
            continuousAudioSeconds >= Math.Max(3, _config.AudioFilterDurationSeconds));

        if (!string.IsNullOrEmpty(detectedMedia) && !detectedMedia.Equals(_currentMedia, StringComparison.OrdinalIgnoreCase))
        {
            _currentMedia = detectedMedia;
            _storage.AddTransientEvent(new BlackBoxEvent
            {
                Time = DateTime.Now,
                Category = "Media",
                Summary = $"媒体播放变动: {detectedMedia}",
                Details = "通过原生 SMTC 或持续发声的媒体进程会话捕获",
                DurationSeconds = 0
            }, _config.MaxTransientEventsCount);
        }
        else if (string.IsNullOrEmpty(detectedMedia))
        {
            _currentMedia = string.Empty;
        }
    }
    #endregion

}

#region 原生 COM 声卡、进程级会话峰值、SMTC 媒体与外设辅助类
internal static class AudioMeterNative
{
    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr ppDevices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid id, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object pInterface);
        int OpenPropertyStore(int stgmAccess, out IntPtr ppProperties);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        int GetState(out int pdwState);
    }

    [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        int GetPeakValue(out float pfPeak);
    }

    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        int GetAudioSessionControl(IntPtr AudioSessionGuid, uint StreamFlags, out IntPtr SessionControl);
        int GetSimpleAudioVolume(IntPtr AudioSessionGuid, uint StreamFlags, out IntPtr AudioVolume);
        int GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
    }

    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        int GetCount(out int SessionCount);
        int GetSession(int SessionCount, [MarshalAs(UnmanagedType.IUnknown)] out object Session);
    }

    [Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        int GetState(out int pRetVal);
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
        int GetGroupingParam(out Guid pRetVal);
        int SetGroupingParam(ref Guid Override, ref Guid EventContext);
        int RegisterAudioSessionNotification(IntPtr NewNotifications);
        int UnregisterAudioSessionNotification(IntPtr NewNotifications);
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        int GetProcessId(out uint pRetVal);
        int IsSystemSoundsSession();
        int SetDuckingPreference(bool optOut);
    }

    public static float GetMasterPeak()
    {
        IMMDevice? dev = null;
        object? meterObj = null;
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            int hr = enumerator.GetDefaultAudioEndpoint(0, 0, out dev);
            if (hr != 0 || dev == null) return 0f;

            Guid iid = typeof(IAudioMeterInformation).GUID;
            hr = dev.Activate(ref iid, 1, IntPtr.Zero, out meterObj);
            if (hr != 0 || meterObj == null) return 0f;

            var meter = (IAudioMeterInformation)meterObj;
            meter.GetPeakValue(out float peak);
            return peak;
        }
        catch
        {
            return 0f;
        }
        finally
        {
            if (meterObj != null && Marshal.IsComObject(meterObj)) Marshal.ReleaseComObject(meterObj);
            if (dev != null && Marshal.IsComObject(dev)) Marshal.ReleaseComObject(dev);
        }
    }

    public static List<(uint Pid, string ProcessName, string MainWindowTitle, float Peak)> GetActiveAudioSessions(float minPeak = 0.01f)
    {
        var list = new List<(uint Pid, string ProcessName, string MainWindowTitle, float Peak)>();
        IMMDevice? dev = null;
        object? mgrObj = null;
        IAudioSessionEnumerator? sessionEnum = null;

        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDefaultAudioEndpoint(0, 0, out dev) != 0 || dev == null)
                return list;

            Guid iidMgr2 = typeof(IAudioSessionManager2).GUID;
            if (dev.Activate(ref iidMgr2, 1, IntPtr.Zero, out mgrObj) != 0 || mgrObj == null)
                return list;

            var mgr2 = (IAudioSessionManager2)mgrObj;
            if (mgr2.GetSessionEnumerator(out sessionEnum) != 0 || sessionEnum == null)
                return list;

            if (sessionEnum.GetCount(out int count) != 0 || count <= 0)
                return list;

            for (int i = 0; i < count; i++)
            {
                object? sessionObj = null;
                try
                {
                    if (sessionEnum.GetSession(i, out sessionObj) == 0 && sessionObj != null)
                    {
                        if (sessionObj is IAudioMeterInformation meter && sessionObj is IAudioSessionControl2 ctl2)
                        {
                            meter.GetPeakValue(out float sessionPeak);
                            if (sessionPeak >= minPeak)
                            {
                                ctl2.GetProcessId(out uint pid);
                                if (pid > 0)
                                {
                                    try
                                    {
                                        using var proc = Process.GetProcessById((int)pid);
                                        list.Add((pid, proc.ProcessName, proc.MainWindowTitle ?? string.Empty, sessionPeak));
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                }
                catch { }
                finally
                {
                    if (sessionObj != null && Marshal.IsComObject(sessionObj))
                        Marshal.ReleaseComObject(sessionObj);
                }
            }
        }
        catch { }
        finally
        {
            if (sessionEnum != null && Marshal.IsComObject(sessionEnum)) Marshal.ReleaseComObject(sessionEnum);
            if (mgrObj != null && Marshal.IsComObject(mgrObj)) Marshal.ReleaseComObject(mgrObj);
            if (dev != null && Marshal.IsComObject(dev)) Marshal.ReleaseComObject(dev);
        }

        return list;
    }
}

internal static class MediaDetectorNative
{
    private static bool _gsmtcInitAttempted = false;
    private static object? _sessionManager = null;
    private static MethodInfo? _getCurrentSessionMethod = null;
    private static MethodInfo? _getPlaybackInfoMethod = null;
    private static MethodInfo? _tryGetMediaPropertiesAsyncMethod = null;
    private static PropertyInfo? _sourceAppProp = null;
    private static PropertyInfo? _titleProp = null;
    private static PropertyInfo? _artistProp = null;

    private static readonly HashSet<string> _nonMediaProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "QQ", "WeChat", "Weixin", "TIM", "Telegram", "Discord", "Alife.Client.Windows",
        "explorer", "SearchHost", "ShellExperienceHost", "svchost", "audiodg"
    };

    private static void EnsureGsmtcInitialized()
    {
        if (_gsmtcInitAttempted) return;
        _gsmtcInitAttempted = true;

        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string sdkPath = Path.Combine(baseDir, "Microsoft.Windows.SDK.NET.dll");
            string winrtPath = Path.Combine(baseDir, "WinRT.Runtime.dll");

            Assembly? sdkAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.Windows.SDK.NET");

            if (sdkAsm == null && File.Exists(sdkPath))
            {
                if (File.Exists(winrtPath)) Assembly.LoadFrom(winrtPath);
                sdkAsm = Assembly.LoadFrom(sdkPath);
            }

            if (sdkAsm == null) return;

            Type? managerType = sdkAsm.GetType("Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager");
            if (managerType == null) return;

            MethodInfo? requestMethod = managerType.GetMethod("RequestAsync", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (requestMethod == null) return;

            object? asyncOp = requestMethod.Invoke(null, null);
            if (asyncOp == null) return;

            MethodInfo? getAwaiter = asyncOp.GetType().GetMethod("GetAwaiter");
            if (getAwaiter != null)
            {
                object? awaiter = getAwaiter.Invoke(asyncOp, null);
                if (awaiter != null)
                {
                    MethodInfo? getResult = awaiter.GetType().GetMethod("GetResult");
                    if (getResult != null)
                    {
                        _sessionManager = getResult.Invoke(awaiter, null);
                    }
                }
            }

            if (_sessionManager != null)
            {
                Type smType = _sessionManager.GetType();
                _getCurrentSessionMethod = smType.GetMethod("GetCurrentSession", BindingFlags.Public | BindingFlags.Instance);

                Type? sessionType = sdkAsm.GetType("Windows.Media.Control.GlobalSystemMediaTransportControlsSession");
                if (sessionType != null)
                {
                    _sourceAppProp = sessionType.GetProperty("SourceAppUserModelId");
                    _getPlaybackInfoMethod = sessionType.GetMethod("GetPlaybackInfo");
                    _tryGetMediaPropertiesAsyncMethod = sessionType.GetMethod("TryGetMediaPropertiesAsync", Type.EmptyTypes);
                }

                Type? mediaPropsType = sdkAsm.GetType("Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties");
                if (mediaPropsType != null)
                {
                    _titleProp = mediaPropsType.GetProperty("Title");
                    _artistProp = mediaPropsType.GetProperty("Artist");
                }
            }
        }
        catch { }
    }

    public static string DetectActiveMedia(float minSessionPeak = 0.01f, bool allowAudioSessionFallback = true)
    {
        // 1. 优先使用 Windows 原生 GSMTC (全局媒体传输控制，覆盖浏览器B站、播客及现代播放器)
        try
        {
            EnsureGsmtcInitialized();
            if (_sessionManager != null && _getCurrentSessionMethod != null)
            {
                object? session = _getCurrentSessionMethod.Invoke(_sessionManager, null);
                if (session != null)
                {
                    bool isPlaying = true;
                    if (_getPlaybackInfoMethod != null)
                    {
                        object? playbackInfo = _getPlaybackInfoMethod.Invoke(session, null);
                        if (playbackInfo != null)
                        {
                            var statusProp = playbackInfo.GetType().GetProperty("PlaybackStatus");
                            if (statusProp != null)
                            {
                                string? statusStr = statusProp.GetValue(playbackInfo)?.ToString();
                                if (!string.IsNullOrEmpty(statusStr) && !statusStr.Equals("Playing", StringComparison.OrdinalIgnoreCase))
                                {
                                    isPlaying = false;
                                }
                            }
                        }
                    }

                    if (isPlaying && _tryGetMediaPropertiesAsyncMethod != null)
                    {
                        object? asyncProps = _tryGetMediaPropertiesAsyncMethod.Invoke(session, null);
                        object? props = null;
                        if (asyncProps != null)
                        {
                            MethodInfo? getAwaiter = asyncProps.GetType().GetMethod("GetAwaiter");
                            if (getAwaiter != null)
                            {
                                object? awaiter = getAwaiter.Invoke(asyncProps, null);
                                if (awaiter != null)
                                {
                                    MethodInfo? getResult = awaiter.GetType().GetMethod("GetResult");
                                    if (getResult != null)
                                    {
                                        props = getResult.Invoke(awaiter, null);
                                    }
                                }
                            }
                        }

                        if (props != null)
                        {
                            string title = _titleProp?.GetValue(props)?.ToString() ?? string.Empty;
                            string artist = _artistProp?.GetValue(props)?.ToString() ?? string.Empty;
                            string sourceApp = _sourceAppProp?.GetValue(session)?.ToString() ?? string.Empty;

                            if (!string.IsNullOrEmpty(sourceApp))
                            {
                                try
                                {
                                    if (sourceApp.Contains('\\') || sourceApp.Contains('/'))
                                    {
                                        sourceApp = Path.GetFileName(sourceApp);
                                    }
                                    else if (sourceApp.Contains('!'))
                                    {
                                        sourceApp = sourceApp.Split('!')[0];
                                    }
                                }
                                catch { }
                            }

                            if (!string.IsNullOrWhiteSpace(title))
                            {
                                string appTag = !string.IsNullOrEmpty(sourceApp) ? $"[{sourceApp}] " : "";
                                string artistTag = !string.IsNullOrWhiteSpace(artist) ? $" - {artist}" : "";
                                return $"{appTag}{title}{artistTag}".Trim();
                            }
                        }
                    }
                }
            }
        }
        catch { }

        // 2. 二级联动：Core Audio 进程级音频会话峰值 (仅当发声已持续达标，且排除 QQ/微信等通讯软件瞬时提示音)
        if (allowAudioSessionFallback)
        {
            try
            {
                var activeSessions = AudioMeterNative.GetActiveAudioSessions(minSessionPeak);
                foreach (var s in activeSessions.OrderByDescending(x => x.Peak))
                {
                    if (_nonMediaProcesses.Contains(s.ProcessName)) continue;

                    string winTitle = s.MainWindowTitle.Trim();
                    if (!string.IsNullOrWhiteSpace(winTitle) &&
                        !winTitle.Equals(s.ProcessName, StringComparison.OrdinalIgnoreCase))
                    {
                        return $"[{s.ProcessName}] {winTitle}";
                    }
                }
            }
            catch { }
        }

        // 3. 三级保底：传统媒体进程窗口标题检测 (仅当当前声卡确实在发声时采信)
        if (allowAudioSessionFallback)
        {
            try
            {
                string[] mediaProcesses = { "cloudmusic", "qqmusic", "kugou", "potplayer", "vlc", "foobar2000" };
                foreach (var name in mediaProcesses)
                {
                    var procs = Process.GetProcessesByName(name);
                    foreach (var p in procs)
                    {
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(p.MainWindowTitle))
                            {
                                return $"{p.ProcessName}: {p.MainWindowTitle}";
                            }
                        }
                        catch { }
                        finally
                        {
                            p.Dispose();
                        }
                    }
                }
            }
            catch { }
        }

        return string.Empty;
    }
}
#endregion
