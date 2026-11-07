using System;
using System.Threading;
using OpenCvSharp;

namespace Alife.BlackBoxSuite;

public sealed class BlackBoxCameraMonitor : IDisposable
{
    private readonly object _syncRoot = new object();
    private Thread? _thread;
    private volatile bool _running;
    private bool _disposed;

    private volatile bool _available;
    private volatile bool _hasFace;
    private volatile int _faceCount;
    private double _yaw;
    private double _pitch;
    private long _lastSeenTicks;

    private int _deviceIndex;
    private int _fps = 3;
    private double _scoreThreshold = 0.6;
    private double _yawThreshold = 0.15;
    private int _leaveGraceSeconds = 60;
    private volatile bool _needRebuild;

    private static string? _modelPathCache;
    private static readonly object _modelLock = new object();

    public bool Available { get { return _available; } }
    public bool HasFace { get { return _hasFace; } }
    public int FaceCount { get { return _faceCount; } }
    public double Yaw { get { return _yaw; } }
    public double Pitch { get { return _pitch; } }

    public double AbsentSeconds()
    {
        long t = Interlocked.Read(ref _lastSeenTicks);
        if (t == 0) return 9999.0;
        double d = (DateTime.Now - new DateTime(t, DateTimeKind.Local)).TotalSeconds;
        if (d < 0) return 0.0;
        return d;
    }

    public bool IsPresent()
    {
        if (!_available) return false;
        if (_leaveGraceSeconds <= 0) return _hasFace;
        long t = Interlocked.Read(ref _lastSeenTicks);
        if (t == 0) return false;
        double d = (DateTime.Now - new DateTime(t, DateTimeKind.Local)).TotalSeconds;
        return d <= _leaveGraceSeconds;
    }

    public void UpdateSettings(int deviceIndex, int fps, double score, double yawThreshold, int leaveGrace)
    {
        lock (_syncRoot)
        {
            if (_disposed) return;
            bool needRestart = _deviceIndex != Math.Clamp(deviceIndex, 0, 9);
            double newScore = Math.Clamp(score, 0.1, 0.99);
            if (Math.Abs(newScore - _scoreThreshold) > 0.0001) _needRebuild = true;
            _deviceIndex = Math.Clamp(deviceIndex, 0, 9);
            _fps = Math.Clamp(fps, 1, 10);
            _scoreThreshold = newScore;
            _yawThreshold = yawThreshold;
            _leaveGraceSeconds = Math.Max(0, leaveGrace);
            if (needRestart && _running)
            {
                StopInternal();
                StartInternal();
            }
        }
    }

    public void Start()
    {
        lock (_syncRoot) { StartInternal(); }
    }

    private void StartInternal()
    {
        if (_running || _disposed) return;
        if (_thread != null) return;
        _running = true;
        var t = new Thread(Loop) { IsBackground = true, Name = "BlackBoxCamera" };
        _thread = t;
        try { t.Start(); } catch { _running = false; _thread = null; }
    }

    public void Stop()
    {
        lock (_syncRoot) { StopInternal(); }
    }

