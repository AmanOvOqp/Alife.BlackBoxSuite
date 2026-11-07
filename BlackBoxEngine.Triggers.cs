using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Alife.BlackBoxSuite;

/// <summary>
/// 自定义触发规则可用的信号枚举。数值型参与比较，事件型只做发生计数。
/// </summary>
public enum TriggerSignal
{
    // ---- 数值型 ----
    IdleSeconds,
    ForegroundApp,
    ForegroundTitle,
    ForegroundDwellSeconds,
    IsFullscreen,
    KeystrokesPerMinute,
    BackspacePerMinute,
    WheelPerMinute,
    MousePixelsPerMinute,
    ClickBurstCount,
    RageShakeCount,
    AudioPeak,
    IsAudioPlaying,
    AudioProcess,
    IsHeadphone,
    MediaTitle,
    TodayActiveMinutes,
    TodayFullscreenMinutes,
    ContinuousWorkMinutes,
    ClipboardLength,
    ClipboardText,
    LastFileName,
    LastFileExt,
    MinuteOfDay,
    DayOfWeek,
    CamPresent,
    CamFaceCount,
    CamYaw,
    CamPitch,
    CamAbsentSeconds,
    CamAvailable,

    // ---- 事件型 ----
    AppOpened,
    AppClosed,
    FileDropped,
    ClipboardChanged,
    ScreenLocked,
    ScreenUnlocked,
    SystemResumed,
    DeviceAttached,
    DeviceDetached,
    AudioEndpointChanged,
    SystemSuspending
}

/// <summary>
/// 条件比较符。文本型支持 Equal / NotEqual / Contains。
/// </summary>
public enum TriggerOperator
{
    Greater,
    Less,
    GreaterOrEqual,
    LessOrEqual,
    Equal,
    NotEqual,
    Contains
}

/// <summary>
/// 规则生命周期。
/// </summary>
public enum TriggerLifecycle
{
    /// <summary>一次性，触发后立即删除</summary>
    Once,
    /// <summary>限定总次数，用完自动删除</summary>
    Count,
    /// <summary>每天可再次触发，按日历清零</summary>
    Daily,
    /// <summary>永久有效，直到手动删除</summary>
    Permanent
}

/// <summary>
/// 规则创建来源，用于风控与审计。
/// </summary>
public enum TriggerSource
{
    User,
    Ai
}

/// <summary>
/// 单次评估结果。
/// </summary>
public enum TriggerEvalResult
{
    /// <summary>条件未满足</summary>
    Miss,
    /// <summary>条件满足但仍在持续计时中</summary>
    Pending,
    /// <summary>满足且已触发</summary>
    Fired,
    /// <summary>满足但被冷却或仲裁丢弃</summary>
    Suppressed,
    /// <summary>信号不可用，本条件跳过</summary>
    Unavailable,
    /// <summary>规则处于暂停或已失效状态</summary>
    Inactive
}

/// <summary>
/// 单条触发条件。
/// </summary>
public sealed class TriggerCondition
{
    /// <summary>信号名，取 TriggerSignal 枚举名</summary>
    public string Signal { get; set; } = nameof(TriggerSignal.IdleSeconds);

    /// <summary>比较符，取 TriggerOperator 枚举名</summary>
    public string Operator { get; set; } = nameof(TriggerOperator.Greater);

    /// <summary>数值阈值</summary>
    public double Value { get; set; }

    /// <summary>文本阈值，文本型信号使用</summary>
    public string? TextValue { get; set; }

    /// <summary>需要连续满足多少秒才算命中，0 表示瞬时判定</summary>
    public double DurationSeconds { get; set; }

    /// <summary>持续型条件的宽限容差，中断不超过该秒数不清零</summary>
    public double GraceSeconds { get; set; } = 10;

    public TriggerCondition() { }

    public TriggerCondition(TriggerSignal signal, TriggerOperator op, double value, double durationSeconds = 0)
    {
        Signal = signal.ToString();
        Operator = op.ToString();
        Value = value;
        DurationSeconds = durationSeconds;
    }

    [JsonIgnore]
    public TriggerSignal SignalEnum =>
        Enum.TryParse<TriggerSignal>(Signal, true, out var s) ? s : TriggerSignal.IdleSeconds;

    [JsonIgnore]
    public TriggerOperator OperatorEnum =>
        Enum.TryParse<TriggerOperator>(Operator, true, out var o) ? o : TriggerOperator.Greater;

    public TriggerCondition Clone() => new()
    {
        Signal = Signal,
        Operator = Operator,
        Value = Value,
        TextValue = TextValue,
        DurationSeconds = DurationSeconds,
        GraceSeconds = GraceSeconds
    };
}

/// <summary>
/// 一条自定义触发规则。
/// </summary>
public sealed class TriggerRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";

    /// <summary>归属角色名，来自 Character.Name</summary>
    public string OwnerRole { get; set; } = "";

    public List<TriggerCondition> Conditions { get; set; } = new();

    /// <summary>触发时的播报模板，支持占位符</summary>
    public string Prompt { get; set; } = "";

    /// <summary>派发方式：Poke 或 Chat</summary>
    public string DispatchMode { get; set; } = "Poke";

    /// <summary>生命周期，取 TriggerLifecycle 枚举名</summary>
    public string Lifecycle { get; set; } = nameof(TriggerLifecycle.Permanent);

    /// <summary>Count 模式下的总次数</summary>
    public int MaxTimes { get; set; } = 1;

    /// <summary>剩余可触发次数，小于 0 表示不限</summary>
    public int RemainingTimes { get; set; } = -1;

    /// <summary>触发后冷却秒数，期间不再重复触发</summary>
    public int CooldownSeconds { get; set; } = 300;

    /// <summary>是否启用</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>是否暂停，暂停后不参与评估</summary>
    public bool Paused { get; set; }

    /// <summary>创建来源</summary>
    public string CreatedBy { get; set; } = nameof(TriggerSource.User);

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>截止日期，为空表示永久</summary>
    public DateTime? ExpireAt { get; set; }

    /// <summary>上次触发时间</summary>
    public DateTime? LastFiredAt { get; set; }

    /// <summary>累计触发次数</summary>
    public int FireCount { get; set; }

    /// <summary>仲裁优先级，数字越大越优先</summary>
    public int Priority { get; set; }

    /// 每日触发上限，0 表示不限
    public int DailyMaxTimes { get; set; } = 0;

    /// 今日已触发次数，跨日自动清零
    public int TodayFireCount { get; set; } = 0;

    /// 今日计数的日期戳，格式 yyyy-MM-dd
    public string TodayDateKey { get; set; } = "";

    [JsonIgnore]
    public TriggerLifecycle LifecycleEnum =>
        Enum.TryParse<TriggerLifecycle>(Lifecycle, true, out var l) ? l : TriggerLifecycle.Permanent;

    [JsonIgnore]
    public TriggerSource SourceEnum =>
        Enum.TryParse<TriggerSource>(CreatedBy, true, out var s) ? s : TriggerSource.User;

    /// <summary>该规则当前是否仍有可触发资格</summary>
    public bool IsAlive(DateTime now)
    {
        if (!Enabled || Paused) return false;
        if (ExpireAt.HasValue && now > ExpireAt.Value) return false;
        if (LifecycleEnum == TriggerLifecycle.Count && RemainingTimes == 0) return false;
        if (DailyMaxTimes > 0)
        {
            string today = now.ToString("yyyy-MM-dd");
            if (string.Equals(TodayDateKey, today, StringComparison.Ordinal) && TodayFireCount >= DailyMaxTimes) return false;
        }
        return true;
    }

    /// <summary>触发后结算次数与状态</summary>
    public void ConsumeOnce(DateTime now)
    {
        LastFiredAt = now;
        FireCount++;

        string today = now.ToString("yyyy-MM-dd");
        if (!string.Equals(TodayDateKey, today, StringComparison.Ordinal))
        {
            TodayDateKey = today;
            TodayFireCount = 0;
        }
        TodayFireCount++;
        switch (LifecycleEnum)
        {
            case TriggerLifecycle.Once:
                RemainingTimes = 0;
                Enabled = false;
                break;
            case TriggerLifecycle.Count:
                if (RemainingTimes > 0) RemainingTimes--;
                if (RemainingTimes <= 0) Enabled = false;
                break;
        }
    }
}

