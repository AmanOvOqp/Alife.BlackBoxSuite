using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Alife.BlackBoxSuite;

/// <summary>
/// 近期瞬态离散事件（仅在内存滑动窗口保留，供即时感知，不作长期堆积）
/// </summary>
public class BlackBoxEvent
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string Category { get; set; } = string.Empty; // App, CoRead, Keyboard, MouseWheel, MouseKinematics, Idle, Power, Session, Audio, AudioEndpoint, Media, Device, Clipboard, Screenshot, FilePulse
    public string Summary { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public double DurationSeconds { get; set; } = 0;

    public override string ToString()
    {
        string dur = DurationSeconds > 0 ? $" [持续 {FormatDuration(DurationSeconds)}]" : "";
        return $"[{Time:yyyy-MM-dd HH:mm:ss}] [{Category}] {Summary}{dur}{(string.IsNullOrEmpty(Details) ? "" : " (" + Details + ")")}";
    }

    public static string FormatDuration(double seconds)
    {
        if (seconds < 60) return $"{seconds:F0}秒";
        if (seconds < 3600) return $"{(seconds / 60):F1}分钟";
        return $"{(seconds / 3600):F1}小时";
    }
}

/// <summary>
/// 长期记忆：剪贴板知识库与截图视觉闪存条目（永久落盘，支持分类、上下文绑定、摘要与原始正文/图片路径）
/// </summary>
public class ClipboardItem
{
    public int Id { get; set; }
    public DateTime Time { get; set; } = DateTime.Now;
    public string Category { get; set; } = "Text"; // URL, Code, Article, Text, Image
    public int Length { get; set; } = 0; // 文本为字符数，Image为KB大小
    public string SourceContext { get; set; } = string.Empty; // 捕获时的前台与伴读窗口上下文标签
    public string ImagePath { get; set; } = string.Empty; // 若为Image分类，存储本地JPEG缩略/原图路径
    public string Summary { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;

    public override string ToString()
    {
        string ctx = !string.IsNullOrEmpty(SourceContext) ? $" [{SourceContext}]" : "";
        if (Category.Equals("Image", StringComparison.OrdinalIgnoreCase))
        {
            return $"[#{Id}] [{Time:yyyy-MM-dd HH:mm:ss}] [Image]{ctx} ({Length} KB): {Summary} -> {ImagePath}";
        }
        return $"[#{Id}] [{Time:yyyy-MM-dd HH:mm:ss}] [{Category}]{ctx} (共 {Length} 字): {Summary}";
    }
}

/// <summary>
/// 长期记忆：单个应用在当天的专注画像（含前台活跃、全屏沉浸与同屏伴读时长）
/// </summary>
public class DailyAppUsage
{
    public string ProcessName { get; set; } = string.Empty;
    public double TotalActiveSeconds { get; set; } = 0;
    public double FullscreenSeconds { get; set; } = 0;
    public double CoReadSeconds { get; set; } = 0;
    public int SessionCount { get; set; } = 0;
    public string LastWindowTitle { get; set; } = string.Empty;

    public double FullscreenRatio => TotalActiveSeconds > 0 ? Math.Clamp(FullscreenSeconds / TotalActiveSeconds, 0.0, 1.0) : 0;
}

/// <summary>
/// 长期记忆：整天的专注、伴读与文件产出画像
/// </summary>
public class DailyFocusProfile
{
    public string Date { get; set; } = string.Empty; // "yyyy-MM-dd"
    public DateTime FirstActiveTime { get; set; } = DateTime.MinValue;
    public DateTime LastActiveTime { get; set; } = DateTime.MinValue;
    public double TotalActiveSeconds { get; set; } = 0;
    public double TotalFullscreenSeconds { get; set; } = 0;
    public double TotalCoReadSeconds { get; set; } = 0;
    public int FileSavePulseCount { get; set; } = 0;
    public int NewDownloadFileCount { get; set; } = 0;
    public List<string> RecentLandedFiles { get; set; } = new();
    public Dictionary<string, DailyAppUsage> Apps { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public double FullscreenRatio => TotalActiveSeconds > 0 ? Math.Clamp(TotalFullscreenSeconds / TotalActiveSeconds, 0.0, 1.0) : 0;
}

public class BlackBoxStorage
{
    private static readonly Lazy<BlackBoxStorage> _lazyInstance = new(() => new BlackBoxStorage());
    public static BlackBoxStorage Instance => _lazyInstance.Value;

    private readonly object _lock = new();

    // 1. 瞬态事件：内存滑动窗口
    private readonly List<BlackBoxEvent> _transientEvents = new();

