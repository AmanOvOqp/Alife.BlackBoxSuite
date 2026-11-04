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

public enum BlackBoxNotificationType
{
    Generic,
    Device,
    AudioEndpoint,
    Idle,
    Return,
    RageShake,
    AppOpen,
    KeyWatch,
    ContinuousWork,
    LateNight,
    /// <summary>自定义触发规则专用通道</summary>
    Custom
}

public partial class BlackBoxEngine : IDisposable
{
    private static readonly object _syncRoot = new();
    private static BlackBoxEngine? _instance;
    private static int _refCount = 0;

    public static event Action<string, string, BlackBoxNotificationType>? OnActiveNotificationTyped;
    public static event Action<string, string>? OnActiveNotification; // 保留兼容 (prompt, dispatchMode)
    public static event Action<string>? OnGlobalDeviceNotification;

    private readonly ConcurrentQueue<(string Prompt, BlackBoxNotificationType Type)> _deferredFullscreenPrompts = new();
    private string _lastLateNightNotifyDate = string.Empty;

    public void DispatchNotification(string prompt, bool allowDeferInFullscreen = false, BlackBoxNotificationType type = BlackBoxNotificationType.Generic)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;

        if (allowDeferInFullscreen && _config.DeferNotifyWhenFullscreen && _lastIsFullscreen)
        {
            _deferredFullscreenPrompts.Enqueue((prompt, type));
            _storage.AddTransientEvent(new BlackBoxEvent
            {
                Time = DateTime.Now,
                Category = "Idle",
                Summary = "全屏沉浸护盾生效：非紧急提醒已静默延后，待退出全屏后送达",
                Details = prompt,
                DurationSeconds = 0
            }, _config.MaxTransientEventsCount);
            return;
        }