/// <summary>
/// 一次评估的日志条目，用于排查为什么响或为什么不响。
/// </summary>
public sealed class TriggerLogEntry
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string RuleId { get; set; } = "";
    public string RuleName { get; set; } = "";
    public string OwnerRole { get; set; } = "";
    public string Result { get; set; } = nameof(TriggerEvalResult.Miss);
    public string Detail { get; set; } = "";
}

/// <summary>
/// 某一时刻的全部信号取值快照。
/// </summary>
public sealed class TriggerSignalSnapshot
{
    private readonly Dictionary<TriggerSignal, double> _numbers = new();
    private readonly Dictionary<TriggerSignal, string> _texts = new();
    private readonly HashSet<TriggerSignal> _unavailable = new();

    public DateTime Time { get; } = DateTime.Now;

    public void SetNumber(TriggerSignal signal, double value)
    {
        _numbers[signal] = value;
        _unavailable.Remove(signal);
    }

    public void SetText(TriggerSignal signal, string? value)
    {
        if (value == null) { _unavailable.Add(signal); return; }
        _texts[signal] = value;
        _unavailable.Remove(signal);
    }

    public void MarkUnavailable(TriggerSignal signal)
    {
        _unavailable.Add(signal);
        _numbers.Remove(signal);
        _texts.Remove(signal);
    }

    public bool TryGetNumber(TriggerSignal signal, out double value)
    {
        value = default;
        if (_unavailable.Contains(signal)) return false;
        return _numbers.TryGetValue(signal, out value);
    }

    public bool TryGetText(TriggerSignal signal, out string value)
    {
        value = string.Empty;
        if (_unavailable.Contains(signal)) return false;
        return _texts.TryGetValue(signal, out value!) && value != null;
    }

    public bool IsAvailable(TriggerSignal signal) => !_unavailable.Contains(signal);

    private readonly HashSet<TriggerSignal> _events = new();
    private readonly Dictionary<TriggerSignal, string> _eventDetails = new();

    /// //本轮发生过的事件信号，命中后由评估器摘除，保证一个事件只触发一次
    //
    public void SetEvent(TriggerSignal signal, string detail = "")
    {
        _events.Add(signal);
        if (!string.IsNullOrEmpty(detail)) _eventDetails[signal] = detail;
    }

    public bool HasEvent(TriggerSignal signal) => _events.Contains(signal);

    public string GetEventDetail(TriggerSignal signal) =>
        _eventDetails.TryGetValue(signal, out var d) ? d : "";

    public bool RemoveEvent(TriggerSignal signal)
    {
        _eventDetails.Remove(signal);
        return _events.Remove(signal);
    }

    public int EventCount => _events.Count;
}


/// <summary>
/// 平滑模式。
/// </summary>
public enum TriggerSmoothMode
{
    /// <summary>窗口内均值，适合 APM、鼠标距离等</summary>
    Average,
    /// <summary>窗口内峰值，适合音频音量</summary>
    Max,
    /// <summary>直接取最近一次采样</summary>
    Last,
    /// <summary>窗口内样本求和，适合每秒增量累积出的每分钟速率</summary>
    Sum
}

/// <summary>
/// 抖动信号的滑动窗口平滑器。所有数值型信号统一走这里，避免瞬时抖动误触发。
/// </summary>
public sealed class TriggerSignalSmoother
{
    private sealed class Window
    {
        public readonly Queue<(DateTime Time, double Value)> Samples = new();
        public double Sum;
        public double Max;
    }

    private readonly Dictionary<TriggerSignal, Window> _windows = new();
    private readonly int _defaultWindowSeconds;

    /// <summary>按信号指定的平滑模式，未指定走 Average</summary>
    public Dictionary<TriggerSignal, TriggerSmoothMode> Modes { get; } = new();

    /// <summary>按信号指定的窗口长度（秒），未指定走默认</summary>
    public Dictionary<TriggerSignal, int> WindowSeconds { get; } = new();

    public TriggerSignalSmoother(int defaultWindowSeconds = 60)
    {
        _defaultWindowSeconds = Math.Max(5, defaultWindowSeconds);

        // 默认按信号特性分配，避免一刀切
        Modes[TriggerSignal.AudioPeak] = TriggerSmoothMode.Max;
        Modes[TriggerSignal.ClickBurstCount] = TriggerSmoothMode.Max;
        Modes[TriggerSignal.RageShakeCount] = TriggerSmoothMode.Max;
        Modes[TriggerSignal.IsAudioPlaying] = TriggerSmoothMode.Last;
        Modes[TriggerSignal.IsFullscreen] = TriggerSmoothMode.Last;
        Modes[TriggerSignal.IsHeadphone] = TriggerSmoothMode.Last;
        Modes[TriggerSignal.IdleSeconds] = TriggerSmoothMode.Last;
        Modes[TriggerSignal.ForegroundDwellSeconds] = TriggerSmoothMode.Last;
        Modes[TriggerSignal.MinuteOfDay] = TriggerSmoothMode.Last;
        Modes[TriggerSignal.DayOfWeek] = TriggerSmoothMode.Last;
        Modes[TriggerSignal.TodayActiveMinutes] = TriggerSmoothMode.Last;
        Modes[TriggerSignal.TodayFullscreenMinutes] = TriggerSmoothMode.Last;
        Modes[TriggerSignal.ContinuousWorkMinutes] = TriggerSmoothMode.Last;
        Modes[TriggerSignal.ClipboardLength] = TriggerSmoothMode.Last;
        Modes[TriggerSignal.KeystrokesPerMinute] = TriggerSmoothMode.Sum;
        Modes[TriggerSignal.BackspacePerMinute] = TriggerSmoothMode.Sum;
        Modes[TriggerSignal.WheelPerMinute] = TriggerSmoothMode.Sum;
        Modes[TriggerSignal.MousePixelsPerMinute] = TriggerSmoothMode.Sum;
    }

    public int GetWindowSeconds(TriggerSignal signal)
        => WindowSeconds.TryGetValue(signal, out var s) ? Math.Max(5, s) : _defaultWindowSeconds;

    public TriggerSmoothMode GetMode(TriggerSignal signal)
        => Modes.TryGetValue(signal, out var m) ? m : TriggerSmoothMode.Average;

    /// <summary>压入一次采样并清理过期样本。</summary>
    public void Push(TriggerSignal signal, double value, DateTime now)
    {
        if (!_windows.TryGetValue(signal, out var w))
        {
            w = new Window();
            _windows[signal] = w;
        }

        w.Samples.Enqueue((now, value));
        w.Sum += value;
        if (w.Samples.Count == 1 || value > w.Max) w.Max = value;

        Prune(signal, w, now);
    }

    private void Prune(TriggerSignal signal, Window w, DateTime now)
    {
        int span = GetWindowSeconds(signal);
        while (w.Samples.Count > 0 && (now - w.Samples.Peek().Time).TotalSeconds > span)
        {
            var old = w.Samples.Dequeue();
            w.Sum -= old.Value;
            if (w.Samples.Count == 0)
            {
                w.Max = 0;
            }
            else if (Math.Abs(old.Value - w.Max) < 1e-9)
            {
                // 被移除的正是当前峰值，重新计算
                w.Max = w.Samples.Max(x => x.Value);
            }
        }
        if (w.Sum < 0) w.Sum = 0;
    }