    // 2. 长期记忆资产 A & C：剪贴板知识库与截图视觉闪存库
    private readonly List<ClipboardItem> _clipboardStore = new();
    private int _lastClipboardId = 0;

    // 3. 长期记忆资产 B：每日应用与专注画像
    private readonly Dictionary<string, DailyFocusProfile> _dailyProfiles = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _storageDir;
    private readonly string _screenshotRootDir;
    private readonly string _clipboardDbPath;
    private readonly string _dailyProfilesPath;

    public string ScreenshotRootDir => _screenshotRootDir;

    public BlackBoxStorage()
    {
        _storageDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Storage", "BlackBox");
        _screenshotRootDir = Path.Combine(_storageDir, "Screenshots");
        _clipboardDbPath = Path.Combine(_storageDir, "clipboard_store.json");
        _dailyProfilesPath = Path.Combine(_storageDir, "daily_focus_profiles.json");

        try
        {
            if (!Directory.Exists(_storageDir)) Directory.CreateDirectory(_storageDir);
            if (!Directory.Exists(_screenshotRootDir)) Directory.CreateDirectory(_screenshotRootDir);
            Load();
        }
        catch { }
    }

    #region 瞬态事件管理（内存滑动窗口）
    public void AddTransientEvent(BlackBoxEvent ev, int maxCapacity = 300)
    {
        lock (_lock)
        {
            _transientEvents.Add(ev);
            int cap = Math.Max(50, maxCapacity);
            if (_transientEvents.Count > cap)
            {
                int removeCount = _transientEvents.Count - cap;
                _transientEvents.RemoveRange(0, removeCount);
            }
        }
    }

