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
    // 1. 窗口状态、全屏融合与同屏伴读拓扑 (Module 1 & Module 10)
    private IntPtr _lastHwnd = IntPtr.Zero;
    private string _lastProcess = string.Empty;
    private string _lastTitle = string.Empty;
    private DateTime _lastWindowStart = DateTime.Now;
    private DateTime _lastAccountingTime = DateTime.Now; // 用于隔离挂机/休眠时段的精确有效时长记账点
    private bool _lastIsFullscreen = false;
    private List<CoReadingWindowInfo> _currentCoReadingWindows = new();
    private readonly object _coReadLock = new();
    private readonly ConcurrentDictionary<string, DateTime> _appOpenCooldowns = new(StringComparer.OrdinalIgnoreCase);

    public bool CurrentForegroundIsFullscreen => IsWindowFullscreen(_lastHwnd);
    public string CurrentForegroundApp => $"{_lastProcess} - {_lastTitle} ({(CurrentForegroundIsFullscreen ? "全屏" : "窗口化")})";
    public double CurrentForegroundDwellSeconds => (_isIdle || _isSuspended || _isLocked) ? 0 : Math.Max(0, (DateTime.Now - _lastWindowStart).TotalSeconds);

    public string CurrentCoReadingSummary
    {
        get
        {
            lock (_coReadLock)
            {
                if (_currentCoReadingWindows.Count == 0) return "无同屏伴读窗口";
                return string.Join(" | ", _currentCoReadingWindows.Select(w => $"{w.ProcessName}: {w.Title} (可见{(w.UnoccludedRatio * 100):F0}%)"));
            }
        }
    }


    /// <summary>
    /// 将当前正在活跃的前台窗口未结转时长安全刷入每日画像（支持实时查询、进离席前强制结算、定时存盘结算，杜绝离席时间混入）
    /// </summary>
    public void FlushCurrentActiveWindowSegment(DateTime? settleEndTime = null, bool incrementSession = false)
    {
        try
        {
            if (!_config.EnableDailyFocusProfile || string.IsNullOrEmpty(_lastProcess)) return;
            if (_isIdle || _isSuspended || _isLocked) return;

            DateTime end = settleEndTime ?? DateTime.Now;
            double segSeconds = (end - _lastAccountingTime).TotalSeconds;
            if (segSeconds >= 1.0)
            {
                _storage.RecordAppUsage(
                    _lastProcess,
                    _lastTitle,
                    segSeconds,
                    _lastIsFullscreen,
                    _lastAccountingTime,
                    end,
                    _config.ProfileIgnoreProcesses,
                    incrementSession: incrementSession);
                _lastAccountingTime = end;
            }
        }
        catch { }
    }

    private string BuildCurrentSourceContextTag()
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(_lastProcess))
        {
            string shortTitle = _lastTitle.Length > 35 ? _lastTitle.Substring(0, 35) + "..." : _lastTitle;
            sb.Append($"#{_lastProcess}:{shortTitle}");
        }
        lock (_coReadLock)
        {
            if (_currentCoReadingWindows.Count > 0)
            {
                var firstCo = _currentCoReadingWindows[0];
                string coTitle = firstCo.Title.Length > 25 ? firstCo.Title.Substring(0, 25) + "..." : firstCo.Title;
                sb.Append($" | #伴读-{firstCo.ProcessName}:{coTitle}");
            }
        }
        return sb.ToString();
    }


    #region 1. 窗口时间线与同屏伴读拓扑 (Module 1 & Module 10)
    private void CheckForegroundWindow()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;

        if (hwnd != _lastHwnd)
        {
            DateTime now = DateTime.Now;
            double totalDwell = (now - _lastWindowStart).TotalSeconds;
            double activeDwell = (now - _lastAccountingTime).TotalSeconds;

            if (!string.IsNullOrEmpty(_lastProcess))
            {
                // 仅当总停留超过防抖门槛时，结转未记账的真实活跃时段并计入一次会话
                if (totalDwell >= _config.WindowDwellThresholdSeconds)
                {
                    if (_config.EnableDailyFocusProfile && !_isIdle && !_isSuspended && !_isLocked && activeDwell > 0)
                    {
                        _storage.RecordAppUsage(
                            _lastProcess,
                            _lastTitle,
                            activeDwell,
                            _lastIsFullscreen,
                            _lastAccountingTime,
                            now,
                            _config.ProfileIgnoreProcesses,
                            incrementSession: true);
                    }

                    string modeTag = _lastIsFullscreen ? "[全屏沉浸]" : "[窗口化]";
                    _storage.AddTransientEvent(new BlackBoxEvent
                    {
                        Time = _lastWindowStart,
                        Category = "App",
                        Summary = $"切换应用：从 [{_lastProcess}] 切出 {modeTag}",
                        Details = $"标题: {_lastTitle} | 运行模式: {(_lastIsFullscreen ? "全屏沉浸专注" : "常规窗口")}",
                        DurationSeconds = totalDwell
                    }, _config.MaxTransientEventsCount);
                }
            }

            GetWindowThreadProcessId(hwnd, out uint pid);
            string procName = "Unknown";
            try
            {
                using var p = Process.GetProcessById((int)pid);
                procName = p.ProcessName;
            }
            catch { }

            var sb = new StringBuilder(512);
            GetWindowText(hwnd, sb, 512);
            string title = sb.ToString();

            _lastHwnd = hwnd;
            _lastProcess = procName;
            _lastTitle = title;
            _lastWindowStart = now;
            _lastAccountingTime = now;
            _lastIsFullscreen = IsWindowFullscreen(hwnd);
            if (!_lastIsFullscreen) FlushDeferredNotifications();
            CheckWatchedAppOpenNotify(procName, title, now);
        }
        else
        {
            if (!_lastIsFullscreen && IsWindowFullscreen(hwnd))
            {
                _lastIsFullscreen = true;
            }
        }
    }

    private void CheckCoReadingTopology(double intervalSeconds)
    {
        if (_isIdle || _isSuspended || _isLocked)
        {
            lock (_coReadLock) { _currentCoReadingWindows.Clear(); }
            return;
        }

        double ratioThreshold = Math.Clamp(_config.CoReadingUnoccludedRatio, 0.25, 0.95);
        var detected = WindowTopologyNative.DetectCoReadingWindows(_lastHwnd, ratioThreshold, _config.ProfileIgnoreProcesses);

        lock (_coReadLock)
        {
            _currentCoReadingWindows = detected;
        }

        // 当主窗口处于打字、滚轮或近期操作活跃时，为同屏未遮挡的辅助窗口累加“同屏伴读时长”
        bool isActivelyWorking = _sliceKeystrokes > 0 || _sliceWheelClicks > 0 || GetEffectiveIdleSeconds() < 30.0;
        if (isActivelyWorking && _config.EnableDailyFocusProfile && detected.Count > 0)
        {
            DateTime now = DateTime.Now;
            foreach (var w in detected)
            {
                _storage.RecordCoReadingUsage(w.ProcessName, w.Title, intervalSeconds, now, _config.ProfileIgnoreProcesses);
            }
        }
    }

    public static bool IsWindowFullscreen(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd))
            return false;

        var classSb = new StringBuilder(256);
        GetClassNameW(hwnd, classSb, 256);
        string cls = classSb.ToString();
        if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd")
            return false;

        // 排除透明分层置顶悬浮窗（如 Alife 桌宠透明层：WS_EX_LAYERED = 0x00080000, WS_EX_TRANSPARENT = 0x00000020, WS_EX_NOACTIVATE = 0x08000000）
        int exStyle = GetWindowLongW(hwnd, -20);
        if ((exStyle & 0x00000020) != 0 || (exStyle & 0x08000000) != 0)
            return false;

        if ((exStyle & 0x00080000) != 0)
        {
            var titleSb = new StringBuilder(128);
            GetWindowText(hwnd, titleSb, 128);
            string t = titleSb.ToString();
            if (t.Equals("Alife", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (!GetWindowRect(hwnd, out RECT wRect))
            return false;

        IntPtr hMon = MonitorFromWindow(hwnd, 2);
        if (hMon == IntPtr.Zero)
            return false;

        var mi = new MONITORINFO();
        mi.cbSize = (uint)Marshal.SizeOf<MONITORINFO>();
        if (!GetMonitorInfoW(hMon, ref mi))
            return false;

        var mRect = mi.rcMonitor;
        return (wRect.Left <= mRect.Left &&
                wRect.Top <= mRect.Top &&
                wRect.Right >= mRect.Right &&
                wRect.Bottom >= mRect.Bottom);
    }


    public static List<(string ProcessName, string WindowTitle)> GetRunningVisibleApps()
    {
        var list = new List<(string ProcessName, string WindowTitle)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ignore = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "SearchHost", "ShellExperienceHost", "StartMenuExperienceHost",
            "TextInputHost", "ApplicationFrameHost", "SystemSettings", "Alife.Client.Windows"
        };

        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(p.MainWindowTitle))
                    {
                        string pName = p.ProcessName;
                        if (!ignore.Contains(pName) && seen.Add(pName))
                        {
                            list.Add((pName, p.MainWindowTitle));
                        }
                    }
                }
                catch { }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }
        }
        catch { }

        return list.OrderBy(x => x.ProcessName).ToList();
    }

    private void CheckWatchedAppOpenNotify(string procName, string title, DateTime now)
    {
        try
        {
            if (!_config.EnableAppOpenNotifyPrompt || string.IsNullOrWhiteSpace(_config.WatchedAppsCsv))
                return;

            var targets = _config.WatchedAppsCsv
                .Split(new[] { ',', ';', '，', '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            if (targets.Count == 0) return;

            string? matchedKey = null;
            foreach (var t in targets)
            {
                if ((!string.IsNullOrEmpty(procName) && procName.Contains(t, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(title) && title.Contains(t, StringComparison.OrdinalIgnoreCase)))
                {
                    matchedKey = t;
                    break;
                }
            }

            if (matchedKey == null) return;

            int cooldownSec = Math.Max(0, _config.AppOpenNotifyCooldownSeconds);
            if (cooldownSec > 0 && _appOpenCooldowns.TryGetValue(matchedKey, out DateTime lastTrigger))
            {
                if ((now - lastTrigger).TotalSeconds < cooldownSec)
                    return;
            }

            _appOpenCooldowns[matchedKey] = now;

            _storage.AddTransientEvent(new BlackBoxEvent
            {
                Time = now,
                Category = "App",
                Summary = $"目标应用触发：打开/切入 [{procName}] (匹配规则: {matchedKey})",
                Details = $"窗口标题: {title}",
                DurationSeconds = 0
            }, _config.MaxTransientEventsCount);

            string tmpl = string.IsNullOrWhiteSpace(_config.AppOpenPromptTemplate)
                ? "检测到阿瞒打开并切入了目标应用 [{ProcessName}]（窗口：{WindowTitle}）。"
                : _config.AppOpenPromptTemplate;

            string prompt = tmpl
                .Replace("{ProcessName}", procName)
                .Replace("{WindowTitle}", title);

            DispatchNotification(prompt, false, BlackBoxNotificationType.AppOpen);
        }
        catch { }
    }

    #endregion

}

#region 模块10：DWM 真实边界窗口空间拓扑与同屏伴读探测
public class CoReadingWindowInfo
{
    public IntPtr Hwnd { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public double UnoccludedRatio { get; set; }
}

internal static class WindowTopologyNative
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out BlackBoxEngine.RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref BlackBoxEngine.MONITORINFO lpmi);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeRect(IntPtr hwnd, int dwAttribute, out BlackBoxEngine.RECT pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeInt(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;

    public static List<CoReadingWindowInfo> DetectCoReadingWindows(
        IntPtr foregroundHwnd,
        double minUnoccludedRatio = 0.60,
        string? ignoreProcessesCsv = null)
    {
        var results = new List<CoReadingWindowInfo>();
        var occludingRects = new List<BlackBoxEngine.RECT>();

        var ignoreSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Alife.Client.Windows", "SearchHost", "ShellExperienceHost", "StartMenuExperienceHost",
            "TextInputHost", "LockApp", "SystemSettings", "ApplicationFrameHost"
        };

        if (!string.IsNullOrEmpty(ignoreProcessesCsv))
        {
            foreach (var p in ignoreProcessesCsv.Split(new[] { ',', ';', '，' }, StringSplitOptions.RemoveEmptyEntries))
            {
                ignoreSet.Add(p.Trim());
            }
        }

        try
        {
            EnumWindows((hwnd, lParam) =>
            {
                if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;

                // 过滤 UWP 挂起幽灵窗口 (DWMWA_CLOAKED)
                if (DwmGetWindowAttributeInt(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                {
                    return true;
                }

                // 过滤透明穿透层、无激活置顶窗与悬浮工具条
                int exStyle = GetWindowLong32(hwnd, GWL_EXSTYLE);
                if ((exStyle & WS_EX_TOOLWINDOW) != 0 || (exStyle & WS_EX_TRANSPARENT) != 0 || (exStyle & WS_EX_NOACTIVATE) != 0)
                {
                    return true;
                }

                var clsSb = new StringBuilder(128);
                GetClassNameW(hwnd, clsSb, 128);
                string cls = clsSb.ToString();
                if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Windows.UI.Core.CoreWindow")
                {
                    return true;
                }

                var titleSb = new StringBuilder(256);
                GetWindowText(hwnd, titleSb, 256);
                string title = titleSb.ToString().Trim();

                // 过滤 Alife 桌宠透明置顶层，不将其加入 occludingRects 遮挡其它窗口
                if ((exStyle & WS_EX_LAYERED) != 0 && title.Equals("Alife", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // 获取 DWM 真实视觉边界 (剥离 Win10/11 7px 透明阴影边框)
                if (DwmGetWindowAttributeRect(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out BlackBoxEngine.RECT rect, Marshal.SizeOf<BlackBoxEngine.RECT>()) != 0)
                {
                    if (!GetWindowRect(hwnd, out rect)) return true;
                }

                int w = rect.Right - rect.Left;
                int h = rect.Bottom - rect.Top;
                if (w < 300 || h < 250) return true;

                // 若是当前焦点主窗口，将其加入遮挡层后继续往下看辅助窗口
                if (hwnd == foregroundHwnd)
                {
                    occludingRects.Add(rect);
                    return true;
                }

                if (string.IsNullOrEmpty(title))
                {
                    occludingRects.Add(rect);
                    return true;
                }

                // 检查占所属显示器面积比例 (至少占屏幕 18% 以上才算分屏/副屏伴读窗口)
                IntPtr hMon = MonitorFromWindow(hwnd, 2);
                if (hMon != IntPtr.Zero)
                {
                    var mi = new BlackBoxEngine.MONITORINFO { cbSize = (uint)Marshal.SizeOf<BlackBoxEngine.MONITORINFO>() };
                    if (GetMonitorInfoW(hMon, ref mi))
                    {
                        double monArea = (double)(mi.rcMonitor.Right - mi.rcMonitor.Left) * (mi.rcMonitor.Bottom - mi.rcMonitor.Top);
                        double winArea = (double)w * h;
                        if (monArea > 0 && (winArea / monArea) < 0.18)
                        {
                            occludingRects.Add(rect);
                            return true;
                        }
                    }
                }

                // 使用 10x10 均匀网格采样计算未被上方 Z-Order 窗口遮挡的真实可见比例
                int visiblePoints = 0;
                for (int gx = 0; gx < 10; gx++)
                {
                    int px = rect.Left + (int)((gx + 0.5) * w / 10.0);
                    for (int gy = 0; gy < 10; gy++)
                    {
                        int py = rect.Top + (int)((gy + 0.5) * h / 10.0);
                        bool occluded = false;
                        for (int k = 0; k < occludingRects.Count; k++)
                        {
                            var occ = occludingRects[k];
                            if (px >= occ.Left && px <= occ.Right && py >= occ.Top && py <= occ.Bottom)
                            {
                                occluded = true;
                                break;
                            }
                        }
                        if (!occluded) visiblePoints++;
                    }
                }

                double unoccludedRatio = visiblePoints / 100.0;
                occludingRects.Add(rect);

                if (unoccludedRatio >= minUnoccludedRatio)
                {
                    GetWindowThreadProcessId(hwnd, out uint pid);
                    string procName = string.Empty;
                    try
                    {
                        using var p = Process.GetProcessById((int)pid);
                        procName = p.ProcessName;
                    }
                    catch { }

                    if (!string.IsNullOrEmpty(procName) && !ignoreSet.Contains(procName))
                    {
                        results.Add(new CoReadingWindowInfo
                        {
                            Hwnd = hwnd,
                            ProcessName = procName,
                            Title = title,
                            UnoccludedRatio = unoccludedRatio
                        });
                    }
                }

                return results.Count < 4; // 最多同时追踪前 4 个同屏伴读窗口
            }, IntPtr.Zero);
        }
        catch { }

        return results;
    }
}
#endregion