    /// <summary>取当前平滑后的值。无样本时返回 false。</summary>
    public bool TryGet(TriggerSignal signal, DateTime now, out double value)
    {
        value = 0;
        if (!_windows.TryGetValue(signal, out var w)) return false;

        Prune(signal, w, now);
        if (w.Samples.Count == 0) return false;

        switch (GetMode(signal))
        {
            case TriggerSmoothMode.Max:
                value = w.Max;
                break;
            case TriggerSmoothMode.Last:
                value = w.Samples.Last().Value;
                break;
            case TriggerSmoothMode.Sum:
                value = w.Sum;
                break;
            default:
                value = w.Sum / w.Samples.Count;
                break;
        }
        return true;
    }

    /// <summary>清空全部窗口，休眠唤醒或重置时调用。</summary>
    public void Reset()
    {
        lock (_windows)
        {
            _windows.Clear();
        }
    }
}


public partial class BlackBoxEngine
{
    private readonly TriggerSignalSmoother _triggerSmoother = new(60);
    private readonly Dictionary<TriggerSignal, double> _lastRawCounter = new();
    private readonly Dictionary<TriggerSignal, double> _eventCounters = new();
    private string _prevTriggerApp = "";
    private string _prevTriggerFile = "";
    private DateTime _snapshotLastTick = DateTime.MinValue;

    /// <summary>
    /// 采集当前全部信号，形成快照。取不到值的信号一律标记为不可用，绝不拿 0 冒充真实值。
    /// </summary>
    public TriggerSignalSnapshot CollectSnapshot(DateTime now)
    {
        var snap = new TriggerSignalSnapshot();
        double secSinceLast = _snapshotLastTick == DateTime.MinValue
            ? 1.0
            : Math.Clamp((now - _snapshotLastTick).TotalSeconds, 0.5, 5.0);
        _snapshotLastTick = now;

        // ---- 空闲 ----
        try { snap.SetNumber(TriggerSignal.IdleSeconds, GetEffectiveIdleSeconds()); }
        catch { snap.MarkUnavailable(TriggerSignal.IdleSeconds); }

        // ---- 前台应用 ----
        try
        {
            string app = CurrentForegroundApp;
            if (!string.IsNullOrEmpty(app))
            {
                snap.SetText(TriggerSignal.ForegroundApp, app);
                if (!string.IsNullOrEmpty(_prevTriggerApp) && !string.Equals(_prevTriggerApp, app, StringComparison.OrdinalIgnoreCase))
                {
                    snap.SetEvent(TriggerSignal.AppOpened, app);
                    snap.SetEvent(TriggerSignal.AppClosed, _prevTriggerApp);
                }
                _prevTriggerApp = app;
            }
            else snap.MarkUnavailable(TriggerSignal.ForegroundApp);
        }
        catch { snap.MarkUnavailable(TriggerSignal.ForegroundApp); }

        try
        {
            if (!string.IsNullOrEmpty(_lastTitle)) snap.SetText(TriggerSignal.ForegroundTitle, _lastTitle);
            else snap.MarkUnavailable(TriggerSignal.ForegroundTitle);
        }
        catch { snap.MarkUnavailable(TriggerSignal.ForegroundTitle); }

        try { snap.SetNumber(TriggerSignal.ForegroundDwellSeconds, CurrentForegroundDwellSeconds); }
        catch { snap.MarkUnavailable(TriggerSignal.ForegroundDwellSeconds); }

        try { snap.SetNumber(TriggerSignal.IsFullscreen, _lastIsFullscreen ? 1 : 0); }
        catch { snap.MarkUnavailable(TriggerSignal.IsFullscreen); }

        // ---- 击键节拍：以分片增量累积，避免把分片计数当分钟数 ----
        FeedCounter(TriggerSignal.KeystrokesPerMinute, _sliceKeystrokes, now, 60.0);
        FeedCounter(TriggerSignal.BackspacePerMinute, _sliceBackspaces, now, 60.0);
        FeedCounter(TriggerSignal.WheelPerMinute, _sliceWheelRawNotches, now, 60.0);
        FeedCounter(TriggerSignal.MousePixelsPerMinute, _sliceMouseMoveDistance, now, 60.0);

        TryPutSum(TriggerSignal.KeystrokesPerMinute, snap, now);
        TryPutSum(TriggerSignal.BackspacePerMinute, snap, now);
        TryPutSum(TriggerSignal.WheelPerMinute, snap, now);
        TryPutSum(TriggerSignal.MousePixelsPerMinute, snap, now);

        try { snap.SetNumber(TriggerSignal.ClickBurstCount, _clickBurstCount); }
        catch { snap.MarkUnavailable(TriggerSignal.ClickBurstCount); }

        try { snap.SetNumber(TriggerSignal.RageShakeCount, _rageReversalCount); }
        catch { snap.MarkUnavailable(TriggerSignal.RageShakeCount); }

        // ---- 声音与媒体 ----
        try { snap.SetNumber(TriggerSignal.AudioPeak, Math.Round(_lastPeak, 2)); }
        catch { snap.MarkUnavailable(TriggerSignal.AudioPeak); }

        try { snap.SetNumber(TriggerSignal.IsAudioPlaying, _isAudioPlaying ? 1 : 0); }
        catch { snap.MarkUnavailable(TriggerSignal.IsAudioPlaying); }

        try
        {
            if (!string.IsNullOrEmpty(_activeAudioProcessesSummary))
                snap.SetText(TriggerSignal.AudioProcess, _activeAudioProcessesSummary);
            else
                snap.MarkUnavailable(TriggerSignal.AudioProcess);
        }
        catch { snap.MarkUnavailable(TriggerSignal.AudioProcess); }

        try
        {
            if (!string.IsNullOrEmpty(_lastAudioDeviceFriendlyName))
                snap.SetNumber(TriggerSignal.IsHeadphone,
                    DeviceDetectorNative.IsHeadphoneDevice(_lastAudioDeviceFriendlyName) ? 1 : 0);
            else
                snap.MarkUnavailable(TriggerSignal.IsHeadphone);
        }
        catch { snap.MarkUnavailable(TriggerSignal.IsHeadphone); }

        try
        {
            if (!string.IsNullOrEmpty(_currentMedia)) snap.SetText(TriggerSignal.MediaTitle, _currentMedia);
            else snap.MarkUnavailable(TriggerSignal.MediaTitle);
        }
        catch { snap.MarkUnavailable(TriggerSignal.MediaTitle); }

        // ---- 摄像头体征（内置采集，未启用或不可用时不提供该信号）----
        try
        {
            var cam = _camera;
            if (cam != null)
            {
                if (cam.Available)
                {
                    snap.SetNumber(TriggerSignal.CamAvailable, 1);
                    snap.SetNumber(TriggerSignal.CamPresent, cam.IsPresent() ? 1 : 0);
                    snap.SetNumber(TriggerSignal.CamFaceCount, cam.FaceCount);
                    if (cam.HasFace)
                    {
                        snap.SetNumber(TriggerSignal.CamYaw, cam.Yaw);
                        snap.SetNumber(TriggerSignal.CamPitch, cam.Pitch);
                    }
                    else
                    {
                        snap.MarkUnavailable(TriggerSignal.CamYaw);
                        snap.MarkUnavailable(TriggerSignal.CamPitch);
                    }
                    snap.SetNumber(TriggerSignal.CamAbsentSeconds, Math.Round(cam.AbsentSeconds(), 1));
                }
                else
                {
                    snap.SetNumber(TriggerSignal.CamAvailable, 0);
                    snap.MarkUnavailable(TriggerSignal.CamPresent);
                    snap.MarkUnavailable(TriggerSignal.CamFaceCount);
                    snap.MarkUnavailable(TriggerSignal.CamYaw);
                    snap.MarkUnavailable(TriggerSignal.CamPitch);
                    snap.MarkUnavailable(TriggerSignal.CamAbsentSeconds);
                }
            }
            else
            {
                snap.MarkUnavailable(TriggerSignal.CamAvailable);
                snap.MarkUnavailable(TriggerSignal.CamPresent);
                snap.MarkUnavailable(TriggerSignal.CamFaceCount);
                snap.MarkUnavailable(TriggerSignal.CamYaw);
                snap.MarkUnavailable(TriggerSignal.CamPitch);
                snap.MarkUnavailable(TriggerSignal.CamAbsentSeconds);
            }
        }
        catch
        {
            snap.MarkUnavailable(TriggerSignal.CamPresent);
            snap.MarkUnavailable(TriggerSignal.CamAvailable);
        }

        // ---- 今日画像 ----
        try
        {
            var profile = _storage.GetDailyProfile("today", _config.ProfileIgnoreProcesses);
            if (profile != null)
            {
                snap.SetNumber(TriggerSignal.TodayActiveMinutes, Math.Round(profile.TotalActiveSeconds / 60.0, 1));
                snap.SetNumber(TriggerSignal.TodayFullscreenMinutes, Math.Round(profile.TotalFullscreenSeconds / 60.0, 1));

                var files = profile.RecentLandedFiles;
                if (files != null && files.Count > 0)
                {
                    string last = files[^1];
                    string fname = StripLandedPrefix(last);
                    snap.SetText(TriggerSignal.LastFileName, fname);
                    if (!string.IsNullOrEmpty(_prevTriggerFile) && !string.Equals(_prevTriggerFile, last, StringComparison.Ordinal))
                    {
                        snap.SetEvent(TriggerSignal.FileDropped, fname);
                    }
                    _prevTriggerFile = last;
                    string ext = Path.GetExtension(last);
                    snap.SetText(TriggerSignal.LastFileExt, string.IsNullOrEmpty(ext) ? "" : ext.TrimStart('.'));
                }
                else
                {
                    snap.MarkUnavailable(TriggerSignal.LastFileName);
                    snap.MarkUnavailable(TriggerSignal.LastFileExt);
                }
            }
            else
            {
                snap.MarkUnavailable(TriggerSignal.TodayActiveMinutes);
                snap.MarkUnavailable(TriggerSignal.TodayFullscreenMinutes);
            }
        }
        catch
        {
            snap.MarkUnavailable(TriggerSignal.TodayActiveMinutes);
            snap.MarkUnavailable(TriggerSignal.TodayFullscreenMinutes);
            snap.MarkUnavailable(TriggerSignal.LastFileName);
            snap.MarkUnavailable(TriggerSignal.LastFileExt);
        }

        // ---- 连续工作 ----
        try
        {
            double cw = _continuousWorkStartTime == DateTime.MinValue
                ? 0
                : (now - _continuousWorkStartTime).TotalMinutes;
            snap.SetNumber(TriggerSignal.ContinuousWorkMinutes, Math.Round(Math.Max(0, cw), 1));
        }
        catch { snap.MarkUnavailable(TriggerSignal.ContinuousWorkMinutes); }

        // ---- 剪贴板：需协议端消息泵配合，一期标记不可用，二期接 Clipboard 服务 ----
        snap.MarkUnavailable(TriggerSignal.ClipboardLength);
        snap.MarkUnavailable(TriggerSignal.ClipboardText);

        // ---- 时间 ----
        snap.SetNumber(TriggerSignal.MinuteOfDay, now.Hour * 60 + now.Minute);
        snap.SetNumber(TriggerSignal.DayOfWeek, (int)now.DayOfWeek == 0 ? 7 : (int)now.DayOfWeek);

        // 把外部通知的事件（锁屏、解锁、唤醒、外设插拔、音频端点切换）搬进本轮快照，读完即清空
        if (_eventCounters.Count > 0)
        {
            foreach (var kv in _eventCounters)
            {
                if (kv.Value > 0) snap.SetEvent(kv.Key, "");
            }
            _eventCounters.Clear();
        }

        return snap;
    }

