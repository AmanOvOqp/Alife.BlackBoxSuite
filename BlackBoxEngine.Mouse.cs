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
    // 鼠标滚轮与鼠标动力学状态 (Module 3 & Module 11)
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

                        // 暴躁甩鼠标 (Rage Shake) 检测：速度 > 1600 px/s 且主运动轴高频往复反转 (抗微颤滤波)
                        double speedPxPerSec = (dist * 1000.0) / dt;
                        if (speedPxPerSec >= 1600.0)
                        {
                            // 死区提高至 25px，杜绝传感器底噪与手部生理颤动
                            int dirX = Math.Abs(dx) >= 25 ? Math.Sign(dx) : 0;
                            int dirY = Math.Abs(dy) >= 25 ? Math.Sign(dy) : 0;

                            // 锁定主轴（X轴与Y轴谁的位移显著就只判定谁，杜绝双轴颤动交叉累加）
                            bool reversed = false;
                            if (Math.Abs(dx) >= Math.Abs(dy) * 1.2 && dirX != 0)
                            {
                                if (_lastFastDirX != 0 && dirX == -_lastFastDirX) reversed = true;
                            }
                            else if (Math.Abs(dy) >= Math.Abs(dx) * 1.2 && dirY != 0)
                            {
                                if (_lastFastDirY != 0 && dirY == -_lastFastDirY) reversed = true;
                            }
                            else if (dirX != 0 && _lastFastDirX != 0 && dirX == -_lastFastDirX)
                            {
                                reversed = true;
                            }

                            if (reversed)
                            {
                                if (nowTick - _rageWindowStartTick > 1500)
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
                                                Summary = $"鼠标动力学：检测到暴躁甩鼠标体征 (1.5秒内高速往复反转 {revCount} 次)",
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
                                                DispatchNotification(prompt, false, BlackBoxNotificationType.RageShake);
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

    private void CheckMouseSlice()
    {
        DateTime now = DateTime.Now;
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
}
