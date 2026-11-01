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

public class BlackBoxEngine : IDisposable
{
    private static readonly object _syncRoot = new();
    private static BlackBoxEngine? _instance;
    private static int _refCount = 0;

    public static event Action<string, string>? OnActiveNotification; // (prompt, dispatchMode: Poke/Chat)
    public static event Action<string>? OnGlobalDeviceNotification;

    public void DispatchNotification(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;
        string mode = _config.NotifyDispatchMode ?? "Poke";
        try
        {
            OnActiveNotification?.Invoke(prompt, mode);
        }
        catch { }
        try
        {
            OnGlobalDeviceNotification?.Invoke(prompt);
        }
        catch { }
    }

    public static BlackBoxEngine Acquire(BlackBoxConfig config)
    {
        lock (_syncRoot)
        {
            if (_instance == null)
            {
                _instance = new BlackBoxEngine(config, BlackBoxStorage.Instance);
                _instance.Start();
            }
            _refCount++;
            return _instance;
        }
    }

    public static void Release()
    {
        lock (_syncRoot)
        {
            _refCount--;
            if (_refCount <= 0 && _instance != null)
            {
                _instance.InternalDispose();
                _instance = null;
                _refCount = 0;
            }
        }
    }

    public static BlackBoxEngine? Instance => _instance;

    private readonly BlackBoxConfig _config;
    private readonly BlackBoxStorage _storage;

    private readonly CancellationTokenSource _cts = new();
    private Thread? _workerThread;

    // 1. 窗口状态、全屏融合与同屏伴读拓扑 (Module 1 & Module 10)
    private IntPtr _lastHwnd = IntPtr.Zero;
    private string _lastProcess = string.Empty;
    private string _lastTitle = string.Empty;
    private DateTime _lastWindowStart = DateTime.Now;
    private DateTime _lastAccountingTime = DateTime.Now; // 用于隔离挂机/休眠时段的精确有效时长记账点
    private bool _lastIsFullscreen = false;
    private List<CoReadingWindowInfo> _currentCoReadingWindows = new();
    private readonly object _coReadLock = new();

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

    // 2. 键盘、鼠标滚轮与鼠标动力学状态 (Module 2, Module 3 & Module 11)
    private int _sliceKeystrokes = 0;
    private int _sliceBackspaces = 0;
    private DateTime _lastKeyboardSliceTime = DateTime.Now;

    private int _sliceWheelClicks = 0; // 180ms 脉冲防抖后的真实手指拨动/触控板划动次数
    private int _sliceWheelRawNotches = 0; // 原始消息刻度数
    private int _sliceWheelDelta = 0;
    private long _lastWheelEventTick = 0;
    private DateTime _lastMouseWheelSliceTime = DateTime.Now;

    // 鼠标动力学状态 (50ms 降采样，零坐标落盘)
    private long _lastMouseMoveSampleTick = 0;
    private int _lastSampleMouseX = -1;
    private int _lastSampleMouseY = -1;
    private int _sliceMouseMoveDistance = 0;
    private int _sliceLeftClicks = 0;
    private int _sliceMinX = int.MaxValue, _sliceMaxX = int.MinValue;
    private int _sliceMinY = int.MaxValue, _sliceMaxY = int.MinValue;

    // 暴躁甩鼠标 (Rage Shake) 1秒滑动窗口状态
    private int _lastFastDirX = 0;
    private int _lastFastDirY = 0;
    private long _rageWindowStartTick = 0;
    private int _rageReversalCount = 0;
    private long _lastRageTriggerTick = 0;

    // 高频连点脉冲 3秒滑动窗口状态
    private long _clickBurstStartTick = 0;
    private int _clickBurstCount = 0;
    private long _lastClickBurstTriggerTick = 0;

    private string _lastMouseKinematicsStatus = "平稳正常";

    public int CurrentMinuteKeystrokes => _sliceKeystrokes;
    public int CurrentMinuteBackspaces => _sliceBackspaces;
    public int CurrentMinuteWheelClicks => _sliceWheelClicks;
    public int CurrentMinuteWheelDelta => _sliceWheelDelta;
    public int CurrentMinuteMouseDistancePx => _sliceMouseMoveDistance;
    public int CurrentMinuteLeftClicks => _sliceLeftClicks;

    public string CurrentMouseKinematicsSummary
    {
        get
        {
            string liveStatus = _lastMouseKinematicsStatus;
            if (liveStatus == "静止中" && (_sliceMouseMoveDistance > 0 || _sliceLeftClicks > 0))
            {
                liveStatus = "常规活跃操作";
            }
            return $"{liveStatus} (本分钟位移 {_sliceMouseMoveDistance}px, 点击 {_sliceLeftClicks}次)";
        }
    }

    // 3. 空闲与离席检测 (GetLastInputInfo 原生探针 + 多源联合仲裁)
    private bool _isIdle = false;
    private readonly object _idleSyncLock = new();

    public void CheckAndTriggerReturnFromIdle(string triggerSource = "物理输入")
    {
        if (!_isIdle) return;

        lock (_idleSyncLock)
        {
            if (!_isIdle) return;

            DateTime now = DateTime.Now;
            double totalIdle = (now - _idleStartTime).TotalSeconds;
            _isIdle = false;
            _lastActiveTime = now;
            _lastWindowStart = now;
            _lastAccountingTime = now;

            _storage.AddTransientEvent(new BlackBoxEvent
            {
                Time = now,
                Category = "Idle",
                Summary = $"恢复物理活跃操作，空闲结束 (本次离开/静止持续 {BlackBoxEvent.FormatDuration(totalIdle)})",
                Details = $"通过 [{triggerSource}] 恢复活跃于应用: {_lastProcess} ({_lastTitle})",
                DurationSeconds = totalIdle
            }, _config.MaxTransientEventsCount);

            if (_config.EnableReturnNotifyPrompt)
            {
                string prompt = string.IsNullOrWhiteSpace(_config.ReturnPromptTemplate)
                    ? "阿瞒回到屏幕前了。"
                    : _config.ReturnPromptTemplate;
                DispatchNotification(prompt);
            }
        }
    }
    private DateTime _idleStartTime = DateTime.MinValue;
    private DateTime _lastActiveTime = DateTime.Now;
    private DateTime _lastHookInputTime = DateTime.Now;