    /// <summary>
    /// 把引擎的分片计数换算为每分钟速率增量，压入统一的平滑窗口。
    /// 分片计数会在切片结束时归零，这里用下降沿识别新切片，累加本秒真实增量。
    /// </summary>
    private void FeedCounter(TriggerSignal signal, double currentSliceValue, DateTime now, double scale)
    {
        double last = _lastRawCounter.TryGetValue(signal, out var v) ? v : 0;
        double delta = currentSliceValue >= last ? currentSliceValue - last : currentSliceValue;
        if (delta < 0) delta = 0;
        _lastRawCounter[signal] = currentSliceValue;

        // delta 即本秒新增量；Sum 模式在 60 秒窗口内累加，结果天然就是每分钟速率
        _triggerSmoother.Push(signal, delta, now);
    }

    private void TryPutSum(TriggerSignal signal, TriggerSignalSnapshot snap, DateTime now)
    {
        if (_triggerSmoother.TryGet(signal, now, out double value) && value > 0)
            snap.SetNumber(signal, Math.Round(value, 2));
        else
            snap.SetNumber(signal, 0);
    }

    /// <summary>记录一次事件型信号的发生，供时间窗口内计数。</summary>
    public void NoteTriggerEvent(TriggerSignal signal, double amount = 1)
    {
        _eventCounters[signal] = (_eventCounters.TryGetValue(signal, out var v) ? v : 0) + amount;
    }
}


/// <summary>
/// 按角色分文件的规则存储。每角色一份 JSON，写入走临时文件加原子替换，避免断电写坏。
/// </summary>
public static class TriggerRuleStore
{
    private static readonly object _lock = new();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>规则根目录：Storage\BlackBox\triggers</summary>
    public static string RootDir =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Storage", "BlackBox", "triggers");

    /// <summary>把角色名净化为合法文件名，杜绝路径穿越与非法字符。</summary>
    public static string SanitizeRoleName(string? roleName)
    {
        if (string.IsNullOrWhiteSpace(roleName)) return "unknown";
        var sb = new System.Text.StringBuilder();
        foreach (char c in roleName.Trim())
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(c);
            else if (c == '.' || c == '·') sb.Append('_');
            else if (char.IsWhiteSpace(c)) sb.Append('_');
        }
        string s = sb.ToString();
        if (s.Length == 0) s = "unknown";
        if (s.Length > 64) s = s[..64];
        return s;
    }

    public static string GetRoleFilePath(string? roleName)
        => Path.Combine(RootDir, SanitizeRoleName(roleName) + ".json");

    /// <summary>读取单个角色的规则文件，不存在或损坏返回空列表。</summary>
    public static List<TriggerRule> LoadRole(string? roleName)
    {
        string path = GetRoleFilePath(roleName);
        lock (_lock)
        {
            try
            {
                if (!File.Exists(path)) return new List<TriggerRule>();
                string json = File.ReadAllText(path, System.Text.Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return new List<TriggerRule>();
                var list = JsonSerializer.Deserialize<List<TriggerRule>>(json, _jsonOptions) ?? new List<TriggerRule>();
                foreach (var rule in list)
                {
                    if (string.IsNullOrEmpty(rule.OwnerRole)) rule.OwnerRole = roleName ?? "unknown";
                }
                return list;
            }
            catch
            {
                return new List<TriggerRule>();
            }
        }
    }

    /// <summary>原子写入单个角色的规则文件。</summary>
    public static bool SaveRole(string? roleName, List<TriggerRule> rules)
    {
        string path = GetRoleFilePath(roleName);
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(RootDir);
                string json = JsonSerializer.Serialize(rules ?? new List<TriggerRule>(), _jsonOptions);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>扫描目录，加载全部角色的规则，键为角色名。</summary>
    public static Dictionary<string, List<TriggerRule>> LoadAll()
    {
        var result = new Dictionary<string, List<TriggerRule>>(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            try
            {
                if (!Directory.Exists(RootDir)) return result;
                foreach (string file in Directory.GetFiles(RootDir, "*.json"))
                {
                    try
                    {
                        string json = File.ReadAllText(file, System.Text.Encoding.UTF8);
                        var list = JsonSerializer.Deserialize<List<TriggerRule>>(json, _jsonOptions);
                        if (list == null) continue;
                        string role = Path.GetFileNameWithoutExtension(file);
                        foreach (var rule in list)
                        {
                            if (string.IsNullOrEmpty(rule.OwnerRole)) rule.OwnerRole = role;
                        }
                        result[role] = list;
                    }
                    catch
                    {
                        // 单文件损坏不影响其它角色
                    }
                }
            }
            catch
            {
            }
        }
        return result;
    }

    /// <summary>删除某个角色的全部规则文件。</summary>
    public static bool DeleteRole(string? roleName)
    {
        string path = GetRoleFilePath(roleName);
        lock (_lock)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return true;
            }
            catch { return false; }
        }
    }
}