    public List<BlackBoxEvent> QueryTransientEvents(int minutes, string category = "All")
    {
        lock (_lock)
        {
            DateTime cutoff = DateTime.Now.AddMinutes(-Math.Max(1, minutes));
            var query = _transientEvents.Where(e => e.Time >= cutoff);
            if (!string.IsNullOrEmpty(category) && !category.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(e => e.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
            }
            return query.OrderBy(e => e.Time).ToList();
        }
    }
    #endregion

    #region 长期资产 A & C：剪贴板知识库与上下文绑定截图闪存库
    public ClipboardItem? AddClipboard(string text, BlackBoxConfig? config = null, string sourceContext = "")
    {
        if (string.IsNullOrEmpty(text)) return null;

        int minLen = config != null ? Math.Max(1, config.ClipboardMinLength) : 2;
        if (text.Length < minLen) return null;

        lock (_lock)
        {
            string processedText = text;
            if (config == null || config.ClipboardFilterSensitive)
            {
                processedText = SanitizeSensitiveContent(text);
            }

            bool dedup = config == null || config.ClipboardDedup;
            if (dedup && _clipboardStore.Count > 0 && _clipboardStore[^1].Content == processedText)
            {
                return null;
            }

            DateTime now = DateTime.Now;
            string category = DetectClipboardCategory(processedText);
            string summary = GenerateSummary(processedText);

            _lastClipboardId++;
            var item = new ClipboardItem
            {
                Id = _lastClipboardId,
                Time = now,
                Category = category,
                Length = processedText.Length,
                SourceContext = sourceContext ?? string.Empty,
                Summary = summary,
                Content = processedText
            };

            _clipboardStore.Add(item);
            SaveClipboardsOnly();
            return item;
        }
    }

    public ClipboardItem? AddScreenshotClipboard(
        string imagePath,
        int width,
        int height,
        long fileSizeBytes,
        string sourceContext)
    {
        if (string.IsNullOrEmpty(imagePath)) return null;

        lock (_lock)
        {
            DateTime now = DateTime.Now;
            int sizeKb = Math.Max(1, (int)Math.Round(fileSizeBytes / 1024.0));
            string summary = $"[截图 {width}x{height}] {(string.IsNullOrEmpty(sourceContext) ? "桌面截图" : sourceContext)}";
            string content = $"[视觉闪存截图]\n本地路径: {imagePath}\n图像尺寸: {width}x{height} ({sizeKb} KB)\n来源上下文: {sourceContext}";

            _lastClipboardId++;
            var item = new ClipboardItem
            {
                Id = _lastClipboardId,
                Time = now,
                Category = "Image",
                Length = sizeKb,
                SourceContext = sourceContext ?? string.Empty,
                ImagePath = imagePath,
                Summary = summary,
                Content = content
            };

            _clipboardStore.Add(item);
            SaveClipboardsOnly();
            return item;
        }
    }

    public static string SanitizeSensitiveContent(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;

        string result = raw;
        try
        {
            // GitHub Token: ghp_, gho_, ghu_, ghs_, ghr_
            result = Regex.Replace(result, @"(gh[pousr]_[A-Za-z0-9_]{30,50})", m => m.Value.Substring(0, 4) + "************************************");
            // GitHub Fine-grained PAT
            result = Regex.Replace(result, @"(github_pat_[A-Za-z0-9_]{50,100})", "github_pat_[PROTECTED_TOKEN]");
            // OpenAI / Anthropic Keys
            result = Regex.Replace(result, @"(sk-[A-Za-z0-9_-]{20,})", "sk-[PROTECTED_API_KEY]");
            // Password / Secret in json/yaml
            result = Regex.Replace(result, @"(?i)(""?(password|secret|token|passwd)""?\s*[:=]\s*""?)([^""\r\n\s]{6,})(""?| )", "$1[PROTECTED]$4");
        }
        catch { }
        return result;
    }

    public static string DetectClipboardCategory(string text)
    {
        string trimmed = text.Trim();

        // 1. URL 判定
        if ((trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
             trimmed.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase) ||
             trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) &&
            !trimmed.Contains('\n') && !trimmed.Contains('\r') && !trimmed.Contains(' '))
        {
            return "URL";
        }

        // 计算中文字符占比：若中文字符占比高于 25% 且包含句号/分号/顿号，优先按文章/笔记处理，避免中文长文因含个别英文词被误判为 Code
        int cjkCount = 0;
        foreach (char c in trimmed)
        {
            if (c >= 0x4E00 && c <= 0x9FFF) cjkCount++;
        }
        double cjkRatio = trimmed.Length > 0 ? (double)cjkCount / trimmed.Length : 0;

        if (cjkRatio >= 0.25 && trimmed.Length >= 80 && (trimmed.Contains('。') || trimmed.Contains('；') || trimmed.Contains('、') || trimmed.Contains('：')))
        {
            return "Article";
        }

        // 2. Code 判定（要求具备明显代码结构特征）
        bool hasCodeBraces = (trimmed.Contains('{') && trimmed.Contains('}') && (trimmed.Contains(';') || trimmed.Contains("=>") || trimmed.Contains("return ")));
        bool hasCodeKeywords = Regex.IsMatch(trimmed, @"\b(public\s+|private\s+|protected\s+|internal\s+|class\s+\w+|function\s+\w+|def\s+\w+|import\s+\w+|namespace\s+\w+|using\s+System|SELECT\s+.+\s+FROM)\b");
        bool hasHtmlTags = trimmed.StartsWith("<") && trimmed.Contains(">") && (trimmed.Contains("</") || trimmed.EndsWith("/>"));

        if (hasCodeBraces || hasCodeKeywords || hasHtmlTags)
        {
            return "Code";
        }

        // 3. Article / Note 判定
        if (trimmed.Length >= 200 || (trimmed.Length >= 80 && (trimmed.Contains('\n') || trimmed.Contains('。') || trimmed.Contains('；'))))
        {
            return "Article";
        }

        return "Text";
    }

    public static string GenerateSummary(string text)
    {
        string singleLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
        while (singleLine.Contains("  "))
        {
            singleLine = singleLine.Replace("  ", " ");
        }

        if (singleLine.Length > 70)
        {
            return singleLine.Substring(0, 70) + "...";
        }
        return singleLine;
    }

    public (List<ClipboardItem> Items, int TotalMatches, int TotalPages) QueryClipboard(
        int page = 1,
        int pageSize = 10,
        string category = "All",
        string keyword = "")
    {
        lock (_lock)
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 50);

            IEnumerable<ClipboardItem> query = _clipboardStore.AsEnumerable();