        string mode = string.IsNullOrWhiteSpace(_config.NotifyDispatchMode) ? "Poke" : _config.NotifyDispatchMode;
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try
            {
                OnActiveNotificationTyped?.Invoke(prompt, mode, type);
                OnActiveNotification?.Invoke(prompt, mode);
            }
            catch { }
        }, null);
    }

    public void FlushDeferredNotifications()
    {
        if (_lastIsFullscreen) return;
        while (_deferredFullscreenPrompts.TryDequeue(out var item))
        {
            if (!string.IsNullOrWhiteSpace(item.Prompt))
            {
                string p = item.Prompt;
                var t = item.Type;
                string mode = string.IsNullOrWhiteSpace(_config.NotifyDispatchMode) ? "Poke" : _config.NotifyDispatchMode;
                ThreadPool.UnsafeQueueUserWorkItem(_ =>
                {
                    try
                    {
                        OnActiveNotificationTyped?.Invoke(p, mode, t);
                        OnActiveNotification?.Invoke(p, mode);
                    }
                    catch { }
                }, null);
            }
        }
    }

    public void TriggerTestNotification(string rawTemplate, string? extraKeyName = null, int extraCount = 1)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(rawTemplate)) return;
            DateTime now = DateTime.Now;
            string proc = string.IsNullOrWhiteSpace(_lastProcess) ? "Alife.Client.Windows" : _lastProcess;
            string title = string.IsNullOrWhiteSpace(_lastTitle) ? "全息感知黑盒配置面板" : _lastTitle;
            string devName = string.IsNullOrWhiteSpace(_lastAudioDeviceFriendlyName) ? "Headset Earphone (SteelSeries Arctis Pro)" : _lastAudioDeviceFriendlyName;

            string prompt = rawTemplate
                .Replace("{ProcessName}", proc)
                .Replace("{WindowTitle}", title)
                .Replace("{DeviceName}", devName)
                .Replace("{AwayMinutes}", "25")
                .Replace("{TimeOfDay}", GetTimeOfDayLabel(now))
                .Replace("{KeyName}", extraKeyName ?? "Back")
                .Replace("{Count}", extraCount.ToString())
                .Replace("{WindowSeconds}", Math.Max(1, _config.KeyWatchWindowSeconds).ToString())
                .Replace("{Minutes}", Math.Max(5, _config.ContinuousWorkThresholdMinutes).ToString())
                .Replace("{CurrentTime}", now.ToString("HH:mm"));

            DispatchNotification(prompt, allowDeferInFullscreen: false, BlackBoxNotificationType.Generic);
        }
        catch { }
    }

    public static string GetTimeOfDayLabel(DateTime dt)
    {
        int h = dt.Hour;
        return h switch
        {
            >= 5 and < 9 => "清晨",
            >= 9 and < 12 => "上午",
            >= 12 and < 14 => "中午",
            >= 14 and < 18 => "下午",
            >= 18 and < 23 => "晚上",
            _ => "深夜"
        };
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
            else if (config != null)
            {
                _instance.UpdateConfig(config);
            }
            _refCount++;
            return _instance;
        }
    }

    public void UpdateConfig(BlackBoxConfig config)
    {
        if (config == null) return;
        lock (_syncRoot)
        {
            _config = config;
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

    private BlackBoxConfig _config;
    private readonly BlackBoxStorage _storage;

    private readonly CancellationTokenSource _cts = new();
    private Thread? _workerThread;


    private InputHookPumpNative? _inputHookPump;

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
            _continuousWorkStartTime = now;
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
                int awayMins = Math.Max(1, (int)Math.Round(totalIdle / 60.0));
                int longThresh = Math.Max(5, _config.LongReturnThresholdMinutes);
                string timeLabel = GetTimeOfDayLabel(now);
                string proc = string.IsNullOrWhiteSpace(_lastProcess) ? "桌面" : _lastProcess;

                string tmpl;
                if (awayMins >= longThresh && !string.IsNullOrWhiteSpace(_config.LongReturnPromptTemplate))
                {
                    tmpl = _config.LongReturnPromptTemplate;
                }
                else
                {
                    tmpl = string.IsNullOrWhiteSpace(_config.ReturnPromptTemplate)
                        ? "阿瞒短暂离开 {AwayMinutes} 分钟后回到屏幕前了（当前时段：{TimeOfDay}）。"
                        : _config.ReturnPromptTemplate;
                }

                string prompt = tmpl
                    .Replace("{AwayMinutes}", awayMins.ToString())
                    .Replace("{TimeOfDay}", timeLabel)
                    .Replace("{ProcessName}", proc)
                    .Replace("{WindowTitle}", _lastTitle);

                DispatchNotification(prompt, allowDeferInFullscreen: false, BlackBoxNotificationType.Return);
            }
        }
    }
    private DateTime _idleStartTime = DateTime.MinValue;
    private DateTime _lastActiveTime = DateTime.Now;
    private DateTime _continuousWorkStartTime = DateTime.Now;
    private DateTime _lastContinuousWorkNotifyTime = DateTime.MinValue;
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


    private BlackBoxEngine(BlackBoxConfig config, BlackBoxStorage storage)
    {
        _config = config;
        _storage = storage;
    }

    private void Start()
    {
        try
        {
            if (_config.EnableKeyboardWatch || _config.EnableKeyWatchNotifyPrompt || _config.EnableMouseWheelWatch || _config.EnableMouseKinematicsWatch || _config.EnableReturnNotifyPrompt)
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
            bool needInputHook = _config.EnableKeyboardWatch || _config.EnableKeyWatchNotifyPrompt ||
                                 _config.EnableMouseWheelWatch || _config.EnableMouseKinematicsWatch ||
                                 _config.EnableReturnNotifyPrompt;
            if (needInputHook && _inputHookPump == null)
            {
                _inputHookPump = new InputHookPumpNative(OnRawKeyEvent, OnRawMouseEvent);
            }
            else if (!needInputHook && _inputHookPump != null)
            {
                _inputHookPump.Dispose();
                _inputHookPump = null;
            }
        }
        catch { }

        try
        {
            bool needClip = _config.EnableClipboardWatch || _config.EnableScreenshotFlashWatch;
            if (needClip && _clipboardWatcher == null)
            {
                _clipboardWatcher = new ClipboardWatcherNative(OnClipboardChanged, OnClipboardImageCaptured);
            }
            else if (!needClip && _clipboardWatcher != null)
            {
                _clipboardWatcher.Dispose();
                _clipboardWatcher = null;
            }
        }
        catch { }

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


    private void WorkerLoop()
    {
        int tickCounter = 0;
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                tickCounter++;

                // 自定义触发规则引擎：每秒采集快照并逐角色评估派发
                try
                {
                    if (_roleRules.Count > 0) TickTriggers(DateTime.Now);
                }
                catch { }

                if (_config.EnableWindowTimeline)
                {
                    CheckForegroundWindow();
                }

                if (_config.EnableCoReadingWatch && tickCounter % 5 == 0)
                {
                    CheckCoReadingTopology(5.0);
                }

                if (_config.EnableKeyboardWatch)
                {
                    CheckKeyboardSlice();
                }

                if (_config.EnableMouseWheelWatch || _config.EnableMouseKinematicsWatch)
                {
                    CheckMouseSlice();
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
                                DispatchNotification(prompt, false, BlackBoxNotificationType.Idle);
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

            if (!_isIdle && _config.EnableContinuousWorkNotifyPrompt)
            {
                double continuousMinutes = (now - _continuousWorkStartTime).TotalMinutes;
                int threshMinutes = Math.Max(5, _config.ContinuousWorkThresholdMinutes);
                if (continuousMinutes >= threshMinutes &&
                    (_lastContinuousWorkNotifyTime == DateTime.MinValue || (now - _lastContinuousWorkNotifyTime).TotalMinutes >= threshMinutes))
                {
                    _lastContinuousWorkNotifyTime = now;
                    int mins = (int)Math.Round(continuousMinutes);

                    _storage.AddTransientEvent(new BlackBoxEvent
                    {
                        Time = now,
                        Category = "Idle",
                        Summary = $"连续久坐/专注防疲劳提醒：已连续活跃操作 {mins} 分钟未离席休息",
                        Details = $"当前停留在应用: {_lastProcess} ({_lastTitle})",
                        DurationSeconds = continuousMinutes * 60.0
                    }, _config.MaxTransientEventsCount);

                    string tmpl = string.IsNullOrWhiteSpace(_config.ContinuousWorkPromptTemplate)
                        ? "阿瞒已经连续在电脑前专注操作满 {Minutes} 分钟没有离开工位休息了（当前应用：{ProcessName}），请温柔提醒他喝口水、活动一下颈椎和眼睛。"
                        : _config.ContinuousWorkPromptTemplate;

                    string prompt = tmpl
                        .Replace("{Minutes}", mins.ToString())
                        .Replace("{ProcessName}", _lastProcess)
                        .Replace("{WindowTitle}", _lastTitle);

                    DispatchNotification(prompt, allowDeferInFullscreen: true, BlackBoxNotificationType.ContinuousWork);
                }
            }

            if (!_isIdle && idleSeconds < 30.0 && _config.EnableLateNightNotifyPrompt)
            {
                CheckLateNightNotify(now);
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
                NoteTriggerEvent(TriggerSignal.SystemSuspending);
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
                NoteTriggerEvent(TriggerSignal.SystemResumed);

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
                NoteTriggerEvent(TriggerSignal.ScreenLocked);
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
                NoteTriggerEvent(TriggerSignal.ScreenUnlocked);

                CheckAndTriggerReturnFromIdle("屏幕解锁");
            }
        }
        catch { }
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


    private void CheckLateNightNotify(DateTime now)
    {
        try
        {
            if (now.Hour >= 5 && now.Hour < 23) return;

            string nightCycleKey = now.Hour >= 23
                ? now.ToString("yyyy-MM-dd")
                : now.AddDays(-1).ToString("yyyy-MM-dd");

            if (_lastLateNightNotifyDate == nightCycleKey) return;

            string rawTime = string.IsNullOrWhiteSpace(_config.LateNightTriggerTime) ? "01:00" : _config.LateNightTriggerTime.Trim();
            var parts = rawTime.Split(':');
            int targetHour = 1, targetMin = 0;
            if (parts.Length >= 2)
            {
                int.TryParse(parts[0], out targetHour);
                int.TryParse(parts[1], out targetMin);
            }

            DateTime triggerTime;
            if (targetHour >= 23)
            {
                DateTime baseDate = now.Hour >= 23 ? now.Date : now.Date.AddDays(-1);
                triggerTime = baseDate.AddHours(targetHour).AddMinutes(targetMin);
            }
            else
            {
                DateTime baseDate = now.Hour >= 23 ? now.Date.AddDays(1) : now.Date;
                triggerTime = baseDate.AddHours(targetHour).AddMinutes(targetMin);
            }

            if (now >= triggerTime)
            {
                _lastLateNightNotifyDate = nightCycleKey;

                _storage.AddTransientEvent(new BlackBoxEvent
                {
                    Time = now,
                    Category = "Idle",
                    Summary = $"深夜修仙温柔哄睡触发：当前时间 {now:HH:mm} 仍在活跃操作",
                    Details = $"所在应用: {_lastProcess} ({_lastTitle})",
                    DurationSeconds = 0
                }, _config.MaxTransientEventsCount);

                string tmpl = string.IsNullOrWhiteSpace(_config.LateNightPromptTemplate)
                    ? "现在已经是深夜 {CurrentTime} 了，阿瞒还在应用 [{ProcessName}] 前忙碌，请温柔心疼地哄他早点保存进度去休息，别熬太晚。"
                    : _config.LateNightPromptTemplate;

                string prompt = tmpl
                    .Replace("{CurrentTime}", now.ToString("HH:mm"))
                    .Replace("{ProcessName}", _lastProcess)
                    .Replace("{WindowTitle}", _lastTitle);

                DispatchNotification(prompt, allowDeferInFullscreen: true, BlackBoxNotificationType.LateNight);
            }
        }
        catch { }
    }
}