public partial class BlackBoxEngine
{
    /// <summary>持续型条件的计时状态，键为 规则Id#条件序号</summary>
    private readonly Dictionary<string, (DateTime Start, DateTime LastSatisfied)> _conditionTimers = new();

    /// <summary>评估单条规则。调用方需保证 snapshot 为同一时刻采集。</summary>
    public TriggerEvalResult EvaluateRule(TriggerRule rule, TriggerSignalSnapshot snap, DateTime now, out string detail)
    {
        detail = "";
        if (rule == null) return TriggerEvalResult.Inactive;
        if (!rule.IsAlive(now))
        {
            detail = "规则未启用、已暂停或已过期";
            return TriggerEvalResult.Inactive;
        }

        if (rule.Conditions == null || rule.Conditions.Count == 0)
        {
            detail = "规则没有任何条件";
            return TriggerEvalResult.Miss;
        }

        var misses = new List<string>();
        var lines = new List<string>();
        bool anyUnavailable = false;
        bool allReached = true;

        for (int i = 0; i < rule.Conditions.Count; i++)
        {
            var cond = rule.Conditions[i];
            if (cond == null) continue;

            string key = rule.Id + "#" + i;
            bool satisfied = CheckCondition(cond, snap, out string desc, out bool unavailable);

            if (unavailable)
            {
                anyUnavailable = true;
                allReached = false;
                misses.Add(desc);
                lines.Add(desc);
                _conditionTimers.Remove(key);
                continue;
            }

            if (cond.DurationSeconds <= 0)
            {
                if (!satisfied)
                {
                    allReached = false;
                    misses.Add(desc);
                }
                lines.Add(desc);
                continue;
            }

            if (satisfied)
            {
                if (!_conditionTimers.TryGetValue(key, out var t))
                {
                    _conditionTimers[key] = (now, now);
                    allReached = false;
                    string d1 = desc + " 持续计时开始";
                    misses.Add(d1);
                    lines.Add(d1);
                }
                else
                {
                    _conditionTimers[key] = (t.Start, now);
                    double held = (now - t.Start).TotalSeconds;
                    if (held + 1e-9 < cond.DurationSeconds)
                    {
                        allReached = false;
                        string d2 = desc + " 已持续 " + held.ToString("F0") + " 秒，需 " + cond.DurationSeconds.ToString("F0") + " 秒";
                        misses.Add(d2);
                        lines.Add(d2);
                    }
                    else
                    {
                        lines.Add(desc + " 已持续 " + held.ToString("F0") + " 秒，满足时长要求");
                    }
                }
            }
            else
            {
                if (_conditionTimers.TryGetValue(key, out var t))
                {
                    double sinceOk = (now - t.LastSatisfied).TotalSeconds;
                    if (sinceOk > Math.Max(0, cond.GraceSeconds))
                    {
                        _conditionTimers.Remove(key);
                        string d3 = desc + " 中断超过宽限，计时清零";
                        misses.Add(d3);
                        lines.Add(d3);
                    }
                    else
                    {
                        string d4 = desc + " 短暂波动，宽限内保留计时";
                        misses.Add(d4);
                        lines.Add(d4);
                    }
                }
                else
                {
                    misses.Add(desc);
                    lines.Add(desc);
                }
                allReached = false;
            }
        }

        string joined = string.Join("；", misses);
        string lineText = string.Join("\n    ", lines);
        if (allReached)
        {
            detail = "全部条件满足：" + lineText;
            return TriggerEvalResult.Fired;
        }

        if (anyUnavailable && misses.Count == rule.Conditions.Count)
        {
            detail = "信号不可用：" + joined;
            return TriggerEvalResult.Unavailable;
        }

        detail = lineText;
        return misses.Exists(m => m.Contains("需 ") || m.Contains("持续计时")) ? TriggerEvalResult.Pending : TriggerEvalResult.Miss;
    }

    /// <summary>比对单个条件，返回是否满足。</summary>
    private bool CheckCondition(TriggerCondition cond, TriggerSignalSnapshot snap,
        out string desc, out bool unavailable)
    {
        unavailable = false;

        // 外部插件注册的动态信号优先解析，命中则不进入内置枚举分支
        if (BlackBoxSignalRegistry.TryGet(cond.Signal, out var dynEntry))
        {
            return CheckDynamicSignal(dynEntry, cond, out desc, out unavailable);
        }

        var signal = cond.SignalEnum;
        var op = cond.OperatorEnum;
        string label = signal.ToString();

        if (IsEventSignal(signal))
        {
            bool has = snap.HasEvent(signal);
            desc = label + (has ? " 本轮发生" : " 本轮未发生");
            return has;
        }

        // 文本型信号
        if (IsTextSignal(signal))
        {
            if (!snap.TryGetText(signal, out string text))
            {
                unavailable = true;
                desc = label + " 当前不可用";
                return false;
            }

            string expect = cond.TextValue ?? string.Empty;
            bool ok;
            switch (op)
            {
                case TriggerOperator.Contains:
                    ok = text.Contains(expect, StringComparison.OrdinalIgnoreCase);
                    break;
                case TriggerOperator.NotEqual:
                    ok = !text.Equals(expect, StringComparison.OrdinalIgnoreCase);
                    break;
                case TriggerOperator.Equal:
                default:
                    ok = text.Equals(expect, StringComparison.OrdinalIgnoreCase);
                    break;
            }

            desc = label + " 当前为 " + Truncate(text, 30) + "，判定 " + (ok ? "满足" : "不满足");
            return ok;
        }

        // 数值型信号
        if (!snap.TryGetNumber(signal, out double value))
        {
            unavailable = true;
            desc = label + " 当前不可用";
            return false;
        }

        double target = cond.Value;
        bool hit;
        switch (op)
        {
            case TriggerOperator.Greater: hit = value > target + 1e-9; break;
            case TriggerOperator.Less: hit = value < target - 1e-9; break;
            case TriggerOperator.GreaterOrEqual: hit = value >= target - 1e-9; break;
            case TriggerOperator.LessOrEqual: hit = value <= target + 1e-9; break;
            case TriggerOperator.Equal: hit = Math.Abs(value - target) <= 1e-6; break;
            case TriggerOperator.NotEqual: hit = Math.Abs(value - target) > 1e-6; break;
            case TriggerOperator.Contains:
                hit = value.ToString("F2").Contains(target.ToString("F2"), StringComparison.OrdinalIgnoreCase);
                break;
            default: hit = false; break;
        }

        desc = label + " 当前 " + value.ToString("F2") + "，阈值 " + target.ToString("F2") + " " + op + "，" + (hit ? "满足" : "不满足");
        return hit;
    }

