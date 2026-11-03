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
    // 键盘状态与打字节拍 (Module 2)
    private int _sliceKeystrokes = 0;
    private int _sliceBackspaces = 0;
    private DateTime _lastKeyboardSliceTime = DateTime.Now;

    public int CurrentMinuteKeystrokes => _sliceKeystrokes;
    public int CurrentMinuteBackspaces => _sliceBackspaces;

    // 一键录制快捷键支持
    private Action<string>? _keyRecordCallback;
    public bool IsRecordingKey => _keyRecordCallback != null;

    public void StartOneShotKeyRecording(Action<string> onRecorded)
    {
        _keyRecordCallback = onRecorded;
    }

    public void CancelKeyRecording()
    {
        _keyRecordCallback = null;
    }

    // 自定义按键/组合键监测状态
    private readonly object _keyWatchLock = new();
    private string _cachedKeySpec = string.Empty;
    private int _cachedDefaultCount = -1;
    private List<WatchedKeyRule> _watchedKeyRules = new();

    private sealed class WatchedKeyRule
    {
        public string DisplayName { get; init; } = string.Empty;
        public int VkCode { get; init; }
        public bool RequireCtrl { get; init; }
        public bool RequireShift { get; init; }
        public bool RequireAlt { get; init; }
        public int TargetCount { get; init; }
        public Queue<long> PressTicks { get; } = new();
        public long LastTriggerTick { get; set; } = 0;
    }

    private void OnRawKeyEvent(int msg, IntPtr lParam)
    {
        _lastHookInputTime = DateTime.Now;
        if (_isIdle && (msg == 0x0100 || msg == 0x0104)) // 物理按键毫秒边缘唤醒
        {
            CheckAndTriggerReturnFromIdle("键盘按键");
        }

        if (msg == 0x0100 || msg == 0x0104) // WM_KEYDOWN, WM_SYSKEYDOWN
        {
            int vkCode = 0;
            try
            {
                vkCode = Marshal.ReadInt32(lParam);
            }
            catch { }

            if (_keyRecordCallback != null && vkCode > 0)
            {
                // 忽略单纯按下修饰键本身(Ctrl/Shift/Alt/Win)，等待按下主键完成组合录制
                if (vkCode is not (0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C))
                {
                    bool cDown = (GetAsyncKeyState(0x11) & 0x8000) != 0 || (GetAsyncKeyState(0xA2) & 0x8000) != 0 || (GetAsyncKeyState(0xA3) & 0x8000) != 0;
                    bool sDown = (GetAsyncKeyState(0x10) & 0x8000) != 0 || (GetAsyncKeyState(0xA0) & 0x8000) != 0 || (GetAsyncKeyState(0xA1) & 0x8000) != 0;
                    bool aDown = (GetAsyncKeyState(0x12) & 0x8000) != 0 || (GetAsyncKeyState(0xA4) & 0x8000) != 0 || (GetAsyncKeyState(0xA5) & 0x8000) != 0;
                    string keyName = FormatVirtualKeyName(vkCode);
                    var parts = new List<string>();
                    if (cDown) parts.Add("Ctrl");
                    if (sDown) parts.Add("Shift");
                    if (aDown) parts.Add("Alt");
                    parts.Add(keyName);
                    string combo = string.Join("+", parts);
                    var cb = Interlocked.Exchange(ref _keyRecordCallback, null);
                    try { cb?.Invoke(combo); } catch { }
                    return;
                }
            }

            if (_config.EnableKeyboardWatch)
            {
                Interlocked.Increment(ref _sliceKeystrokes);
                if (vkCode == 0x08) // VK_BACK
                {
                    Interlocked.Increment(ref _sliceBackspaces);
                }
            }

            if (_config.EnableKeyWatchNotifyPrompt && vkCode > 0 && !string.IsNullOrWhiteSpace(_config.WatchedKeysSpec))
            {
                CheckCustomWatchedKeys(vkCode);
            }
        }
    }

    private void CheckCustomWatchedKeys(int vkCode)
    {
        try
        {
            // 若当前按下的仅是修饰键本身(Ctrl/Shift/Alt)，检查规则中是否有单修饰键；通常组合键在主键按下时触发
            bool ctrlDown = (GetAsyncKeyState(0x11) & 0x8000) != 0 || (GetAsyncKeyState(0xA2) & 0x8000) != 0 || (GetAsyncKeyState(0xA3) & 0x8000) != 0;
            bool shiftDown = (GetAsyncKeyState(0x10) & 0x8000) != 0 || (GetAsyncKeyState(0xA0) & 0x8000) != 0 || (GetAsyncKeyState(0xA1) & 0x8000) != 0;
            bool altDown = (GetAsyncKeyState(0x12) & 0x8000) != 0 || (GetAsyncKeyState(0xA4) & 0x8000) != 0 || (GetAsyncKeyState(0xA5) & 0x8000) != 0;

            long nowTick = Environment.TickCount64;
            long windowMs = Math.Max(1, _config.KeyWatchWindowSeconds) * 1000L;
            long cooldownMs = Math.Max(1, _config.KeyWatchCooldownSeconds) * 1000L;

            lock (_keyWatchLock)
            {
                EnsureWatchedKeyRulesLoaded();
                if (_watchedKeyRules.Count == 0) return;

                foreach (var rule in _watchedKeyRules)
                {
                    if (rule.VkCode != vkCode) continue;
                    if (rule.RequireCtrl != ctrlDown) continue;
                    if (rule.RequireShift != shiftDown) continue;
                    if (rule.RequireAlt != altDown) continue;

                    while (rule.PressTicks.Count > 0 && nowTick - rule.PressTicks.Peek() > windowMs)
                    {
                        rule.PressTicks.Dequeue();
                    }

                    rule.PressTicks.Enqueue(nowTick);

                    int targetCount = Math.Max(1, rule.TargetCount);
                    if (rule.PressTicks.Count >= targetCount && (rule.LastTriggerTick == 0 || nowTick - rule.LastTriggerTick >= cooldownMs))
                    {
                        int actualCount = rule.PressTicks.Count;
                        rule.LastTriggerTick = nowTick;
                        rule.PressTicks.Clear();

                        _storage.AddTransientEvent(new BlackBoxEvent
                        {
                            Time = DateTime.Now,
                            Category = "Keyboard",
                            Summary = $"自定义按键触发：[{rule.DisplayName}] 累计按压 {actualCount} 次",
                            Details = $"统计窗口: {_config.KeyWatchWindowSeconds}秒 | 所在应用: {_lastProcess} ({_lastTitle})",
                            DurationSeconds = _config.KeyWatchWindowSeconds
                        }, _config.MaxTransientEventsCount);

                        string tmpl = string.IsNullOrWhiteSpace(_config.KeyWatchPromptTemplate)
                            ? "检测到阿瞒在应用 [{ProcessName}] 中 {WindowSeconds}秒内按下监测按键 [{KeyName}] 达 {Count} 次，请根据当前场景主动关怀或响应。"
                            : _config.KeyWatchPromptTemplate;

                        string prompt = tmpl
                            .Replace("{KeyName}", rule.DisplayName)
                            .Replace("{Count}", actualCount.ToString())
                            .Replace("{WindowSeconds}", _config.KeyWatchWindowSeconds.ToString())
                            .Replace("{ProcessName}", _lastProcess)
                            .Replace("{WindowTitle}", _lastTitle);

                        DispatchNotification(prompt, false, BlackBoxNotificationType.KeyWatch);
                    }
                }
            }
        }
        catch { }
    }

    private void EnsureWatchedKeyRulesLoaded()
    {
        string spec = _config.WatchedKeysSpec ?? string.Empty;
        int defCount = Math.Max(1, _config.KeyWatchTriggerCount);
        if (spec == _cachedKeySpec && defCount == _cachedDefaultCount)
            return;

        _cachedKeySpec = spec;
        _cachedDefaultCount = defCount;
        _watchedKeyRules.Clear();

        var entries = spec.Split(new[] { ',', ';', '，', '|' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawEntry in entries)
        {
            string entry = rawEntry.Trim();
            if (string.IsNullOrEmpty(entry)) continue;

            int customCount = defCount;
            string keyPart = entry;
            int colonIdx = entry.LastIndexOf(':');
            if (colonIdx > 0 && colonIdx < entry.Length - 1)
            {
                if (int.TryParse(entry.Substring(colonIdx + 1).Trim(), out int parsedCount) && parsedCount >= 1)
                {
                    customCount = parsedCount;
                }
                keyPart = entry.Substring(0, colonIdx).Trim();
            }

            bool reqCtrl = false, reqShift = false, reqAlt = false;
            int mainVk = 0;
            var tokens = keyPart.Split('+', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToList();

            foreach (var tok in tokens)
            {
                if (tok.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || tok.Equals("Control", StringComparison.OrdinalIgnoreCase))
                {
                    reqCtrl = true;
                }
                else if (tok.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                {
                    reqShift = true;
                }
                else if (tok.Equals("Alt", StringComparison.OrdinalIgnoreCase) || tok.Equals("Menu", StringComparison.OrdinalIgnoreCase))
                {
                    reqAlt = true;
                }
                else
                {
                    mainVk = ParseVirtualKeyCode(tok);
                }
            }

            if (mainVk > 0)
            {
                _watchedKeyRules.Add(new WatchedKeyRule
                {
                    DisplayName = keyPart,
                    VkCode = mainVk,
                    RequireCtrl = reqCtrl,
                    RequireShift = reqShift,
                    RequireAlt = reqAlt,
                    TargetCount = customCount
                });
            }
        }
    }


    public static string FormatVirtualKeyName(int vk)
    {
        return vk switch
        {
            0x08 => "Back",
            0x09 => "Tab",
            0x0D => "Enter",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2D => "Insert",
            0x2E => "Delete",
            >= 0x30 and <= 0x39 => ((char)vk).ToString(),
            >= 0x41 and <= 0x5A => ((char)vk).ToString(),
            >= 0x70 and <= 0x87 => $"F{vk - 0x70 + 1}",
            _ => ((Keys)vk).ToString()
        };
    }

    private static int ParseVirtualKeyCode(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return 0;
        string t = token.Trim().ToUpperInvariant();

        return t switch
        {
            "BACK" or "BACKSPACE" or "BKSP" => 0x08,
            "TAB" => 0x09,
            "ENTER" or "RETURN" => 0x0D,
            "ESC" or "ESCAPE" => 0x1B,
            "SPACE" or "SPACEBAR" => 0x20,
            "PGUP" or "PAGEUP" => 0x21,
            "PGDN" or "PAGEDOWN" => 0x22,
            "END" => 0x23,
            "HOME" => 0x24,
            "LEFT" => 0x25,
            "UP" => 0x26,
            "RIGHT" => 0x27,
            "DOWN" => 0x28,
            "INS" or "INSERT" => 0x2D,
            "DEL" or "DELETE" => 0x2E,
            "WIN" or "LWIN" => 0x5B,
            "PLUS" or "=" => 0xBB,
            "MINUS" or "-" => 0xBD,
            "TILDE" or "`" or "~" => 0xC0,
            _ => ParseFallbackKeyCode(t)
        };
    }

    private static int ParseFallbackKeyCode(string t)
    {
        if (t.Length == 1)
        {
            char c = t[0];
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                return (int)c;
        }
        if (t.StartsWith("F") && int.TryParse(t.Substring(1), out int fNum) && fNum >= 1 && fNum <= 24)
        {
            return 0x70 + (fNum - 1);
        }
        if (Enum.TryParse<Keys>(t, true, out Keys k))
        {
            return (int)(k & Keys.KeyCode);
        }
        return 0;
    }

    private void CheckKeyboardSlice()
    {
        DateTime now = DateTime.Now;
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
    }
}
