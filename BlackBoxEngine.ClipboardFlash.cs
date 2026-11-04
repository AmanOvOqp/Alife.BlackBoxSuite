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
    // 7. 剪贴板与截图闪存库状态 (Module 7 & Module 16)
    private ClipboardWatcherNative? _clipboardWatcher;
    private string _lastClipboardText = string.Empty;
    private DateTime _lastClipboardTime = DateTime.MinValue;
    private string _lastScreenshotSummary = string.Empty;

    public string CurrentLastClipboardSummary => !string.IsNullOrEmpty(_lastClipboardText)
        ? $"[{_lastClipboardTime:HH:mm:ss}] ({_lastClipboardText.Length}字) {(_lastClipboardText.Length > 40 ? _lastClipboardText.Substring(0, 40).Replace("\r", " ").Replace("\n", " ") + "..." : _lastClipboardText.Replace("\r", " ").Replace("\n", " "))}"
        : "暂无最近复制记录";

    public string CurrentLastScreenshotSummary => !string.IsNullOrEmpty(_lastScreenshotSummary)
        ? _lastScreenshotSummary
        : "暂无近期截图记录";


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
            NoteTriggerEvent(TriggerSignal.ClipboardChanged);
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

}

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