    /// <summary> 评估一条外部插件注册的动态信号 /<summary>
    private bool CheckDynamicSignal(BlackBoxSignalRegistry.Entry entry, TriggerCondition cond,
        out string desc, out bool unavailable)
    {
        unavailable = false;
        var op = cond.OperatorEnum;

        if (entry.Kind == BlackBoxSignalRegistry.SignalKind.Text)
        {
            string text = null;
            try { if (entry.TextGetter != null) text = entry.TextGetter(); }
            catch { text = null; }

            if (text == null)
            {
                unavailable = true;
                desc = entry.Name + " 当前不可用";
                return false;
            }

            string expect = cond.TextValue ?? string.Empty;
            bool ok;
            switch (op)
            {
                case TriggerOperator.Contains:
                    ok = text.Contains(expect, StringComparison.OrdinalIgnoreCase);
                    break;
                case TriggerOperator.NotEqual:
                    ok = !text.Equals(expect, StringComparison.OrdinalIgnoreCase);
                    break;
                default:
                    ok = text.Equals(expect, StringComparison.OrdinalIgnoreCase);
                    break;
            }

            desc = entry.Name + " 当前为 " + Truncate(text, 30) + "，判定 " + (ok ? "满足" : "不满足");
            return ok;
        }

        double value = double.NaN;
        try { if (entry.NumberGetter != null) value = entry.NumberGetter(); }
        catch { value = double.NaN; }

        if (double.IsNaN(value))
        {
            unavailable = true;
            desc = entry.Name + " 当前不可用";
            return false;
        }

        double target = cond.Value;
        bool hit;
        switch (op)
        {
            case TriggerOperator.Greater: hit = value > target + 1e-9; break;
            case TriggerOperator.Less: hit = value < target - 1e-9; break;
            case TriggerOperator.GreaterOrEqual: hit = value >= target - 1e-9; break;
            case TriggerOperator.LessOrEqual: hit = value <= target + 1e-9; break;
            case TriggerOperator.Equal: hit = Math.Abs(value - target) <= 1e-6; break;
            case TriggerOperator.NotEqual: hit = Math.Abs(value - target) > 1e-6; break;
            case TriggerOperator.Contains:
                hit = value.ToString("F2").Contains(target.ToString("F2"), StringComparison.OrdinalIgnoreCase);
                break;
            default: hit = false; break;
        }

        desc = entry.Name + " 当前 " + value.ToString("F2") + "，阈值 " + target.ToString("F2") + " " + op + "，" + (hit ? "满足" : "不满足");
        return hit;
    }


    /// <summary>判断信号是否为文本型。</summary>
    public static bool IsTextSignal(TriggerSignal signal) => signal switch
    {
        TriggerSignal.ForegroundApp => true,
        TriggerSignal.ForegroundTitle => true,
        TriggerSignal.AudioProcess => true,
        TriggerSignal.MediaTitle => true,
        TriggerSignal.ClipboardText => true,
        TriggerSignal.LastFileName => true,
        TriggerSignal.LastFileExt => true,
        _ => false
    };

    /// 判断信号是否为事件型（发生即命中，不比阈值）
    public static bool IsEventSignal(TriggerSignal signal) => (int)signal >= (int)TriggerSignal.AppOpened;

    /// 把落盘记录条目还原成纯文件名。条目形如 方括号时间 文件名 圆括号大小
    private static string StripLandedPrefix(string entry)
    {
        if (string.IsNullOrEmpty(entry)) return "";
        string t = entry.Trim();
        if (t.StartsWith("[") && t.IndexOf("]") > 0)
        {
            t = t.Substring(t.IndexOf("]") + 1).Trim();
        }
        int p = t.LastIndexOf(" (");
        if (p > 0) t = t.Substring(0, p).Trim();
        return t;
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "...");

    /// <summary>清理已不存在规则的计时状态，防止字典膨胀。</summary>
    public void PruneConditionTimers(IEnumerable<TriggerRule> aliveRules)
    {
        var aliveIds = new HashSet<string>(aliveRules.Select(r => r.Id));
        var stale = _conditionTimers.Keys
            .Where(k => !aliveIds.Contains(k.Split('#')[0]))
            .ToList();
        foreach (var k in stale) _conditionTimers.Remove(k);
    }
}


public partial class BlackBoxEngine
{
    /// <summary>两次 tick 间隔超过该秒数，判定为休眠或系统卡顿</summary>
    public const int SuspendGapSeconds = 30;

    /// <summary>唤醒后的宽容期，期间一律不评估不触发，杜绝补触发轰炸</summary>
    public const int ResumeGraceSeconds = 60;

    private DateTime _guardLastTickTime = DateTime.MinValue;
    private DateTime _guardResumeUntil = DateTime.MinValue;
    private DateTime _guardLastGapReported = DateTime.MinValue;

    /// <summary>当前是否处于唤宽容窗口内，窗口内禁止任何触发。</summary>
    public bool IsInGuardWindow(DateTime now) => now < _guardResumeUntil;

    /// <summary>
    /// 每次评估前调用。负责识别断档（休眠、锁屏、进程卡死），并自动进入宽容期。
    /// </summary>
    public void UpdateTriggerGuard(DateTime now)
    {
        if (_guardLastTickTime != DateTime.MinValue)
        {
            double gap = (now - _guardLastTickTime).TotalSeconds;
            if (gap > SuspendGapSeconds)
            {
                EnterGuardWindow(now, "检测到 " + gap.ToString("F0") + " 秒的评估断档，判定为休眠或系统挂起");
            }
        }
        _guardLastTickTime = now;
    }

    /// <summary>系统唤醒或锁屏解除时主动调用，立即进入宽容期并清零持续计时。</summary>
    public void NotifySystemResumed(string reason)
    {
        DateTime now = DateTime.Now;
        _guardLastTickTime = now;
        EnterGuardWindow(now, string.IsNullOrEmpty(reason) ? "系统唤醒" : reason);
    }

    private void EnterGuardWindow(DateTime now, string reason)
    {
        // 持续型计时一律归零，绝不跨越休眠补算
        _conditionTimers.Clear();
        _triggerSmoother.Reset();
        _lastRawCounter.Clear();

        _guardResumeUntil = now.AddSeconds(ResumeGraceSeconds);

        // 同一分钟内的断档只记一条，避免刷屏
        if ((now - _guardLastGapReported).TotalSeconds < 60) return;
        _guardLastGapReported = now;

        try
        {
            _storage.AddTransientEvent(new BlackBoxEvent
            {
                Time = now,
                Category = "Trigger",
                Summary = "自定义触发器进入宽容期，" + ResumeGraceSeconds + " 秒内不评估不触发",
                Details = reason,
                DurationSeconds = ResumeGraceSeconds
            }, _config.MaxTransientEventsCount);
        }
        catch
        {
        }
    }

    /// <summary>
    /// 进程启动或规则重新加载后调用，确保计时状态从零开始，不继承任何历史。
    /// </summary>
    public void ResetTriggerRuntimeState()
    {
        _conditionTimers.Clear();
        _triggerSmoother.Reset();
        _lastRawCounter.Clear();
        _eventCounters.Clear();
        _guardLastTickTime = DateTime.MinValue;
        _guardResumeUntil = DateTime.MinValue;
    }
}


/// <summary>一次仲裁命中的结果。</summary>
public sealed class TriggerFireCandidate
{
    public TriggerRule Rule { get; init; } = new();
    public string Prompt { get; init; } = "";
    public TriggerEvalResult Result { get; set; } = TriggerEvalResult.Fired;
    public string Reason { get; set; } = "";
}

public partial class BlackBoxEngine
{
    /// <summary>全局最小播报间隔，同一时刻多规则命中时避免连环轰炸</summary>
    public int TriggerMinBroadcastSeconds { get; set; } = 15;

