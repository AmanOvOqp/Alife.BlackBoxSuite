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
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    #endregion
}

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