    // 电源休眠与系统锁屏状态
    private bool _isSuspended = false;
    private DateTime _suspendStartTime = DateTime.MinValue;
    private bool _isLocked = false;
    private DateTime _lockStartTime = DateTime.MinValue;
    private bool _isAudioImmersive = false;

    public bool IsIdle => _isIdle;
    public bool IsSuspended => _isSuspended;
    public bool IsLocked => _isLocked;
    public bool IsAudioImmersive => _isAudioImmersive;
    public double CurrentIdleSeconds => _isIdle ? (DateTime.Now - _idleStartTime).TotalSeconds : GetEffectiveIdleSeconds();

    public string CurrentIdleStatusSummary
    {
        get
        {
            if (_isSuspended)
            {
                double dur = (DateTime.Now - _suspendStartTime).TotalSeconds;
                return $"系统休眠挂起中 (已休眠 {BlackBoxEvent.FormatDuration(dur)})";
            }
            if (_isLocked)
            {
                double dur = (DateTime.Now - _lockStartTime).TotalSeconds;
                return $"Windows 锁屏挂机中 (已锁屏 {BlackBoxEvent.FormatDuration(dur)})";
            }
            if (_isIdle)
            {
                return $"离席/挂机中 (已静止 {BlackBoxEvent.FormatDuration(CurrentIdleSeconds)})";
            }
            if (_isAudioImmersive)
            {
                return $"沉浸影音/倾听中 (物理静止 {BlackBoxEvent.FormatDuration(GetEffectiveIdleSeconds())}, 音频输出中)";
            }
            return $"物理活跃中 (距最近操作 {GetEffectiveIdleSeconds():F0}秒)";
        }
    }

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

    // 7. 剪贴板、截图闪存库与文件脉搏状态 (Module 7, Module 15 & Module 16)
    private ClipboardWatcherNative? _clipboardWatcher;
    private InputHookPumpNative? _inputHookPump;
    private FilePulseWatcherNative? _filePulseWatcher;

    private string _lastClipboardText = string.Empty;
    private DateTime _lastClipboardTime = DateTime.MinValue;
    private string _lastScreenshotSummary = string.Empty;
    private string _lastFilePulseSummary = "暂无近期文件落盘/保存记录";

    public string CurrentLastClipboardSummary => !string.IsNullOrEmpty(_lastClipboardText)
        ? $"[{_lastClipboardTime:HH:mm:ss}] ({_lastClipboardText.Length}字) {(_lastClipboardText.Length > 40 ? _lastClipboardText.Substring(0, 40).Replace("\r", " ").Replace("\n", " ") + "..." : _lastClipboardText.Replace("\r", " ").Replace("\n", " "))}"
        : "暂无最近复制记录";

    public string CurrentLastScreenshotSummary => !string.IsNullOrEmpty(_lastScreenshotSummary)
        ? _lastScreenshotSummary
        : "暂无近期截图记录";

    public string CurrentLastFilePulseSummary => _lastFilePulseSummary;

    private BlackBoxEngine(BlackBoxConfig config, BlackBoxStorage storage)
    {
        _config = config;
        _storage = storage;
    }