    /// <summary>是否把同秒命中的多条合并成一条播报</summary>
    public bool TriggerMergeMultiHit { get; set; } = false;

    private DateTime _lastTriggerBroadcastTime = DateTime.MinValue;

    /// <summary>
    /// 对同一时刻命中的候选做仲裁。返回真正应该播报的候选集合，其余标记为 Suppressed。
    /// </summary>
    public List<TriggerFireCandidate> ArbitrateTriggers(List<TriggerFireCandidate> hits, DateTime now)
    {
        var approved = new List<TriggerFireCandidate>();
        if (hits == null || hits.Count == 0) return approved;

        // 1. 冷却过滤
        foreach (var hit in hits)
        {
            var rule = hit.Rule;
            if (rule == null) { hit.Result = TriggerEvalResult.Suppressed; hit.Reason = "规则为空"; continue; }

            if (rule.LastFiredAt.HasValue)
            {
                double sinceLast = (now - rule.LastFiredAt.Value).TotalSeconds;
                if (sinceLast < Math.Max(0, rule.CooldownSeconds))
                {
                    hit.Result = TriggerEvalResult.Suppressed;
                    hit.Reason = "冷却中，还剩 " + Math.Ceiling(rule.CooldownSeconds - sinceLast).ToString("F0") + " 秒";
                }
            }
        }

        var ready = hits.Where(h => h.Result == TriggerEvalResult.Fired).ToList();
        if (ready.Count == 0) return approved;

        // 2. 全局最小播报间隔
        if (_lastTriggerBroadcastTime != DateTime.MinValue)
        {
            double sinceBroadcast = (now - _lastTriggerBroadcastTime).TotalSeconds;
            if (sinceBroadcast < TriggerMinBroadcastSeconds)
            {
                foreach (var hit in ready)
                {
                    hit.Result = TriggerEvalResult.Suppressed;
                    hit.Reason = "全局播报间隔未到，还剩 " +
                        Math.Ceiling(TriggerMinBroadcastSeconds - sinceBroadcast).ToString("F0") + " 秒";
                }
                return approved;
            }
        }

        // 3. 按优先级降序、已触发次数升序排序
        ready.Sort((a, b) =>
        {
            int p = b.Rule.Priority.CompareTo(a.Rule.Priority);
            if (p != 0) return p;
            return a.Rule.FireCount.CompareTo(b.Rule.FireCount);
        });

        if (TriggerMergeMultiHit)
        {
            approved.AddRange(ready);
        }
        else
        {
            approved.Add(ready[0]);
            for (int i = 1; i < ready.Count; i++)
            {
                ready[i].Result = TriggerEvalResult.Suppressed;
                ready[i].Reason = "同秒命中被高优先级规则占用播报窗口";
            }
        }

        if (approved.Count > 0) _lastTriggerBroadcastTime = now;
        return approved;
    }

    /// <summary>把多条候选的播报文本合并成一条，带序号，避免信息丢失。</summary>
    public static string MergeTriggerPrompts(List<TriggerFireCandidate> approved)
    {
        if (approved == null || approved.Count == 0) return "";
        if (approved.Count == 1) return approved[0].Prompt;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < approved.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(approved[i].Prompt);
        }
        return sb.ToString();
    }

    /// <summary>播报后统一结算，扣次数、记时间、写日志。</summary>
    public void CommitTriggerFire(TriggerFireCandidate candidate, DateTime now)
    {
        if (candidate?.Rule == null) return;
        candidate.Rule.ConsumeOnce(now);
    }
}


public partial class BlackBoxEngine
{
    /// <summary>触发日志总量上限，超出后淘汰最旧条目</summary>
    public int TriggerLogCapacity { get; set; } = 500;

    /// <summary>是否记录未命中的评估结果。默认关闭，避免日志被 Miss 淹没</summary>
    public bool TriggerLogVerbose { get; set; } = false;

    private readonly Queue<TriggerLogEntry> _triggerLogs = new();
    private readonly object _triggerLogLock = new();

    /// <summary>写入一条触发日志。</summary>
    public void AddTriggerLog(TriggerLogEntry entry)
    {
        if (entry == null) return;
        lock (_triggerLogLock)
        {
            _triggerLogs.Enqueue(entry);
            int cap = Math.Max(50, TriggerLogCapacity);
            while (_triggerLogs.Count > cap) _triggerLogs.Dequeue();
        }
    }

    /// <summary>便捷写入。result 用 TriggerEvalResult 的名字。</summary>
    public void AddTriggerLog(TriggerRule rule, TriggerEvalResult result, string detail)
    {
        if (rule == null) return;
        if (result == TriggerEvalResult.Miss && !TriggerLogVerbose) return;
        AddTriggerLog(new TriggerLogEntry
        {
            Time = DateTime.Now,
            RuleId = rule.Id,
            RuleName = rule.Name,
            OwnerRole = rule.OwnerRole,
            Result = result.ToString(),
            Detail = detail ?? ""
        });
    }

    /// <summary>按角色查询最近日志，倒序返回，最新的在前。</summary>
    public List<TriggerLogEntry> QueryTriggerLogs(string? roleName, int count = 20)
    {
        count = Math.Clamp(count, 1, 200);
        lock (_triggerLogLock)
        {
            IEnumerable<TriggerLogEntry> src = _triggerLogs;
            if (!string.IsNullOrWhiteSpace(roleName))
            {
                src = src.Where(e => string.Equals(e.OwnerRole, roleName, StringComparison.OrdinalIgnoreCase));
            }
            return src.Reverse().Take(count).ToList();
        }
    }

    /// <summary>清空全部日志。</summary>
    public void ClearTriggerLogs()
    {
        lock (_triggerLogLock) _triggerLogs.Clear();
    }

    /// <summary>把快照中的关键信号压成一行摘要，写进日志方便复盘。</summary>
    public static string SummarizeSnapshot(TriggerSignalSnapshot snap)
    {
        if (snap == null) return "无快照";
        var sb = new System.Text.StringBuilder();
        void Num(TriggerSignal s)
        {
            if (snap.TryGetNumber(s, out double v)) sb.Append(s).Append('=').Append(v.ToString("F1")).Append(' ');
        }
        void Txt(TriggerSignal s)
        {
            if (snap.TryGetText(s, out string v)) sb.Append(s).Append('=').Append(Truncate(v, 16)).Append(' ');
        }

        Num(TriggerSignal.IdleSeconds);
        Txt(TriggerSignal.ForegroundApp);
        Num(TriggerSignal.IsFullscreen);
        Num(TriggerSignal.KeystrokesPerMinute);
        Num(TriggerSignal.ContinuousWorkMinutes);
        Num(TriggerSignal.AudioPeak);
        Num(TriggerSignal.MinuteOfDay);
        string s = sb.ToString().Trim();
        return string.IsNullOrEmpty(s) ? "无可用信号" : s;
    }
}


/// <summary>自定义触发派发事件：角色名、播报文本、派发方式。</summary>
public delegate void CustomTriggerDispatchHandler(string roleName, string prompt, string dispatchMode);

public partial class BlackBoxEngine
{
    /// <summary>自定义触发专用派发通道，按角色精确投递，绝不跨角色串台</summary>
    public static event CustomTriggerDispatchHandler? OnCustomTriggerDispatch;

    /// <summary>已注册角色的规则表</summary>
    private readonly Dictionary<string, List<TriggerRule>> _roleRules = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>已注册角色的开关查询委托</summary>
    private readonly Dictionary<string, Func<bool>> _roleSwitches = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>已注册角色的合并开关与间隔配置</summary>
    private readonly Dictionary<string, (bool Merge, int MinInterval)> _roleBroadcast = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _roleRulesLock = new();