    private void StopInternal()
    {
        _running = false;
        var t = _thread;
        if (t != null)
        {
            bool exited = false;
            try { exited = t.Join(5000); } catch { }
            if (!exited) return;
        }
        _thread = null;
        _available = false;
        _hasFace = false;
        _faceCount = 0;
        _yaw = 0;
        _pitch = 0;
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed) return;
            _disposed = true;
            StopInternal();
        }
    }

    private static int _camCountCache = -2;
    private static DateTime _camCountTime = DateTime.MinValue;

    private static int QueryCameraCount()
    {
        lock (_modelLock)
        {
            if (_camCountCache != -2 && (DateTime.UtcNow - _camCountTime).TotalSeconds < 30) return _camCountCache;
            int n = -1;
            try
            {
                string path = @"SYSTEM\CurrentControlSet\Control\DeviceClasses\{e5323777-f976-4f5b-9b55-b94699c46e44}";
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path))
                {
                    if (k != null)
                    {
                        int cnt = 0;
                        foreach (var name in k.GetSubKeyNames())
                        {
                            if (string.Equals(name, "Properties", StringComparison.OrdinalIgnoreCase)) continue;
                            cnt++;
                        }
                        if (cnt >= 0) n = cnt;
                    }
                }
            }
            catch { n = -1; }
            _camCountCache = n;
            _camCountTime = DateTime.UtcNow;
            return n;
        }
    }

    private static string? LocateModel()
    {
        lock (_modelLock)
        {
            if (_modelPathCache != null) return _modelPathCache;
            var candidates = new System.Collections.Generic.List<string>();
            try
            {
                string baseDir = AppContext.BaseDirectory;
                string dir = baseDir;
                for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
                {
                    candidates.Add(System.IO.Path.Combine(dir, "models", "face_detection_yunet_2023mar.onnx"));
                    candidates.Add(System.IO.Path.Combine(dir, "Plugins", "Alife.BlackBoxSuite", "models", "face_detection_yunet_2023mar.onnx"));
                    candidates.Add(System.IO.Path.Combine(dir, "Storage", "Plugins", "Alife.BlackBoxSuite", "models", "face_detection_yunet_2023mar.onnx"));
                    dir = System.IO.Path.GetDirectoryName(dir) ?? "";
                }
            }
            catch { }
            foreach (var c in candidates)
            {
                try { if (System.IO.File.Exists(c)) { _modelPathCache = c; return c; } } catch { }
            }
            return null;
        }
    }

    private void Loop()
    {
        VideoCapture? cap = null;
        FaceDetectorYN? detector = null;
        string? modelPath = LocateModel();
        int backoffMs = 1000;
        // 单帧检测耗时仅约 25ms，OpenCV 默认吃满全部核心做并行，CPU 时间暴涨 4 倍却只换来 2 倍墙钟收益，此处锁死单线程。
        try { Cv2.SetNumThreads(1); } catch { }

        try
        {
            while (_running)
            {
                long frameStartMs = Environment.TickCount64;
                try
                {
                    if (_needRebuild && detector != null)
                    {
                        try { detector.Dispose(); } catch { }
                        detector = null;
                        _needRebuild = false;
                    }

                    if (detector == null)
                    {
                        if (modelPath == null) { _available = false; SleepInterruptible(3000); continue; }
                        detector = FaceDetectorYN.Create(modelPath, "", new Size(640, 480), (float)_scoreThreshold, 0.3f, 5000);
                    }

                    if (cap == null)
                    {
                        int camCount = QueryCameraCount();
                        if (camCount >= 0 && _deviceIndex >= camCount)
                        {
                            _available = false;
                            _hasFace = false;
                            _faceCount = 0;
                            SleepInterruptible(backoffMs);
                            backoffMs = Math.Min(backoffMs * 2, 30000);
                            continue;
                        }

                        VideoCapture? c = null;
                        try { c = new VideoCapture(_deviceIndex, VideoCaptureAPIs.DSHOW); } catch { c = null; }
                        bool opened = c != null && c.IsOpened();
                        if (!opened)
                        {
                            try { c?.Dispose(); } catch { }
                            c = null;
                        }
                        if (!opened || c == null)
                        {
                            _available = false;
                            _hasFace = false;
                            _faceCount = 0;
                            SleepInterruptible(backoffMs);
                            backoffMs = Math.Min(backoffMs * 2, 30000);
                            continue;
                        }
                        c.Set(VideoCaptureProperties.FrameWidth, 640);
                        c.Set(VideoCaptureProperties.FrameHeight, 480);
                        cap = c;
                        backoffMs = 1000;
                    }

                    _available = true;
                    using (var frame = new Mat())
                    {
                        if (!cap.Read(frame) || frame.Empty())
                        {
                            try { cap.Dispose(); } catch { }
                            cap = null;
                            _available = false;
                            _hasFace = false;
                            _faceCount = 0;
                            SleepInterruptible(500);
                            continue;
                        }

                        using (var resized = new Mat())
                        using (var faces = new Mat())
                        {
                            if (frame.Width == 640 && frame.Height == 480) { frame.CopyTo(resized); } else { Cv2.Resize(frame, resized, new Size(640, 480)); }
                            detector.Detect(resized, faces);
                            ProcessFaces(faces);
                        }
                    }
                }
                catch
                {
                    _available = false;
                    _hasFace = false;
                    _faceCount = 0;
                    try { cap?.Dispose(); } catch { }
                    cap = null;
                    try { detector?.Dispose(); } catch { }
                    detector = null;
                    SleepInterruptible(1000);
                }

                int delay = 1000 / Math.Max(1, _fps);
                long spentMs = Environment.TickCount64 - frameStartMs;
                int remainMs = delay - (int)spentMs;
                if (remainMs > 0) SleepInterruptible(remainMs);
            }
        }
        finally
        {
            try { cap?.Dispose(); } catch { }
            try { detector?.Dispose(); } catch { }
            cap = null;
            detector = null;
            _available = false;
            _hasFace = false;
            _faceCount = 0;
            if (ReferenceEquals(Thread.CurrentThread, _thread)) _thread = null;
        }
    }

    private void SleepInterruptible(int ms)
    {
        int waited = 0;
        while (waited < ms && _running)
        {
            int step = Math.Min(50, ms - waited);
            Thread.Sleep(step);
            waited += step;
        }
    }

    private void ProcessFaces(Mat faces)
    {
        int rows = faces == null ? 0 : faces.Rows;
        if (rows <= 0)
        {
            _faceCount = 0;
            _hasFace = false;
            _yaw = 0;
            _pitch = 0;
            return;
        }

        int best = -1;
        float bestScore = -1f;
        int passed = 0;
        for (int r = 0; r < rows; r++)
        {
            float s = faces.At<float>(r, 14);
            float fw = faces.At<float>(r, 2);
            float fh = faces.At<float>(r, 3);
            if (fw < 36f || fh < 36f) continue;
            if (s >= _scoreThreshold) passed++;
            if (s > bestScore) { bestScore = s; best = r; }
        }
        _faceCount = passed;

        if (best < 0 || bestScore < _scoreThreshold)
        {
            _hasFace = false;
            _yaw = 0;
            _pitch = 0;
            return;
        }

        float rx = faces.At<float>(best, 4);
        float ry = faces.At<float>(best, 5);
        float lx = faces.At<float>(best, 6);
        float ly = faces.At<float>(best, 7);
        float nx = faces.At<float>(best, 8);
        float ny = faces.At<float>(best, 9);

        double eyeDx = rx - lx;
        double eyeDy = ry - ly;
        double eyeDist = Math.Sqrt(eyeDx * eyeDx + eyeDy * eyeDy);
        if (eyeDist < 1.0) eyeDist = 1.0;

        double cx = (rx + lx) / 2.0;
        double cy = (ry + ly) / 2.0;

        _yaw = Math.Round((nx - cx) / eyeDist, 3);
        _pitch = Math.Round((ny - cy) / eyeDist, 3);
        _hasFace = true;
        Interlocked.Exchange(ref _lastSeenTicks, DateTime.Now.Ticks);
    }
}