    private void Start()
    {
        try
        {
            if (_config.EnableKeyboardWatch || _config.EnableMouseWheelWatch || _config.EnableMouseKinematicsWatch || _config.EnableReturnNotifyPrompt)
            {
                _inputHookPump = new InputHookPumpNative(OnRawKeyEvent, OnRawMouseEvent);
            }
        }
        catch { }

        try
        {
            if (_config.EnableClipboardWatch || _config.EnableScreenshotFlashWatch)
            {
                _clipboardWatcher = new ClipboardWatcherNative(OnClipboardChanged, OnClipboardImageCaptured);
            }
        }
        catch { }

        try
        {
            if (_config.EnableFilePulseWatch && !string.IsNullOrWhiteSpace(_config.WatchedDirectoriesCsv))
            {
                _filePulseWatcher = new FilePulseWatcherNative(_config.WatchedDirectoriesCsv, OnFilePulseDetected);
            }
        }
        catch { }

        try
        {
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
            Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        }
        catch { }

        _workerThread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "BlackBoxEngineWorker"
        };
        _workerThread.Start();
    }

    public void RefreshWatchersIfNeeded()
    {
        try
        {
            _filePulseWatcher?.Dispose();
            _filePulseWatcher = null;
            if (_config.EnableFilePulseWatch && !string.IsNullOrWhiteSpace(_config.WatchedDirectoriesCsv))
            {
                _filePulseWatcher = new FilePulseWatcherNative(_config.WatchedDirectoriesCsv, OnFilePulseDetected);
            }
        }
        catch { }
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

    private void OnClipboardChanged(string text)
    {
        if (!_config.EnableClipboardWatch) return;
        if (string.IsNullOrEmpty(text)) return;
        if (_config.ClipboardDedup && text == _lastClipboardText) return;

        DateTime now = DateTime.Now;
        _lastClipboardText = text;
        _lastClipboardTime = now;

        string sourceCtx = BuildCurrentSourceContextTag();
        var item = _storage.AddClipboard(text, _config, sourceCtx);
        if (item != null)
        {
            _storage.AddTransientEvent(new BlackBoxEvent
            {
                Time = item.Time,
                Category = "Clipboard",
                Summary = $"剪贴板记录：复制了 [{item.Category}] (共{item.Length}字): {item.Summary}",
                Details = string.IsNullOrEmpty(sourceCtx) ? item.Summary : $"来源上下文: {sourceCtx}",
                DurationSeconds = 0
            }, _config.MaxTransientEventsCount);
        }
    }

    private void OnClipboardImageCaptured(Bitmap bmp)
    {
        try
        {
            if (!_config.EnableScreenshotFlashWatch) return;

            DateTime now = DateTime.Now;
            string sourceCtx = BuildCurrentSourceContextTag();
            string monthDir = Path.Combine(_storage.ScreenshotRootDir, now.ToString("yyyy-MM"));
            if (!Directory.Exists(monthDir)) Directory.CreateDirectory(monthDir);

            string fileName = $"shot_{now:yyyyMMdd_HHmmss_fff}.jpg";
            string fullPath = Path.Combine(monthDir, fileName);

            int quality = Math.Clamp(_config.ScreenshotJpegQuality, 10, 100);
            SaveBitmapAsJpeg(bmp, fullPath, quality);

            long fileBytes = 0;
            if (File.Exists(fullPath))
            {
                fileBytes = new FileInfo(fullPath).Length;
            }

            var item = _storage.AddScreenshotClipboard(fullPath, bmp.Width, bmp.Height, fileBytes, sourceCtx);
            if (item != null)
            {
                _lastScreenshotSummary = $"[{now:HH:mm:ss}] [#{item.Id}] {bmp.Width}x{bmp.Height} ({item.Length}KB) {sourceCtx}";
                _storage.AddTransientEvent(new BlackBoxEvent
                {
                    Time = now,
                    Category = "Screenshot",
                    Summary = $"视觉闪存截图 [#{item.Id}] ({bmp.Width}x{bmp.Height}, {item.Length}KB)",
                    Details = $"绑定上下文: {sourceCtx} | 路径: {fullPath}",
                    DurationSeconds = 0
                }, _config.MaxTransientEventsCount);
            }
        }
        catch { }
        finally
        {
            try { bmp.Dispose(); } catch { }
        }
    }

    private static void SaveBitmapAsJpeg(Bitmap bmp, string path, int quality)
    {
        var jpegEncoder = ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

        if (jpegEncoder != null)
        {
            using var encParams = new EncoderParameters(1);
            encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
            bmp.Save(path, jpegEncoder, encParams);
        }
        else
        {
            bmp.Save(path, ImageFormat.Jpeg);
        }
    }

    private void OnFilePulseDetected(string fullPath, bool isNewLandedFile, long sizeBytes)
    {
        try
        {
            if (!_config.EnableFilePulseWatch) return;

            DateTime now = DateTime.Now;
            string fileName = Path.GetFileName(fullPath);
            string dirName = Path.GetDirectoryName(fullPath) ?? string.Empty;
            string sizeStr = sizeBytes >= 1024 * 1024
                ? $"{sizeBytes / (1024.0 * 1024.0):F1}MB"
                : $"{Math.Max(1, sizeBytes / 1024)}KB";

            _storage.RecordFilePulse(fullPath, isNewLandedFile, sizeBytes, now);

            if (isNewLandedFile)
            {
                _lastFilePulseSummary = $"[{now:HH:mm:ss}] 新文件落盘: {fileName} ({sizeStr})";
                _storage.AddTransientEvent(new BlackBoxEvent
                {
                    Time = now,
                    Category = "FilePulse",
                    Summary = $"新文件落盘：{fileName} ({sizeStr})",
                    Details = $"所在目录: {dirName}",
                    DurationSeconds = 0
                }, _config.MaxTransientEventsCount);
            }
            else
            {
                _lastFilePulseSummary = $"[{now:HH:mm:ss}] Ctrl+S 保存产出: {fileName} ({sizeStr})";
                _storage.AddTransientEvent(new BlackBoxEvent
                {
                    Time = now,
                    Category = "FilePulse",
                    Summary = $"工作区保存脉搏 (Ctrl+S)：{fileName}",
                    Details = $"路径: {fullPath} ({sizeStr})",
                    DurationSeconds = 0
                }, _config.MaxTransientEventsCount);
            }
        }
        catch { }
    }

    private void WorkerLoop()
    {
        int tickCounter = 0;
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                tickCounter++;

                if (_config.EnableWindowTimeline)
                {
                    CheckForegroundWindow();
                }

                if (_config.EnableCoReadingWatch && tickCounter % 5 == 0)
                {
                    CheckCoReadingTopology(5.0);
                }

                if (_config.EnableKeyboardWatch || _config.EnableMouseWheelWatch || _config.EnableMouseKinematicsWatch)
                {
                    CheckInputSlice();
                }

                if (_config.EnableIdleWatch)
                {
                    CheckIdleState();
                }

                if (_config.EnableAudioWatch && tickCounter % Math.Max(1, _config.AudioCheckIntervalSeconds) == 0)
                {
                    CheckAudioPeak();
                }

                if (_config.EnableMediaWatch && tickCounter % Math.Max(1, _config.MediaCheckIntervalSeconds) == 0)
                {
                    CheckMediaPlayback();
                }

                if (_config.EnableDeviceWatch && tickCounter % 2 == 0)
                {
                    CheckDeviceChanges();
                }

                // 每 60 秒将正在进行中的前台活跃时长平滑刷入画像，避免不切窗口时数据滞后
                if (tickCounter % 60 == 0)
                {
                    FlushCurrentActiveWindowSegment();
                }

                int saveSec = Math.Max(60, _config.ProfileAutoSaveIntervalMinutes * 60);
                if (tickCounter % saveSec == 0)
                {
                    FlushCurrentActiveWindowSegment();
                    _storage.Save();
                }
            }
            catch { }

            Thread.Sleep(1000);
        }
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
    #endregion

    #region 2. 键盘、滚轮与鼠标动力学体征 (Module 2, 3 & 11)
    internal delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    internal delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    private const int CURSOR_SHOWING = 0x00000001;

    private static bool IsMouseCursorVisible()
    {
        try
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (GetCursorInfo(ref ci))
            {
                return (ci.flags & CURSOR_SHOWING) != 0;
            }
        }
        catch { }
        return true;
    }

    private void OnRawKeyEvent(int msg, IntPtr lParam)
    {
        _lastHookInputTime = DateTime.Now;
        if (_isIdle && (msg == 0x0100 || msg == 0x0104)) // 物理按键毫秒边缘唤醒
        {
            CheckAndTriggerReturnFromIdle("键盘按键");
        }
        if (!_config.EnableKeyboardWatch) return;

        if (msg == 0x0100 || msg == 0x0104) // WM_KEYDOWN, WM_SYSKEYDOWN
        {
            Interlocked.Increment(ref _sliceKeystrokes);
            try
            {
                int vkCode = Marshal.ReadInt32(lParam);
                if (vkCode == 0x08) // VK_BACK
                {
                    Interlocked.Increment(ref _sliceBackspaces);
                }
            }
            catch { }
        }
    }

    private void OnRawMouseEvent(int msg, IntPtr lParam)
    {
        _lastHookInputTime = DateTime.Now;
        if (_isIdle && (msg == 0x020A || msg == 0x020E || msg == 0x0201 || msg == 0x0204 || msg == 0x0207 || msg == 0x0200))
        {
            CheckAndTriggerReturnFromIdle("鼠标操作");
        }

        // 1. 滚轮事件 (WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E)
        if (msg == 0x020A || msg == 0x020E)
        {
            if (_config.EnableMouseWheelAntiIdle)
            {
                _lastHookInputTime = DateTime.Now;
            }

            if (_config.EnableMouseWheelWatch)
            {
                long nowTick = Environment.TickCount64;
                // 180ms 脉冲防抖：将触控板双指惯性滑动或无极滚轮单次拨动产生的数十条连续消息合并为 1 次有效滚动动作
                if (nowTick - _lastWheelEventTick >= 180)
                {
                    Interlocked.Increment(ref _sliceWheelClicks);
                }
                _lastWheelEventTick = nowTick;
                Interlocked.Increment(ref _sliceWheelRawNotches);

                try
                {
                    var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    short delta = (short)((hookStruct.mouseData >> 16) & 0xffff);
                    Interlocked.Add(ref _sliceWheelDelta, Math.Abs((int)delta));
                }
                catch { }
            }
            return;
        }

        // 2. 左键点击脉冲 (WM_LBUTTONDOWN = 0x0201)
        if (msg == 0x0201)
        {
            _lastHookInputTime = DateTime.Now;
            if (!_config.EnableMouseKinematicsWatch) return;

            Interlocked.Increment(ref _sliceLeftClicks);
            long nowTick = Environment.TickCount64;
            if (nowTick - _clickBurstStartTick > 3000)
            {
                _clickBurstStartTick = nowTick;
                _clickBurstCount = 1;
            }
            else
            {
                _clickBurstCount++;
                if (_clickBurstCount >= 16 && nowTick - _lastClickBurstTriggerTick > 30000)
                {
                    _lastClickBurstTriggerTick = nowTick;
                    int burst = _clickBurstCount;
                    _clickBurstCount = 0;
                    _lastMouseKinematicsStatus = "高频连点脉冲";

                    _storage.AddTransientEvent(new BlackBoxEvent
                    {
                        Time = DateTime.Now,
                        Category = "MouseKinematics",
                        Summary = $"鼠标动力学：高频连点脉冲 (3秒内密集左键点击 {burst} 次)",
                        Details = $"当前窗口: {_lastProcess} ({_lastTitle})",
                        DurationSeconds = 3
                    }, _config.MaxTransientEventsCount);
                }
            }
            return;
        }

        // 3. 鼠标移动向量 50ms 降采样动力学分析 (WM_MOUSEMOVE = 0x0200，零坐标落盘，杜绝高回报率鼠标掉帧)
        if (msg == 0x0200 && _config.EnableMouseKinematicsWatch)
        {
            long nowTick = Environment.TickCount64;
            long dt = nowTick - _lastMouseMoveSampleTick;
            if (dt < 50) return; // 严格限制最高 20Hz 采样

            try
            {
                var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                int curX = hookStruct.pt.x;
                int curY = hookStruct.pt.y;

                if (_lastSampleMouseX >= 0 && dt < 600)
                {
                    int dx = curX - _lastSampleMouseX;
                    int dy = curY - _lastSampleMouseY;
                    int dist = (int)Math.Sqrt(dx * dx + dy * dy);

                    if (dist > 0)
                    {
                        Interlocked.Add(ref _sliceMouseMoveDistance, dist);
                        if (curX < _sliceMinX) _sliceMinX = curX;
                        if (curX > _sliceMaxX) _sliceMaxX = curX;
                        if (curY < _sliceMinY) _sliceMinY = curY;
                        if (curY > _sliceMaxY) _sliceMaxY = curY;

                        // 暴躁甩鼠标 (Rage Shake) 检测：速度 > 1500 px/s 且方向高频正负反转
                        double speedPxPerSec = (dist * 1000.0) / dt;
                        if (speedPxPerSec >= 1500.0)
                        {
                            int dirX = Math.Abs(dx) >= 15 ? Math.Sign(dx) : 0;
                            int dirY = Math.Abs(dy) >= 15 ? Math.Sign(dy) : 0;

                            bool reversedX = dirX != 0 && _lastFastDirX != 0 && dirX == -_lastFastDirX;
                            bool reversedY = dirY != 0 && _lastFastDirY != 0 && dirY == -_lastFastDirY;

                            if (reversedX || reversedY)
                            {
                                if (nowTick - _rageWindowStartTick > 1200)
                                {
                                    _rageWindowStartTick = nowTick;
                                    _rageReversalCount = 1;
                                }
                                else
                                {
                                    _rageReversalCount++;
                                    int thresh = Math.Max(3, _config.RageShakeReversalThreshold);
                                    if (_rageReversalCount >= thresh && nowTick - _lastRageTriggerTick > 60000)
                                    {
                                        // 排除 3D 游戏隐藏光标锁视角场景
                                        if (IsMouseCursorVisible())
                                        {
                                            _lastRageTriggerTick = nowTick;
                                            int revCount = _rageReversalCount;
                                            _rageReversalCount = 0;
                                            _lastMouseKinematicsStatus = "疑似烦躁晃动鼠标";

                                            _storage.AddTransientEvent(new BlackBoxEvent
                                            {
                                                Time = DateTime.Now,
                                                Category = "MouseKinematics",
                                                Summary = $"鼠标动力学：检测到暴躁甩鼠标体征 (1秒内高速往复反转 {revCount} 次)",
                                                Details = $"疑似遇到报错/卡壳/烦躁 | 所在应用: {_lastProcess} ({_lastTitle})",
                                                DurationSeconds = 1
                                            }, _config.MaxTransientEventsCount);

                                            if (_config.EnableRageShakeNotifyPrompt)
                                            {
                                                string tmpl = string.IsNullOrWhiteSpace(_config.RageShakePromptTemplate)
                                                    ? "检测到阿瞒在应用 [{ProcessName}] 中频繁高速往复晃动鼠标，疑似遇到难题、报错或思路卡壳，请温柔关切安抚。"
                                                    : _config.RageShakePromptTemplate;
                                                string prompt = tmpl
                                                    .Replace("{ProcessName}", _lastProcess)
                                                    .Replace("{WindowTitle}", _lastTitle);
                                                DispatchNotification(prompt);
                                            }
                                        }
                                    }
                                }
                            }

                            if (dirX != 0) _lastFastDirX = dirX;
                            if (dirY != 0) _lastFastDirY = dirY;
                        }
                    }
                }

                _lastSampleMouseX = curX;
                _lastSampleMouseY = curY;
                _lastMouseMoveSampleTick = nowTick;
            }
            catch { }
        }
    }

    private void CheckInputSlice()
    {
        DateTime now = DateTime.Now;

        // 键盘切片聚合
        if (_config.EnableKeyboardWatch)
        {
            double elapsedKey = (now - _lastKeyboardSliceTime).TotalSeconds;
            if (elapsedKey >= Math.Max(10, _config.KeyboardSliceSeconds))
            {
                int keys = Interlocked.Exchange(ref _sliceKeystrokes, 0);
                int backspaces = Interlocked.Exchange(ref _sliceBackspaces, 0);
                _lastKeyboardSliceTime = now;

                if (keys > 0)
                {
                    double apm = (keys / elapsedKey) * 60.0;
                    _storage.AddTransientEvent(new BlackBoxEvent
                    {
                        Time = now,
                        Category = "Keyboard",
                        Summary = $"打字节拍：击键 {keys} 次 (APM: {apm:F0}), 退格 {backspaces} 次",
                        Details = backspaces > 10 ? "退格密集，可能处于卡壳或反复修改" : "打字较顺畅",
                        DurationSeconds = elapsedKey
                    }, _config.MaxTransientEventsCount);
                }
            }
        }

        // 鼠标滚轮与动力学切片聚合
        if (_config.EnableMouseWheelWatch || _config.EnableMouseKinematicsWatch)
        {
            double elapsedWheel = (now - _lastMouseWheelSliceTime).TotalSeconds;
            if (elapsedWheel >= Math.Max(10, _config.MouseWheelSliceSeconds))
            {
                int wheels = Interlocked.Exchange(ref _sliceWheelClicks, 0);
                int rawNotches = Interlocked.Exchange(ref _sliceWheelRawNotches, 0);
                int wheelDelta = Interlocked.Exchange(ref _sliceWheelDelta, 0);
                int moveDist = Interlocked.Exchange(ref _sliceMouseMoveDistance, 0);
                int leftClicks = Interlocked.Exchange(ref _sliceLeftClicks, 0);

                int spanX = _sliceMaxX >= _sliceMinX ? (_sliceMaxX - _sliceMinX) : 0;
                int spanY = _sliceMaxY >= _sliceMinY ? (_sliceMaxY - _sliceMinY) : 0;
                _sliceMinX = int.MaxValue; _sliceMaxX = int.MinValue;
                _sliceMinY = int.MaxValue; _sliceMaxY = int.MinValue;

                _lastMouseWheelSliceTime = now;

                if (_config.EnableMouseWheelWatch && wheels > 0)
                {
                    int intenseThresh = Math.Max(5, _config.MouseWheelIntenseThreshold);
                    string readingStyle = wheels >= intenseThresh ? "高强度翻页浏览，处于文献/长文深度速读或精读" : "平缓滚动阅读";

                    _storage.AddTransientEvent(new BlackBoxEvent
                    {
                        Time = now,
                        Category = "MouseWheel",
                        Summary = $"滚轮翻页：有效拨动/划动 {wheels} 次 (物理刻度: {rawNotches}, 总幅度: {wheelDelta})",
                        Details = readingStyle,
                        DurationSeconds = elapsedWheel
                    }, _config.MaxTransientEventsCount);
                }

                // 沉思游走 (Aimless Hover) 判定：无滚轮、无打字、极少点击，但鼠标在屏幕小范围内微幅游走审题
                if (_config.EnableMouseKinematicsWatch)
                {
                    if (wheels == 0 && _sliceKeystrokes == 0 && leftClicks <= 2 && moveDist >= 200 && moveDist <= 5000 && spanX < 900 && spanY < 700)
                    {
                        _lastHookInputTime = now; // 防假离线
                        _lastMouseKinematicsStatus = "沉思游走/审题中";

                        _storage.AddTransientEvent(new BlackBoxEvent
                        {
                            Time = now,
                            Category = "MouseKinematics",
                            Summary = $"鼠标动力学：沉思游走体征 (微幅游走 {moveDist}px，范围 {spanX}x{spanY})",
                            Details = $"无翻页与打字但指针持续微移，处于紧盯屏幕审题/思考状态 | 窗口: {_lastProcess}",
                            DurationSeconds = elapsedWheel
                        }, _config.MaxTransientEventsCount);
                    }
                    else if (moveDist > 0 || leftClicks > 0)
                    {
                        _lastMouseKinematicsStatus = "常规活跃操作";
                    }
                    else
                    {
                        _lastMouseKinematicsStatus = "静止中";
                    }
                }
            }
        }
    }
    #endregion

    #region 3. 空闲与离席检测
    public static double GetSystemIdleSeconds()
    {
        try
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (GetLastInputInfo(ref lii))
            {
                uint currentTick = (uint)Environment.TickCount;
                uint elapsed = currentTick - lii.dwTime;
                return elapsed / 1000.0;
            }
        }
        catch { }
        return 0;
    }

    public double GetEffectiveIdleSeconds()
    {
        double winIdle = GetSystemIdleSeconds();
        double hookIdle = (DateTime.Now - _lastHookInputTime).TotalSeconds;
        return Math.Min(winIdle, hookIdle);
    }

    private void CheckIdleState()
    {
        if (_isSuspended || _isLocked) return;

        double idleSeconds = GetEffectiveIdleSeconds();
        DateTime now = DateTime.Now;

        int threshold = _lastIsFullscreen
            ? Math.Max(60, _config.FullscreenIdleThresholdSeconds)
            : Math.Max(30, _config.IdleThresholdSeconds);

        // 铁律：仅当发声已持续超过短音过滤门槛（默认5秒）或有明确媒体标题，且当前【尚未进入离席状态】时，才触发影音防假离线；
        // 严禁在已经离席（_isIdle == true）期间被后台 QQ 提示音唤醒退出离席！
        double currentAudioDur = _isAudioPlaying ? (now - _audioStateStart).TotalSeconds : 0;
        bool isSustainedAudio = (_isAudioPlaying && currentAudioDur >= Math.Max(3, _config.AudioFilterDurationSeconds)) || !string.IsNullOrEmpty(_currentMedia);
        bool isAudioActive = !_isIdle && _config.EnableAudioAntiIdle && isSustainedAudio;

        if (idleSeconds >= threshold)
        {
            if (isAudioActive)
            {
                _isAudioImmersive = true;
            }
            else
            {
                _isAudioImmersive = false;
                if (!_isIdle)
                {
                    lock (_idleSyncLock)
                    {
                        if (!_isIdle)
                        {
                            // 在进入离席前，将直至真正最后一次物理操作时刻（now - idleSeconds）的有效时长结转落盘，剔除静止等待期
                            DateTime actualIdleStart = now.AddSeconds(-idleSeconds);
                            if (actualIdleStart > _lastAccountingTime)
                            {
                                FlushCurrentActiveWindowSegment(actualIdleStart, incrementSession: false);
                            }

                            _isIdle = true;
                            _idleStartTime = actualIdleStart;

                            string screenNotice = _lastIsFullscreen ? $" [全屏专注静止满{_config.FullscreenIdleThresholdSeconds / 60}分钟]" : "";
                            _storage.AddTransientEvent(new BlackBoxEvent
                            {
                                Time = now,
                                Category = "Idle",
                                Summary = $"系统进入离席/挂机空闲状态 (已持续无操作 {idleSeconds:F0}秒){screenNotice}",
                                Details = $"停留在应用: {_lastProcess} ({_lastTitle})",
                                DurationSeconds = idleSeconds
                            }, _config.MaxTransientEventsCount);

                            // 核心：防误判真离席通知（边缘触发，首次判定真走开）
                            if (_config.EnableIdleNotifyPrompt)
                            {
                                string prompt = string.IsNullOrWhiteSpace(_config.IdlePromptTemplate)
                                    ? "阿瞒离开工位了。"
                                    : _config.IdlePromptTemplate;
                                DispatchNotification(prompt);
                            }
                        }
                    }
                }
            }
        }
        else
        {
            _isAudioImmersive = false;

            if (idleSeconds < 5.0)
            {
                if (_isIdle)
                {
                    CheckAndTriggerReturnFromIdle("轮询检测");
                }
                else
                {
                    _lastActiveTime = now;
                }
            }
        }
    }

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (!_config.EnablePowerSuspendWatch) return;

        try
        {
            DateTime now = DateTime.Now;
            if (e.Mode == Microsoft.Win32.PowerModes.Suspend)
            {
                FlushCurrentActiveWindowSegment(now, incrementSession: false);
                _isSuspended = true;
                _suspendStartTime = now;
                _storage.AddTransientEvent(new BlackBoxEvent
                {
                    Time = now,
                    Category = "Power",
                    Summary = "系统进入休眠/睡眠挂起状态",
                    Details = $"休眠前停留在应用: {_lastProcess} ({_lastTitle})",
                    DurationSeconds = 0
                }, _config.MaxTransientEventsCount);
                _storage.Save();
            }
            else if (e.Mode == Microsoft.Win32.PowerModes.Resume)
            {
                double dur = _isSuspended ? (now - _suspendStartTime).TotalSeconds : 0;
                _isSuspended = false;
                _storage.AddTransientEvent(new BlackBoxEvent
                {
                    Time = now,
                    Category = "Power",
                    Summary = $"系统从休眠/睡眠中唤醒恢复 (休眠持续 {BlackBoxEvent.FormatDuration(dur)})",
                    Details = $"恢复活跃于应用: {_lastProcess} ({_lastTitle})",
                    DurationSeconds = dur
                }, _config.MaxTransientEventsCount);

                CheckAndTriggerReturnFromIdle("系统唤醒");
            }
        }
        catch { }
    }

    private void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (!_config.EnableSessionLockWatch) return;

        try
        {
            DateTime now = DateTime.Now;
            if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock)
            {
                FlushCurrentActiveWindowSegment(now, incrementSession: false);
                _isLocked = true;
                _lockStartTime = now;
                _storage.AddTransientEvent(new BlackBoxEvent
                {
                    Time = now,
                    Category = "Session",
                    Summary = "Windows 屏幕已锁定 (Win+L / 会话锁定)",
                    Details = $"锁屏前停留在应用: {_lastProcess} ({_lastTitle})",
                    DurationSeconds = 0
                }, _config.MaxTransientEventsCount);
            }
            else if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock)
            {
                double dur = _isLocked ? (now - _lockStartTime).TotalSeconds : 0;
                _isLocked = false;
                _storage.AddTransientEvent(new BlackBoxEvent
                {
                    Time = now,
                    Category = "Session",
                    Summary = $"Windows 屏幕已解锁 (会话恢复，锁屏持续 {BlackBoxEvent.FormatDuration(dur)})",
                    Details = $"恢复于应用: {_lastProcess} ({_lastTitle})",
                    DurationSeconds = dur
                }, _config.MaxTransientEventsCount);

                CheckAndTriggerReturnFromIdle("屏幕解锁");
            }
        }
        catch { }
    }
    #endregion

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

            if (_config.DeviceNotifyPrompt)
            {
                DispatchNotification($"【外设变动提醒】{desc}");
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
                    DispatchNotification(prompt);
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

                    if (_config.DeviceNotifyPrompt)
                    {
                        DispatchNotification($"【蓝牙设备已连接】阿瞒连接了蓝牙设备：{dev}");
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

                    if (_config.DeviceNotifyPrompt)
                    {
                        DispatchNotification($"【蓝牙设备已断开】阿瞒断开了蓝牙设备：{dev}");
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

    public void Dispose()
    {
        Release();
    }

    private void InternalDispose()
    {
        _cts.Cancel();
        _inputHookPump?.Dispose();
        _inputHookPump = null;
        _clipboardWatcher?.Dispose();
        _clipboardWatcher = null;
        _filePulseWatcher?.Dispose();
        _filePulseWatcher = null;

        try
        {
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        }
        catch { }

        try
        {
            if (_isAudioPlaying)
            {
                double dur = (DateTime.Now - _audioStateStart).TotalSeconds;
                if (dur >= Math.Max(1, _config.AudioFilterDurationSeconds))
                {
                    _storage.AddTransientEvent(new BlackBoxEvent
                    {
                        Time = _audioStateStart,
                        Category = "Audio",
                        Summary = $"音频播放完成：持续 {BlackBoxEvent.FormatDuration(dur)} (最大峰值 {_audioStateMaxPeak:F2})",
                        Details = "模块退出强制结转音频事件",
                        DurationSeconds = dur
                    }, _config.MaxTransientEventsCount);
                }
                _isAudioPlaying = false;
            }
            FlushCurrentActiveWindowSegment(DateTime.Now, incrementSession: false);
        }
        catch { }

        _storage.Save();
    }

    #region Win32 P/Invoke & Structures
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int GetWindowLongW(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);
    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
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

#region 模块7 & 模块16：原生剪贴板与截图视觉闪存监听器 (STA 消息泵)
internal class ClipboardWatcherNative : IDisposable
{
    private readonly Action<string> _onClipboardText;
    private readonly Action<Bitmap> _onClipboardImage;
    private Thread? _thread;
    private InnerClipboardWindow? _window;
    private readonly ManualResetEvent _readyEvent = new(false);

    public ClipboardWatcherNative(Action<string> onClipboardText, Action<Bitmap> onClipboardImage)
    {
        _onClipboardText = onClipboardText;
        _onClipboardImage = onClipboardImage;
        _thread = new Thread(ThreadProc)
        {
            IsBackground = true,
            Name = "BlackBoxClipboardWatcher"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _readyEvent.WaitOne(2000);
    }

    private void ThreadProc()
    {
        try
        {
            _window = new InnerClipboardWindow(_onClipboardText, _onClipboardImage);
            _readyEvent.Set();
            Application.Run();
        }
        catch { }
    }

    public void Dispose()
    {
        try
        {
            if (_window != null)
            {
                _window.InvokeShutdown();
            }
        }
        catch { }
    }

    private class InnerClipboardWindow : NativeWindow
    {
        private readonly Action<string> _textCallback;
        private readonly Action<Bitmap> _imageCallback;
        private long _lastImageFingerprint = 0;
        private long _lastImageTick = 0;

        private const int WM_CLIPBOARDUPDATE = 0x031D;
        private const int WM_CLOSE = 0x0010;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll")]
        private static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("user32.dll")]
        private static extern IntPtr GetClipboardData(uint uFormat);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const uint CF_TEXT = 1;
        private const uint CF_BITMAP = 2;
        private const uint CF_DIB = 8;
        private const uint CF_UNICODETEXT = 13;
        private const uint CF_HDROP = 15;
        private const uint CF_DIBV5 = 17;

        public InnerClipboardWindow(Action<string> textCallback, Action<Bitmap> imageCallback)
        {
            _textCallback = textCallback;
            _imageCallback = imageCallback;
            CreateHandle(new CreateParams());
            AddClipboardFormatListener(Handle);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CLIPBOARDUPDATE)
            {
                // 铁律护栏：若包含文本，优先按文本处理，绝不把 Word/Excel 富文本伴随的渲染图误存为截图
                if (IsClipboardFormatAvailable(CF_UNICODETEXT))
                {
                    string text = ReadCurrentClipboardText();
                    if (!string.IsNullOrEmpty(text))
                    {
                        _textCallback(text);
                    }
                }
                else if (!IsClipboardFormatAvailable(CF_TEXT) &&
                         !IsClipboardFormatAvailable(CF_HDROP) &&
                         (IsClipboardFormatAvailable(CF_BITMAP) || IsClipboardFormatAvailable(CF_DIB) || IsClipboardFormatAvailable(CF_DIBV5)))
                {
                    TryReadCurrentClipboardImage();
                }
            }
            else if (m.Msg == WM_CLOSE)
            {
                RemoveClipboardFormatListener(Handle);
                DestroyHandle();
                Application.ExitThread();
                return;
            }
            base.WndProc(ref m);
        }

        public void InvokeShutdown()
        {
            if (Handle != IntPtr.Zero)
            {
                PostMessage(Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
        }

        private static string ReadCurrentClipboardText()
        {
            for (int i = 0; i < 5; i++)
            {
                if (OpenClipboard(IntPtr.Zero))
                {
                    try
                    {
                        if (IsClipboardFormatAvailable(CF_UNICODETEXT))
                        {
                            IntPtr hData = GetClipboardData(CF_UNICODETEXT);
                            if (hData != IntPtr.Zero)
                            {
                                IntPtr pData = GlobalLock(hData);
                                if (pData != IntPtr.Zero)
                                {
                                    try
                                    {
                                        return Marshal.PtrToStringUni(pData) ?? string.Empty;
                                    }
                                    finally
                                    {
                                        GlobalUnlock(hData);
                                    }
                                }
                            }
                        }
                    }
                    finally
                    {
                        CloseClipboard();
                    }
                    return string.Empty;
                }
                Thread.Sleep(15);
            }
            return string.Empty;
        }

        private void TryReadCurrentClipboardImage()
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    if (!Clipboard.ContainsImage() || Clipboard.ContainsText() || Clipboard.ContainsFileDropList())
                        return;

                    Image? rawImg = Clipboard.GetImage();
                    if (rawImg == null) return;

                    if (rawImg.Width < 100 || rawImg.Height < 100)
                    {
                        rawImg.Dispose();
                        return;
                    }

                    var bmp = new Bitmap(rawImg);
                    rawImg.Dispose();

                    // 计算对角线快速像素指纹，过滤截图工具单次复制触发两次 WM_CLIPBOARDUPDATE
                    long fp = ((long)bmp.Width << 32) ^ (bmp.Height << 16);
                    for (int i = 1; i <= 8; i++)
                    {
                        int px = (bmp.Width * i) / 9;
                        int py = (bmp.Height * i) / 9;
                        fp = (fp * 31) ^ bmp.GetPixel(px, py).ToArgb();
                    }

                    long nowTick = Environment.TickCount64;
                    if (fp == _lastImageFingerprint && (nowTick - _lastImageTick) < 10000)
                    {
                        bmp.Dispose();
                        return;
                    }

                    _lastImageFingerprint = fp;
                    _lastImageTick = nowTick;

                    _imageCallback(bmp);
                    return;
                }
                catch
                {
                    Thread.Sleep(25);
                }
            }
        }
    }
}
#endregion

#region 模块15：基于 ReadDirectoryChangesW 的工作区文件脉搏与下载管家
internal class FilePulseWatcherNative : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Action<string, bool, long> _onFilePulse;
    private readonly ConcurrentDictionary<string, long> _recentEvents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _createdPendingFiles = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> _tempExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".crdownload", ".part", ".opdownload", ".download", ".partial", ".lock", ".swp", ".bak"
    };

    private static readonly HashSet<string> _productiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".docx", ".doc", ".xlsx", ".pptx", ".pdf", ".cs", ".py", ".js", ".ts",
        ".json", ".xml", ".yaml", ".yml", ".html", ".css", ".cpp", ".h", ".java", ".sql",
        ".zip", ".7z", ".rar", ".epub", ".mobi", ".png", ".jpg", ".jpeg", ".mp4", ".mp3"
    };

    public FilePulseWatcherNative(string directoriesCsv, Action<string, bool, long> onFilePulse)
    {
        _onFilePulse = onFilePulse;
        var dirs = directoriesCsv.Split(new[] { ',', ';', '，' }, StringSplitOptions.RemoveEmptyEntries)
                                 .Select(d => d.Trim())
                                 .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in dirs)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;

                var fsw = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 32768
                };

                fsw.Created += (s, e) => HandleFileEvent(e.FullPath, isNewLanded: true);
                fsw.Renamed += (s, e) => HandleFileEvent(e.FullPath, isNewLanded: true);
                fsw.Changed += (s, e) => HandleFileEvent(e.FullPath, isNewLanded: false);

                fsw.EnableRaisingEvents = true;
                _watchers.Add(fsw);
            }
            catch { }
        }
    }

    private void HandleFileEvent(string fullPath, bool isNewLanded)
    {
        try
        {
            if (string.IsNullOrEmpty(fullPath)) return;

            // 过滤编译缓存、Git 仓库内部与临时目录风暴
            if (fullPath.Contains(@"\.git\", StringComparison.OrdinalIgnoreCase) ||
                fullPath.Contains(@"\node_modules\", StringComparison.OrdinalIgnoreCase) ||
                fullPath.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) ||
                fullPath.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase) ||
                fullPath.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase) ||
                fullPath.Contains(@"\__pycache__\", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string fileName = Path.GetFileName(fullPath);
            if (string.IsNullOrEmpty(fileName) || fileName.StartsWith("~$") || fileName.StartsWith("."))
                return;

            string ext = Path.GetExtension(fullPath);
            if (string.IsNullOrEmpty(ext) || _tempExtensions.Contains(ext))
                return;

            if (!_productiveExtensions.Contains(ext))
                return;

            long nowTick = Environment.TickCount64;
            if (isNewLanded)
            {
                _createdPendingFiles[fullPath] = nowTick;
            }

            if (!File.Exists(fullPath)) return;
            long sizeBytes = new FileInfo(fullPath).Length;
            // 若新创建瞬间是0字节空壳，先不锁死防抖字典，等待数毫秒后的第一次非空写入事件
            if (sizeBytes <= 0) return;

            // 若该文件是在最近 2.5 秒内刚创建的，首次非空写入归类为“新文件落盘”而非 Ctrl+S 修改
            if (!isNewLanded && _createdPendingFiles.TryGetValue(fullPath, out long createdTick))
            {
                if (nowTick - createdTick < 2500)
                {
                    isNewLanded = true;
                }
                _createdPendingFiles.TryRemove(fullPath, out _);
            }

            if (_recentEvents.TryGetValue(fullPath, out long lastTick))
            {
                // 3.5 秒防抖合并（过滤 Word / IDE 单次按 Ctrl+S 产生的多次底层写磁盘事件）
                if (nowTick - lastTick < 3500) return;
            }
            _recentEvents[fullPath] = nowTick;

            if (_recentEvents.Count > 500) _recentEvents.Clear();
            if (_createdPendingFiles.Count > 500) _createdPendingFiles.Clear();

            _onFilePulse(fullPath, isNewLanded, sizeBytes);
        }
        catch { }
    }

    public void Dispose()
    {
        foreach (var w in _watchers)
        {
            try
            {
                w.EnableRaisingEvents = false;
                w.Dispose();
            }
            catch { }
        }
        _watchers.Clear();
    }
}
#endregion

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

