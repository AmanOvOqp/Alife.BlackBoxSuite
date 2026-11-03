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
    // 8. 工作区文件脉搏与下载监听状态 (Module 15)
    private FilePulseWatcherNative? _filePulseWatcher;
    private string _lastFilePulseSummary = "暂无近期文件落盘/保存记录";
    public string CurrentLastFilePulseSummary => _lastFilePulseSummary;

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

}

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