    /// <summary>角色注册进触发器系统：加载规则、绑定开关。</summary>
    public void RegisterTriggerRole(string roleName, Func<bool>? isEnabled, bool mergeMultiHit, int minIntervalSeconds)
    {
        if (string.IsNullOrWhiteSpace(roleName)) return;
        lock (_roleRulesLock)
        {
            var rules = TriggerRuleStore.LoadRole(roleName);
            foreach (var r in rules)
            {
                if (string.IsNullOrEmpty(r.OwnerRole)) r.OwnerRole = roleName;
            }
            _roleRules[roleName] = rules;
            _roleSwitches[roleName] = isEnabled ?? (() => true);
            _roleBroadcast[roleName] = (mergeMultiHit, Math.Max(0, minIntervalSeconds));
        }
    }

    /// <summary>角色注销，规则与开关一并摘除。</summary>
    public void UnregisterTriggerRole(string roleName)
    {
        if (string.IsNullOrWhiteSpace(roleName)) return;
        lock (_roleRulesLock)
        {
            _roleRules.Remove(roleName);
            _roleSwitches.Remove(roleName);
            _roleBroadcast.Remove(roleName);
        }
    }

    /// <summary>取某个角色的规则快照（副本），供查询与编辑。</summary>
    public List<TriggerRule> GetRoleRules(string roleName)
    {
        lock (_roleRulesLock)
        {
            return _roleRules.TryGetValue(roleName, out var list)
                ? list.Select(CloneRule).ToList()
                : new List<TriggerRule>();
        }
    }

    /// <summary>整体替换某个角色的规则并落盘。</summary>
    public bool SetRoleRules(string roleName, List<TriggerRule> rules)
    {
        if (string.IsNullOrWhiteSpace(roleName)) return false;
        lock (_roleRulesLock)
        {
            foreach (var r in rules) r.OwnerRole = roleName;
            _roleRules[roleName] = rules;
        }
        return TriggerRuleStore.SaveRole(roleName, rules);
    }

    private static TriggerRule CloneRule(TriggerRule r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        OwnerRole = r.OwnerRole,
        Conditions = r.Conditions.Select(c => c.Clone()).ToList(),
        Prompt = r.Prompt,
        DispatchMode = r.DispatchMode,
        Lifecycle = r.Lifecycle,
        MaxTimes = r.MaxTimes,
        RemainingTimes = r.RemainingTimes,
        CooldownSeconds = r.CooldownSeconds,
        Enabled = r.Enabled,
        Paused = r.Paused,
        CreatedBy = r.CreatedBy,
        CreatedAt = r.CreatedAt,
        ExpireAt = r.ExpireAt,
        LastFiredAt = r.LastFiredAt,
        FireCount = r.FireCount,
        Priority = r.Priority,
        DailyMaxTimes = r.DailyMaxTimes,
        TodayFireCount = r.TodayFireCount,
        TodayDateKey = r.TodayDateKey
    };

    /// <summary>
    /// 主循环每秒调用：采集快照，逐角色评估、仲裁、派发。
    /// </summary>
    public void TickTriggers(DateTime now)
    {
        UpdateTriggerGuard(now);
        if (IsInGuardWindow(now)) return;

        List<string> roles;
        lock (_roleRulesLock)
        {
            if (_roleRules.Count == 0) return;
            roles = _roleRules.Keys.ToList();
        }

        TriggerSignalSnapshot snap = CollectSnapshot(now);

        foreach (string role in roles)
        {
            Func<bool>? enabledCheck;
            List<TriggerRule> rules;
            (bool Merge, int MinInterval) bc;
            lock (_roleRulesLock)
            {
                if (!_roleRules.TryGetValue(role, out var list) || list.Count == 0) continue;
                if (!_roleSwitches.TryGetValue(role, out enabledCheck)) continue;
                rules = list;
                bc = _roleBroadcast.TryGetValue(role, out var b) ? b : (false, 15);
            }

            try
            {
                if (enabledCheck != null && !enabledCheck()) continue;
            }
            catch { continue; }

            var hits = new List<TriggerFireCandidate>();
            bool mutated = false;

            foreach (var rule in rules)
            {
                if (rule == null) continue;

                // 次数耗尽或到期，自动清理
                if (!rule.IsAlive(now))
                {
                    if (rule.LifecycleEnum == TriggerLifecycle.Once || rule.LifecycleEnum == TriggerLifecycle.Count)
                    {
                        if (rule.RemainingTimes == 0) rule.Paused = true;
                    }
                    continue;
                }

                var result = EvaluateRule(rule, snap, now, out string detail);
                AddTriggerLog(rule, result, detail);

                if (result == TriggerEvalResult.Fired)
                {
                    string prompt = RenderTriggerPrompt(rule, snap, now);
                    hits.Add(new TriggerFireCandidate { Rule = rule, Prompt = prompt });
                }
            }

            if (hits.Count == 0) continue;

            int minInterval = bc.MinInterval;
            var savedMin = TriggerMinBroadcastSeconds;
            var savedMerge = TriggerMergeMultiHit;
            TriggerMinBroadcastSeconds = minInterval;
            TriggerMergeMultiHit = bc.Merge;

            List<TriggerFireCandidate> approved;
            try
            {
                approved = ArbitrateTriggers(hits, now);
            }
            finally
            {
                TriggerMinBroadcastSeconds = savedMin;
                TriggerMergeMultiHit = savedMerge;
            }

            if (approved.Count == 0) continue;

            string merged = MergeTriggerPrompts(approved);
            string mode = approved[0].Rule.DispatchMode ?? "Poke";

            foreach (var a in approved)
            {
                CommitTriggerFire(a, now);
                mutated = true;
            }

            if (!string.IsNullOrWhiteSpace(merged))
            {
                try
                {
                    OnCustomTriggerDispatch?.Invoke(role, merged, mode);
                }
                catch { }
            }

            if (mutated)
            {
                TriggerRuleStore.SaveRole(role, rules);
            }

            PruneConditionTimers(rules);
        }
    }

    /// <summary>渲染播报模板，支持常见宏置换。</summary>
    private string RenderTriggerPrompt(TriggerRule rule, TriggerSignalSnapshot snap, DateTime now)
    {
        string tpl = rule.Prompt ?? "";
        if (string.IsNullOrWhiteSpace(tpl)) tpl = rule.Name ?? "自定义触发";

        var sb = new System.Text.StringBuilder(tpl);
        sb.Replace("{RuleName}", rule.Name ?? "");
        sb.Replace("{CurrentTime}", now.ToString("HH:mm:ss"));
        sb.Replace("{Date}", now.ToString("yyyy-MM-dd"));

        if (snap.TryGetNumber(TriggerSignal.IdleSeconds, out double idle))
            sb.Replace("{IdleSeconds}", idle.ToString("F0"));
        if (snap.TryGetNumber(TriggerSignal.ContinuousWorkMinutes, out double cw))
            sb.Replace("{ContinuousWorkMinutes}", cw.ToString("F0"));
        if (snap.TryGetNumber(TriggerSignal.TodayActiveMinutes, out double ta))
            sb.Replace("{TodayActiveMinutes}", ta.ToString("F0"));
        if (snap.TryGetNumber(TriggerSignal.KeystrokesPerMinute, out double apm))
            sb.Replace("{KeystrokesPerMinute}", apm.ToString("F0"));
        if (snap.TryGetText(TriggerSignal.ForegroundApp, out string app))
            sb.Replace("{ProcessName}", app);
        if (snap.TryGetText(TriggerSignal.ForegroundTitle, out string title))
            sb.Replace("{WindowTitle}", title);
        if (snap.TryGetText(TriggerSignal.MediaTitle, out string media))
            sb.Replace("{MediaTitle}", media);

        return sb.ToString();
    }
}