            if (!string.IsNullOrEmpty(category) && !category.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(c => c.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrEmpty(keyword))
            {
                query = query.Where(c =>
                    c.Summary.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    c.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    (c.SourceContext != null && c.SourceContext.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
            }

            var ordered = query.OrderByDescending(c => c.Id).ToList();
            int totalMatches = ordered.Count;
            int totalPages = (int)Math.Ceiling((double)totalMatches / pageSize);
            if (totalPages == 0) totalPages = 1;

            int skip = (page - 1) * pageSize;
            var items = ordered.Skip(skip).Take(pageSize).ToList();

            return (items, totalMatches, totalPages);
        }
    }

    public ClipboardItem? GetClipboardById(int id)
    {
        lock (_lock)
        {
            return _clipboardStore.FirstOrDefault(c => c.Id == id);
        }
    }
    #endregion

    #region 长期资产 B：每日应用、同屏伴读与文件脉搏画像
    public static bool IsProcessIgnored(string processName, string? ignoreProcessCsv)
    {
        if (string.IsNullOrEmpty(processName)) return true;
        if (string.IsNullOrEmpty(ignoreProcessCsv)) return false;
        var ignores = ignoreProcessCsv.Split(new[] { ',', ';', '，' }, StringSplitOptions.RemoveEmptyEntries)
                                      .Select(s => s.Trim().ToLowerInvariant());
        return ignores.Contains(processName.ToLowerInvariant());
    }

    private DailyFocusProfile GetOrCreateProfileUnlocked(string dateKey, DateTime timePoint)
    {
        if (!_dailyProfiles.TryGetValue(dateKey, out var profile))
        {
            profile = new DailyFocusProfile
            {
                Date = dateKey,
                FirstActiveTime = timePoint,
                LastActiveTime = timePoint
            };
            _dailyProfiles[dateKey] = profile;
        }
        return profile;
    }

    public void RecordAppUsage(
        string processName,
        string windowTitle,
        double dwellSeconds,
        bool isFullscreen,
        DateTime startTime,
        DateTime endTime,
        string? ignoreProcessCsv = null,
        bool incrementSession = true)
    {
        if (string.IsNullOrEmpty(processName) || dwellSeconds <= 0) return;
        if (IsProcessIgnored(processName, ignoreProcessCsv)) return;

        lock (_lock)
        {
            string dateKey = startTime.ToString("yyyy-MM-dd");
            var profile = GetOrCreateProfileUnlocked(dateKey, startTime);

            if (profile.FirstActiveTime == DateTime.MinValue || startTime < profile.FirstActiveTime)
            {
                profile.FirstActiveTime = startTime;
            }
            if (endTime > profile.LastActiveTime)
            {
                profile.LastActiveTime = endTime;
            }

            profile.TotalActiveSeconds += dwellSeconds;
            if (isFullscreen)
            {
                profile.TotalFullscreenSeconds += dwellSeconds;
            }

            if (!profile.Apps.TryGetValue(processName, out var appUsage))
            {
                appUsage = new DailyAppUsage
                {
                    ProcessName = processName
                };
                profile.Apps[processName] = appUsage;
            }

            appUsage.TotalActiveSeconds += dwellSeconds;
            if (isFullscreen)
            {
                appUsage.FullscreenSeconds += dwellSeconds;
            }
            if (incrementSession)
            {
                appUsage.SessionCount++;
            }
            if (!string.IsNullOrEmpty(windowTitle))
            {
                appUsage.LastWindowTitle = windowTitle;
            }
        }
    }

    public void RecordCoReadingUsage(
        string processName,
        string windowTitle,
        double coReadSeconds,
        DateTime now,
        string? ignoreProcessCsv = null)
    {
        if (string.IsNullOrEmpty(processName) || coReadSeconds <= 0) return;
        if (IsProcessIgnored(processName, ignoreProcessCsv)) return;

        lock (_lock)
        {
            string dateKey = now.ToString("yyyy-MM-dd");
            var profile = GetOrCreateProfileUnlocked(dateKey, now);

            profile.TotalCoReadSeconds += coReadSeconds;

            if (!profile.Apps.TryGetValue(processName, out var appUsage))
            {
                appUsage = new DailyAppUsage
                {
                    ProcessName = processName
                };
                profile.Apps[processName] = appUsage;
            }

            appUsage.CoReadSeconds += coReadSeconds;
            if (!string.IsNullOrEmpty(windowTitle))
            {
                appUsage.LastWindowTitle = windowTitle;
            }
        }
    }

    public void RecordFilePulse(
        string filePath,
        bool isNewLandedFile,
        long fileSizeBytes,
        DateTime now)
    {
        if (string.IsNullOrEmpty(filePath)) return;

        lock (_lock)
        {
            string dateKey = now.ToString("yyyy-MM-dd");
            var profile = GetOrCreateProfileUnlocked(dateKey, now);

            if (isNewLandedFile)
            {
                profile.NewDownloadFileCount++;
                string fileName = Path.GetFileName(filePath);
                string sizeStr = fileSizeBytes >= 1024 * 1024
                    ? $"{fileSizeBytes / (1024.0 * 1024.0):F1}MB"
                    : $"{Math.Max(1, fileSizeBytes / 1024)}KB";
                string entry = $"[{now:HH:mm}] {fileName} ({sizeStr})";
                if (!profile.RecentLandedFiles.Contains(entry))
                {
                    profile.RecentLandedFiles.Add(entry);
                    if (profile.RecentLandedFiles.Count > 50)
                    {
                        profile.RecentLandedFiles.RemoveAt(0);
                    }
                }
            }
            else
            {
                profile.FileSavePulseCount++;
            }
        }
    }

    public DailyFocusProfile? GetDailyProfile(string dateQuery, string? ignoreProcessCsv = null)
    {
        lock (_lock)
        {
            string targetDate;
            if (string.IsNullOrEmpty(dateQuery) || dateQuery.Equals("today", StringComparison.OrdinalIgnoreCase))
            {
                targetDate = DateTime.Now.ToString("yyyy-MM-dd");
            }
            else if (dateQuery.Equals("yesterday", StringComparison.OrdinalIgnoreCase))
            {
                targetDate = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd");
            }
            else
            {
                targetDate = dateQuery.Trim();
            }

            if (_dailyProfiles.TryGetValue(targetDate, out var profile))
            {
                if (!string.IsNullOrEmpty(ignoreProcessCsv))
                {
                    foreach (var key in profile.Apps.Keys.ToList())
                    {
                        if (IsProcessIgnored(key, ignoreProcessCsv))
                        {
                            profile.Apps.Remove(key);
                        }
                    }
                }
                profile.TotalActiveSeconds = profile.Apps.Values.Sum(a => a.TotalActiveSeconds);
                profile.TotalFullscreenSeconds = profile.Apps.Values.Sum(a => a.FullscreenSeconds);
                profile.TotalCoReadSeconds = profile.Apps.Values.Sum(a => a.CoReadSeconds);
                return profile;
            }
            return null;
        }
    }

    public List<DailyFocusProfile> GetRecentDailyProfiles(int days = 7)
    {
        lock (_lock)
        {
            days = Math.Clamp(days, 1, 60);
            return _dailyProfiles.Values
                .OrderByDescending(p => p.Date)
                .Take(days)
                .OrderBy(p => p.Date)
                .ToList();
        }
    }
    #endregion

    #region 持久化保存与加载
    private void SaveClipboardsOnly()
    {
        try
        {
            List<ClipboardItem> clipSnapshot;
            lock (_lock)
            {
                clipSnapshot = _clipboardStore.ToList();
            }
            string clipJson = JsonSerializer.Serialize(clipSnapshot, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_clipboardDbPath, clipJson, Encoding.UTF8);
        }
        catch { }
    }

    public void Save()
    {
        try
        {
            List<ClipboardItem> clipSnapshot;
            Dictionary<string, DailyFocusProfile> profileSnapshot;

            lock (_lock)
            {
                clipSnapshot = _clipboardStore.ToList();
                profileSnapshot = new Dictionary<string, DailyFocusProfile>(_dailyProfiles, StringComparer.OrdinalIgnoreCase);
            }

            string clipJson = JsonSerializer.Serialize(clipSnapshot, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_clipboardDbPath, clipJson, Encoding.UTF8);

            string profileJson = JsonSerializer.Serialize(profileSnapshot, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_dailyProfilesPath, profileJson, Encoding.UTF8);
        }
        catch { }
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_clipboardDbPath))
            {
                string json = File.ReadAllText(_clipboardDbPath, Encoding.UTF8);
                var list = JsonSerializer.Deserialize<List<ClipboardItem>>(json);
                if (list != null)
                {
                    lock (_lock)
                    {
                        _clipboardStore.Clear();
                        foreach (var item in list)
                        {
                            if (!item.Category.Equals("Image", StringComparison.OrdinalIgnoreCase))
                            {
                                item.Content = SanitizeSensitiveContent(item.Content);
                                item.Category = DetectClipboardCategory(item.Content);
                                item.Summary = GenerateSummary(item.Content);
                            }
                            _clipboardStore.Add(item);
                        }
                        if (_clipboardStore.Count > 0)
                        {
                            _lastClipboardId = _clipboardStore.Max(c => c.Id);
                        }
                    }
                }
            }

            if (File.Exists(_dailyProfilesPath))
            {
                string json = File.ReadAllText(_dailyProfilesPath, Encoding.UTF8);
                var dict = JsonSerializer.Deserialize<Dictionary<string, DailyFocusProfile>>(json);
                if (dict != null)
                {
                    lock (_lock)
                    {
                        _dailyProfiles.Clear();
                        foreach (var kvp in dict)
                        {
                            _dailyProfiles[kvp.Key] = kvp.Value;
                        }
                    }
                }
            }
        }
        catch { }
    }
    #endregion
}