#region 原生输入钩子独立消息泵 (STA 线程 + Application.Run)
internal class InputHookPumpNative : IDisposable
{
    private readonly Action<int, IntPtr> _onKey;
    private readonly Action<int, IntPtr> _onMouse;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hKeyHook = IntPtr.Zero;
    private IntPtr _hMouseHook = IntPtr.Zero;
    private BlackBoxEngine.LowLevelKeyboardProc? _keyProcDelegate;
    private BlackBoxEngine.LowLevelMouseProc? _mouseProcDelegate;
    private readonly ManualResetEvent _readyEvent = new(false);

    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const uint WM_QUIT = 0x0012;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, BlackBoxEngine.LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookEx")]
    private static extern IntPtr SetWindowsHookExMouse(int idHook, BlackBoxEngine.LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    public InputHookPumpNative(Action<int, IntPtr> onKey, Action<int, IntPtr> onMouse)
    {
        _onKey = onKey;
        _onMouse = onMouse;
        _thread = new Thread(PumpLoop)
        {
            IsBackground = true,
            Name = "BlackBoxInputHookPump",
            Priority = ThreadPriority.Highest
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _readyEvent.WaitOne(2000);
    }

    private void PumpLoop()
    {
        try
        {
            _threadId = GetCurrentThreadId();
            _keyProcDelegate = KeyHookCallback;
            _mouseProcDelegate = MouseHookCallback;

            IntPtr hMod = GetModuleHandle(null);
            _hKeyHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProcDelegate, hMod, 0);
            _hMouseHook = SetWindowsHookExMouse(WH_MOUSE_LL, _mouseProcDelegate, hMod, 0);

            _readyEvent.Set();
            Application.Run();
        }
        catch { }
        finally
        {
            if (_hKeyHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hKeyHook);
                _hKeyHook = IntPtr.Zero;
            }
            if (_hMouseHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hMouseHook);
                _hMouseHook = IntPtr.Zero;
            }
        }
    }

    private IntPtr KeyHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try { _onKey((int)wParam, lParam); } catch { }
        }
        return CallNextHookEx(_hKeyHook, nCode, wParam, lParam);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try { _onMouse((int)wParam, lParam); } catch { }
        }
        return CallNextHookEx(_hMouseHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        try
        {
            if (_threadId != 0)
            {
                PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }
        }
        catch { }
    }
}
#endregion
