using System;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Microsoft.Extensions.Logging;

namespace Alife.BlackBoxSuite;

[Module("全息感知黑盒套件",
    "静默记录应用时间线、同屏伴读拓扑、打字节拍、滚轮翻页、鼠标动力学、声卡进程级峰值、SMTC媒体播放、外设监督、剪贴板与截图视觉闪存库、文件脉搏及每日专注画像",
    defaultCategory: "系统感知",
    EditorUI = typeof(BlackBoxSuiteUI)
)]
public partial class BlackBoxSuiteModule(
    XmlFunctionCaller functionCaller,
    ILogger<BlackBoxSuiteModule> logger,
    Interactor<BlackBoxSuiteModule> interactor
) :
    ChatBehaviour,
    IConfigurable<BlackBoxConfig>
{
    public BlackBoxConfig Configuration { get; set; } = null!;

    private BlackBoxStorage? _storage;
    private BlackBoxEngine? _engine;

    protected override Task OnAwake()
    {
        _storage = BlackBoxStorage.Instance;
        _storage.Load();
        _engine = BlackBoxEngine.Acquire(Configuration);

        BlackBoxEngine.OnActiveNotificationTyped += OnActiveNotifyTyped;
        BlackBoxEngine.OnGlobalDeviceNotification += OnDeviceNotify;
        BlackBoxEngine.OnCustomTriggerDispatch += OnCustomTriggerDispatch;

        // 把本角色的自定义触发规则注册进引擎，按角色独立评估与仲裁
        try
        {
            string roleName = Character?.Name ?? "";
            if (!string.IsNullOrWhiteSpace(roleName))
            {
                _engine.RegisterTriggerRole(
                    roleName,
                    () => Configuration?.EnableCustomTriggerNotifyPrompt ?? false,
                    Configuration?.MergeCustomTriggerPrompts ?? false,
                    Configuration?.CustomTriggerMinIntervalSeconds ?? 15);
            }
        }
        catch { }

        XmlHandler xmlHandler = new(this)
        {
            Description = "【全息感知黑盒】专供桌宠/跟班在需要时调阅前辈的实时切片、同屏伴读、鼠标动力学、长期剪贴板与截图闪存库、文件脉搏、每日专注画像与近期行为时间线。",
            Explanation = "黑盒平时保持绝对静默，不主动骚扰。数据分层架构：剪贴板与上下文截图闪存库（长期资产永久落盘、自动绑定窗口标题标签）、每日应用与专注伴读画像（按天永久归档全天时长、全屏深度、同屏伴读与Ctrl+S产出）、瞬态体征（击键、滚轮、鼠标动力学、声卡进程峰值、SMTC媒体、外设、文件脉搏仅内存滑动切片）。"
        };

        functionCaller.RegisterHandler(xmlHandler, DocumentMode.Implicit, cancellationToken: DestroyCancellationToken);

        RegisterSystemPrompt();
        logger.LogInformation("全息感知黑盒套件已挂载，单例安全引擎与长期记忆底座就绪。");

        return Task.CompletedTask;
    }

    protected override Task OnStart()
    {
        RegisterSystemPrompt();
        return Task.CompletedTask;
    }

    public void SyncConfigToEngine()
    {
        try
        {
            var eng = _engine ?? BlackBoxEngine.Instance;
            if (eng != null && Configuration != null)
            {
                eng.UpdateConfig(Configuration);
                eng.RefreshWatchersIfNeeded();
            }
            RegisterSystemPrompt();
        }
        catch { }
    }

        private void RegisterSystemPrompt()
    {
        if (Configuration != null && !Configuration.EnablePromptInjection)
            return;

        if (Configuration != null && !string.IsNullOrWhiteSpace(Configuration.CustomPrompt))
        {
            interactor.Prompt(Configuration.CustomPrompt);
            return;
        }

        string defaultPrompt = """
你搭载了「全息感知黑盒」（BlackBoxSuiteModule），拥有对主人PC物理体征、应用时间线、心流状态与外设环境的深层感知能力。
黑盒平时在底层绝对静默记账，当需要感知主人状态、答疑查证或回顾陪伴时，主动调用对应函数响应：
- 【实时体征号脉】感知当下前台软件、同屏伴读、打字节拍、滚轮翻页、鼠标动力学、空闲秒数、发声进程、音频端点与蓝牙外设 → <QueryCurrentActivity/>
- 【杜绝单帧误判铁律】视觉看到主人闭眼、低头或托腮时，严禁臆测打瞌睡！必须先调用 <QueryCurrentActivity/> 核对：若硬件空闲 ≤180s 或有击键/滚轮，属于深思或眨眼，严禁打扰
- 【真离席与归座唤醒】仅当系统空闲 >300s 且无发声媒体时判定离开工位，温和提示并静默挂起专注记账；主人归座触碰键鼠时黑盒会毫秒级通知桌宠唤醒
- 【暴躁甩鼠标/卡壳关切】当黑盒捕捉到高频剧烈晃动鼠标体征（RageShake）时，说明主人遇到代码Bug、网页报错或卡壳烦躁，主动温柔关切并提供排障建议
- 【音频防社死】耳机拔出切换扬声器外放时，自动切换为纯文本安静模式，杜绝在宿舍/公共场合语音外放社死；插入耳机恢复语音陪伴
- 【长期剪贴板与截图闪存】检索主人拷贝过的代码、链接、长文，或查阅带窗口标题上下文的截图视觉闪存 → <QueryClipboardHistory page="1" category="All|Code|URL|Article|Image" search="关键词"/>；调阅正文全文或原图路径用 <GetClipboardDetail id="..."/>
- 【今日/历史专注画像】主人问「今天学了多久/做了什么/专注时长」或深宵总结时，查阅全天活跃总时长、全屏沉浸、伴读拓扑、软件排行与文件产出 → <QueryDailyFocusProfile date="today|yesterday|yyyy-MM-dd"/>；看多日趋势用 <QueryFocusTrend days="7"/>
- 【行为轨迹复盘】排查过去一段时间的操作流（应用切换、击键、滚轮、声卡、休眠、锁屏、文件保存） → <QueryBlackBoxTimeline minutes="60" category="All|..."/>
- 【黑盒自适应调优】自主开关或调整黑盒各项监测阈限（驻留时长、离席容差、脱敏、音视频防假离线等） → <ConfigureBlackBox .../>
- 【自定义触发规则】仅当主人明确要求「盯着我别久坐」「打开某个软件就提醒我」「连续写太久就打断我」这类带体征条件的需求时，才用 <AddTrigger> 建规则，条件写法先查 <ListSignals/>，如 conditions="IdleSeconds>=300;IsFullscreen==0"。查看已有规则用 <ListTriggers/>，删除用 <RemoveTrigger id="..."/>。三条铁律：纯到点闹钟一律走 SystemEventBoost 定时任务，不得用本功能；绝不自作主张替主人建规则，只在主人明确提出时才建；首次创建必须先传 dryRun 预演一次，确认条件与当前状态是否合理，再正式落库
- 隐式文档：需要查阅函数具体参数结构时，可调用 <blackboxsuitemodule/> 加载详细文档。
""";

        interactor.Prompt(defaultPrompt.Trim());
    }


    private void OnActiveNotifyTyped(string prompt, string mode, BlackBoxNotificationType type)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(prompt)) return;

            // 角色本地独立门禁检查：核对当前角色配置是否开启了对应提醒
            var cfg = Configuration;
            if (cfg == null) return;

            bool isAllowed = type switch
            {
                BlackBoxNotificationType.RageShake => cfg.EnableRageShakeNotifyPrompt,
                BlackBoxNotificationType.Return => cfg.EnableReturnNotifyPrompt,
                BlackBoxNotificationType.Idle => cfg.EnableIdleNotifyPrompt,
                BlackBoxNotificationType.AudioEndpoint => cfg.EnableAudioEndpointNotifyPrompt,
                BlackBoxNotificationType.Device => cfg.DeviceNotifyPrompt,
                BlackBoxNotificationType.AppOpen => cfg.EnableAppOpenNotifyPrompt,
                BlackBoxNotificationType.KeyWatch => cfg.EnableKeyWatchNotifyPrompt,
                BlackBoxNotificationType.ContinuousWork => cfg.EnableContinuousWorkNotifyPrompt,
                BlackBoxNotificationType.LateNight => cfg.EnableLateNightNotifyPrompt,
                BlackBoxNotificationType.Custom => cfg.EnableCustomTriggerNotifyPrompt,
                _ => true // 测试或通用通知放行
            };

            if (!isAllowed)
            {
                // 当前角色未开启此类提醒，静默拦截丢弃，杜绝跨角色打扰
                return;
            }

            if (mode.Equals("Chat", StringComparison.OrdinalIgnoreCase))
            {
                interactor.Chat(prompt);
            }
            else
            {
                interactor.Poke(prompt);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "全息黑盒主动通知桌宠失败");
        }
    }

    /// <summary>
    /// 自定义触发派发接收端。严格按角色名核验，只处理归属于本角色的播报，
    /// 再核验本角色开关，双重门禁杜绝跨角色串台。
    /// </summary>
    private void OnCustomTriggerDispatch(string roleName, string prompt, string mode)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(prompt)) return;

            string myName = Character?.Name ?? "";
            if (string.IsNullOrWhiteSpace(myName)) return;
            if (!string.Equals(myName, roleName, StringComparison.OrdinalIgnoreCase)) return;

            var cfg = Configuration;
            if (cfg == null || !cfg.EnableCustomTriggerNotifyPrompt) return;

            if (!string.IsNullOrWhiteSpace(mode) && mode.Equals("Chat", StringComparison.OrdinalIgnoreCase))
            {
                interactor.Chat(prompt);
            }
            else
            {
                interactor.Poke(prompt);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "全息黑盒自定义触发派发失败");
        }
    }

    private void OnDeviceNotify(string prompt)
    {
        try
        {
            // 设备全局通知遵循当前角色 DeviceNotifyPrompt 开关
            if (Configuration != null && !Configuration.DeviceNotifyPrompt) return;
            interactor.Poke(prompt);
        }
        catch { }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查阅当前系统当下的实时状态切片：前台软件、同屏伴读窗口、驻留时长、击键密度、滚轮翻页、鼠标动力学体征、声卡状态与发声进程、音频输出端点、蓝牙外设、最近剪贴板/截图及文件脉搏。")]
    public Task QueryCurrentActivity()
    {
        var engine = _engine ?? BlackBoxEngine.Instance;
        if (engine == null)
        {
            interactor.Poke("小黑盒检测引擎尚未就绪。");
            return Task.CompletedTask;
        }

        double dwell = engine.CurrentForegroundDwellSeconds;
        string dwellStr = dwell < 60 ? $"{dwell:F0}秒" : $"{(dwell / 60):F1}分钟";

        var sb = new StringBuilder();
        sb.AppendLine("=== 当前系统实时行为切片 ===");
        sb.AppendLine($"- 当前前台应用: {engine.CurrentForegroundApp}");
        sb.AppendLine($"- 当前驻留时长: {dwellStr}");
        sb.AppendLine($"- 同屏伴读窗口: {engine.CurrentCoReadingSummary}");
        sb.AppendLine($"- 键盘当前分钟敲击: {engine.CurrentMinuteKeystrokes} 次 (退格: {engine.CurrentMinuteBackspaces} 次)");
        sb.AppendLine($"- 鼠标滚轮当前翻页: {engine.CurrentMinuteWheelClicks} 次 (滚动强度: {engine.CurrentMinuteWheelDelta})");
        sb.AppendLine($"- 鼠标动力学体征: {engine.CurrentMouseKinematicsSummary}");
        sb.AppendLine($"- 系统物理空闲状态: {engine.CurrentIdleStatusSummary}");
        sb.AppendLine($"- 声卡音频状态: {(engine.IsAudioPlaying ? "正在发声" : "静音中")} (主峰值: {engine.CurrentAudioPeak:F2} | 发声进程: {engine.ActiveAudioProcessesSummary})");
        sb.AppendLine($"- 默认音频输出端点: {(string.IsNullOrEmpty(engine.CurrentAudioDeviceFriendlyName) ? "检测中..." : engine.CurrentAudioDeviceFriendlyName)}");
        sb.AppendLine($"- 正在播放媒体: {(string.IsNullOrEmpty(engine.CurrentMediaTitle) ? "无媒体标题" : engine.CurrentMediaTitle)}");
        sb.AppendLine($"- 已连接蓝牙外设: {engine.ConnectedBluetoothDevicesSummary}");
        sb.AppendLine($"- 最近剪贴板文本: {engine.CurrentLastClipboardSummary}");
        sb.AppendLine($"- 最近视觉闪存截图: {engine.CurrentLastScreenshotSummary}");
        sb.AppendLine($"- 最近文件落盘/保存: {engine.CurrentLastFilePulseSummary}");

        interactor.Poke(sb.ToString().TrimEnd());
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("分页查阅长期剪贴板知识库与截图视觉闪存库大纲。支持按页码翻看历史、按关键词搜索（支持搜索窗口标题上下文标签）、或按分类过滤（All/URL/Code/Article/Text/Image）。")]
    public Task QueryClipboardHistory(
        [Description("页码，从1开始。第1页为最新记录，页码越大代表更早之前的记录")] int page = 1,
        [Description("每页条数，默认10条（1~50）")] int pageSize = 10,
        [Description("分类过滤：All(全部)、URL(链接)、Code(代码)、Article(长文/笔记)、Text(常规文本)、Image(上下文绑定截图)")] string category = "All",
        [Description("关键词搜索：在剪贴板内容、摘要或来源窗口标题上下文标签中模糊搜索，留空不限")] string keyword = ""
    )
    {
        var storage = _storage ?? BlackBoxStorage.Instance;
        var (items, totalMatches, totalPages) = storage.QueryClipboard(page, pageSize, category, keyword);
        if (items.Count == 0)
        {
            string filterDesc = (!string.IsNullOrEmpty(keyword) ? $" (关键词: \"{keyword}\")" : "") +
                                (!string.IsNullOrEmpty(category) && !category.Equals("All", StringComparison.OrdinalIgnoreCase) ? $" (分类: {category})" : "");
            interactor.Poke($"长期剪贴板与截图闪存库中未找到匹配记录{filterDesc}。");
            return Task.CompletedTask;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"=== 剪贴板与视觉闪存长期知识库 (第 {page}/{totalPages} 页，本次匹配共 {totalMatches} 条) ===");
        foreach (var item in items)
        {
            sb.AppendLine(item.ToString());
        }

        if (page < totalPages)
        {
            sb.AppendLine($"--- 提示：更早之前的历史记录请调用 QueryClipboardHistory(page: {page + 1}) ---");
        }
        sb.AppendLine("--- 提示：调阅完整正文或截图本地路径详情，请调用 GetClipboardDetail(id: 对应编号) ---");

        interactor.Poke(sb.ToString().TrimEnd());
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("根据 ID 精准调阅单条剪贴板记录的 100% 原始正文全文，或获取截图视觉闪存条目的本地图像路径与捕获上下文。")]
    public Task GetClipboardDetail(
        [Description("剪贴板记录编号ID（取自 QueryClipboardHistory 返回的 [#ID]）")] int id
    )
    {
        var storage = _storage ?? BlackBoxStorage.Instance;
        var item = storage.GetClipboardById(id);
        if (item == null)
        {
            interactor.Poke($"未找到编号为 #{id} 的剪贴板记录。");
            return Task.CompletedTask;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"=== 剪贴板/闪存记录详情 [#{item.Id}] ===");
        sb.AppendLine($"- 记录时间: {item.Time:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- 智能分类: [{item.Category}]");
        if (!string.IsNullOrEmpty(item.SourceContext))
        {
            sb.AppendLine($"- 捕获窗口上下文: {item.SourceContext}");
        }
        if (item.Category.Equals("Image", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine($"- 图像体积: {item.Length} KB");
            sb.AppendLine($"- 本地图像路径: {item.ImagePath}");
            sb.AppendLine("--- 提示：可直接将上述本地图像路径传入 LoadImage 或 AnalyseImage 查看截图内容 ---");
        }
        else
        {
            sb.AppendLine($"- 文本字数: {item.Length} 字符");
            sb.AppendLine("--- 原始正文内容 ---");
            sb.AppendLine(item.Content);
        }

        interactor.Poke(sb.ToString().TrimEnd());
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查阅前辈某一天的全天应用与专注画像：累计总活跃时间、全屏沉浸时间、同屏伴读时长、Ctrl+S保存产出、新落盘文件及各软件停留排行。")]
    public Task QueryDailyFocusProfile(
        [Description("查询日期：支持 'today'(今天)、'yesterday'(昨天)、或指定格式 'yyyy-MM-dd'（如 '2026-11-01'）")] string date = "today"
    )
    {
        (_engine ?? BlackBoxEngine.Instance)?.FlushCurrentActiveWindowSegment();
        var storage = _storage ?? BlackBoxStorage.Instance;
        var profile = storage.GetDailyProfile(date, Configuration.ProfileIgnoreProcesses);
        if (profile == null)
        {
            interactor.Poke($"未找到日期 [{date}] 的专注画像记录。");
            return Task.CompletedTask;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"=== 每日应用与专注画像 [{profile.Date}] ===");
        sb.AppendLine($"- 全天有效活跃时长: {BlackBoxEvent.FormatDuration(profile.TotalActiveSeconds)}");
        sb.AppendLine($"- 全屏沉浸专注时长: {BlackBoxEvent.FormatDuration(profile.TotalFullscreenSeconds)} (专注沉浸率: {(profile.FullscreenRatio * 100):F1}%)");
        sb.AppendLine($"- 同屏伴读累计时长: {BlackBoxEvent.FormatDuration(profile.TotalCoReadSeconds)}");
        sb.AppendLine($"- 工作区保存产出(Ctrl+S): {profile.FileSavePulseCount} 次 | 新落盘文件: {profile.NewDownloadFileCount} 个");
        if (profile.FirstActiveTime != DateTime.MinValue)
        {
            sb.AppendLine($"- 作息活跃时段: 首次活跃 {profile.FirstActiveTime:HH:mm:ss} ~ 最近活跃 {profile.LastActiveTime:HH:mm:ss}");
        }

        if (profile.RecentLandedFiles != null && profile.RecentLandedFiles.Count > 0)
        {
            sb.AppendLine($"- 当日新落盘文件: {string.Join(", ", profile.RecentLandedFiles.TakeLast(10))}");
        }

        if (profile.Apps.Count > 0)
        {
            sb.AppendLine("--- 软件使用、全屏与同屏伴读明细排行 ---");
            var sortedApps = profile.Apps.Values
                .OrderByDescending(a => a.TotalActiveSeconds + a.CoReadSeconds)
                .ToList();
            int rank = 1;
            foreach (var app in sortedApps)
            {
                string fullStr = app.FullscreenSeconds > 0 ? $" (全屏: {BlackBoxEvent.FormatDuration(app.FullscreenSeconds)}, {(app.FullscreenRatio * 100):F0}%)" : "";
                string coReadStr = app.CoReadSeconds > 0 ? $" [同屏伴读: {BlackBoxEvent.FormatDuration(app.CoReadSeconds)}]" : "";
                string titleStr = !string.IsNullOrEmpty(app.LastWindowTitle) ? $" | 标题: {app.LastWindowTitle}" : "";
                sb.AppendLine($"{rank++}. [{app.ProcessName}]: 主焦点 {BlackBoxEvent.FormatDuration(app.TotalActiveSeconds)}{fullStr}{coReadStr}, 会话: {app.SessionCount}次{titleStr}");
            }
        }

        interactor.Poke(sb.ToString().TrimEnd());
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查阅最近 N 天（默认7天）的每日专注总时长、全屏沉浸、同屏伴读与文件产出趋势。")]
    public Task QueryFocusTrend(
        [Description("查询天数，默认7天（1~60）")] int days = 7
    )
    {
        var storage = _storage ?? BlackBoxStorage.Instance;
        var list = storage.GetRecentDailyProfiles(days);
        if (list.Count == 0)
        {
            interactor.Poke($"暂无最近 {days} 天的每日专注画像记录。");
            return Task.CompletedTask;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"=== 最近 {list.Count} 天专注与产出趋势 ===");
        foreach (var p in list)
        {
            sb.AppendLine($"[{p.Date}] 活跃: {BlackBoxEvent.FormatDuration(p.TotalActiveSeconds),-8} | 全屏: {BlackBoxEvent.FormatDuration(p.TotalFullscreenSeconds),-8} | 伴读: {BlackBoxEvent.FormatDuration(p.TotalCoReadSeconds),-8} | Ctrl+S: {p.FileSavePulseCount}次 | 新文件: {p.NewDownloadFileCount}个");
        }

        interactor.Poke(sb.ToString().TrimEnd());
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查阅近期瞬态离散行为时间线（应用切屏、打字节拍、滚轮翻页、鼠标动力学、空闲休眠、声卡、媒体、外设、剪贴板、截图闪存、文件脉搏）。")]
    public Task QueryBlackBoxTimeline(
        [Description("查询过去多少分钟内的记录，默认60分钟")] int minutes = 60,
        [Description("过滤分类：All(全部)、App(应用切换)、Keyboard(打字节拍)、MouseWheel(滚轮翻页)、MouseKinematics(鼠标动力学)、Idle(空闲离席)、Power(休眠唤醒)、Session(锁屏解锁)、Audio(声卡发声)、AudioEndpoint(音频端点切换)、Media(媒体)、Device(外设)、Clipboard(剪贴板)、Screenshot(截图闪存)、FilePulse(文件脉搏)")] string category = "All"
    )
    {
        var storage = _storage ?? BlackBoxStorage.Instance;
        var events = storage.QueryTransientEvents(minutes, category);
        if (events.Count == 0)
        {
            interactor.Poke($"过去 {minutes} 分钟内无匹配的瞬态黑盒事件（分类: {category}）。");
            return Task.CompletedTask;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"=== 近期瞬态行为时间线 (过去 {minutes} 分钟，分类: {category}，共 {events.Count} 条) ===");
        foreach (var ev in events)
        {
            sb.AppendLine(ev.ToString());
        }

        interactor.Poke(sb.ToString().TrimEnd());
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("调整全息黑盒的配置开关与阈限（桌宠或跟班可根据需要自主调整）。")]
    public Task ConfigureBlackBox(
        [Description("是否启用应用时间线")] bool? enableWindow = null,
        [Description("窗口有效驻留阈限(秒)")] int? dwellSeconds = null,
        [Description("是否启用同屏伴读拓扑监测")] bool? enableCoRead = null,
        [Description("同屏伴读未遮挡比例门槛(0.25~0.95)")] double? coReadRatio = null,
        [Description("是否启用键盘打字节拍")] bool? enableKeyboard = null,
        [Description("键盘打字切片周期(秒)")] int? keyboardSliceSeconds = null,
        [Description("是否启用鼠标滚轮翻页监测")] bool? enableMouseWheel = null,
        [Description("滚轮翻页切片周期(秒)")] int? mouseWheelSliceSeconds = null,
        [Description("高强度翻页浏览阈值(次)")] int? mouseWheelIntense = null,
        [Description("滚轮翻页是否重置系统空闲")] bool? mouseWheelAntiIdle = null,
        [Description("是否启用鼠标动力学体征监测(甩鼠/沉思/连点)")] bool? enableMouseKinematics = null,
        [Description("暴躁甩鼠方向反转次数门槛")] int? rageShakeThreshold = null,
        [Description("暴躁甩鼠是否主动通知桌宠")] bool? rageShakeNotify = null,
        [Description("是否启用空闲离席挂机检测")] bool? enableIdle = null,
        [Description("常规桌面空闲离席判定阈限(秒)")] int? idleSeconds = null,
        [Description("全屏静止判定阈限(秒)")] int? fullscreenIdleSeconds = null,
        [Description("发声/媒体播放是否防假离线")] bool? audioAntiIdle = null,
        [Description("是否监听Windows锁屏")] bool? lockWatch = null,
        [Description("是否监听系统休眠挂起")] bool? suspendWatch = null,
        [Description("是否启用声卡监测")] bool? enableAudio = null,
        [Description("短音/蜂鸣过滤门槛(秒)")] int? audioFilterSeconds = null,
        [Description("是否启用媒体监测")] bool? enableMedia = null,
        [Description("是否启用外设监督")] bool? enableDevice = null,
        [Description("外设连断是否主动通知桌宠")] bool? deviceNotify = null,
        [Description("耳机/音频设备切换是否通知桌宠")] bool? audioEndpointNotify = null,
        [Description("是否启用毫秒级归座唤醒通知")] bool? enableReturnNotify = null,
        [Description("归座唤醒提示词模板")] string? returnPrompt = null,
        [Description("是否启用防误判真离席通知")] bool? enableIdleNotify = null,
        [Description("真离席提示词模板")] string? idlePrompt = null,
        [Description("耳机连接提示词模板({DeviceName})")] string? headphonePluggedPrompt = null,
        [Description("扬声器外放提示词模板({DeviceName})")] string? headphoneUnpluggedPrompt = null,
        [Description("甩鼠告警提示词模板({ProcessName}/{WindowTitle})")] string? rageShakePrompt = null,
        [Description("是否启用指定应用打开/切入提醒开关")] bool? enableAppOpenNotify = null,
        [Description("监控提醒的目标应用名单(逗号分隔进程名或标题关键词)")] string? watchedAppsCsv = null,
        [Description("同应用提醒冷却时间(秒)")] int? appOpenCooldownSeconds = null,
        [Description("指定应用打开提示词模板({ProcessName}/{WindowTitle})")] string? appOpenPrompt = null,
        [Description("是否启用自定义按键/组合键监测提醒开关")] bool? enableKeyWatchNotify = null,
        [Description("自定义监测按键/组合键列表(如 Back:20,Ctrl+Z:5,F5,Alt+Q)")] string? watchedKeysSpec = null,
        [Description("自定义按键默认触发次数门槛(次)")] int? keyWatchTriggerCount = null,
        [Description("自定义按键统计时间窗(秒)")] int? keyWatchWindowSeconds = null,
        [Description("自定义按键提醒冷却(秒)")] int? keyWatchCooldownSeconds = null,
        [Description("自定义按键提醒提示词模板({KeyName}/{Count}/{WindowSeconds}/{ProcessName}/{WindowTitle})")] string? keyWatchPrompt = null,
        [Description("是否启用连续久坐/防疲劳提醒开关")] bool? enableContinuousWorkNotify = null,
        [Description("连续久坐提醒门槛(分钟)")] int? continuousWorkMinutes = null,
        [Description("连续久坐提醒提示词模板({Minutes}/{ProcessName}/{WindowTitle})")] string? continuousWorkPrompt = null,
        [Description("久别归座分界门槛(分钟)")] int? longReturnMinutes = null,
        [Description("久别重逢归座提示词模板({AwayMinutes}/{TimeOfDay}/{ProcessName})")] string? longReturnPrompt = null,
        [Description("是否启用深夜修仙/熬夜温柔哄睡开关")] bool? enableLateNightNotify = null,
        [Description("深夜哄睡触发时间(如 01:00)")] string? lateNightTime = null,
        [Description("深夜哄睡提示词模板({CurrentTime}/{ProcessName}/{WindowTitle})")] string? lateNightPrompt = null,
        [Description("全屏沉浸免打扰延后护盾")] bool? deferWhenFullscreen = null,
        [Description("主动通知派发方式(Poke/Chat)")] string? notifyDispatchMode = null,
        [Description("是否启用剪贴板长期记忆")] bool? enableClipboard = null,
        [Description("剪贴板最小字数门槛")] int? clipboardMinLen = null,
        [Description("剪贴板Token/密钥是否自动脱敏")] bool? clipboardFilterSensitive = null,
        [Description("剪贴板重复内容是否去重")] bool? clipboardDedup = null,
        [Description("是否启用上下文截图视觉闪存库")] bool? enableScreenshotFlash = null,
        [Description("截图JPEG保存画质(10~100)")] int? screenshotQuality = null,
        [Description("是否启用每日专注画像聚合")] bool? enableDailyProfile = null,
        [Description("专注排行排除名单(逗号分隔进程名)")] string? profileIgnoreProcesses = null,
        [Description("专注画像定时自动存盘间隔(分钟)")] int? autoSaveInterval = null,
        [Description("是否启用工作区文件脉搏与下载管家")] bool? enableFilePulse = null,
        [Description("文件脉搏监听目录列表(逗号分隔路径)")] string? watchedDirsCsv = null,
        [Description("内存瞬态队列最大容量(条)")] int? maxTransientEvents = null,
        [Description("是否向角色上下文注入功能说明")] bool? enablePromptInjection = null
    )
    {
        if (enableWindow.HasValue) Configuration.EnableWindowTimeline = enableWindow.Value;
        if (dwellSeconds.HasValue) Configuration.WindowDwellThresholdSeconds = Math.Max(1, dwellSeconds.Value);
        if (enableCoRead.HasValue) Configuration.EnableCoReadingWatch = enableCoRead.Value;
        if (coReadRatio.HasValue) Configuration.CoReadingUnoccludedRatio = Math.Clamp(coReadRatio.Value, 0.25, 0.95);

        if (enableKeyboard.HasValue) Configuration.EnableKeyboardWatch = enableKeyboard.Value;
        if (keyboardSliceSeconds.HasValue) Configuration.KeyboardSliceSeconds = Math.Max(10, keyboardSliceSeconds.Value);

        if (enableMouseWheel.HasValue) Configuration.EnableMouseWheelWatch = enableMouseWheel.Value;
        if (mouseWheelSliceSeconds.HasValue) Configuration.MouseWheelSliceSeconds = Math.Max(10, mouseWheelSliceSeconds.Value);
        if (mouseWheelIntense.HasValue) Configuration.MouseWheelIntenseThreshold = Math.Max(5, mouseWheelIntense.Value);
        if (mouseWheelAntiIdle.HasValue) Configuration.EnableMouseWheelAntiIdle = mouseWheelAntiIdle.Value;

        if (enableMouseKinematics.HasValue) Configuration.EnableMouseKinematicsWatch = enableMouseKinematics.Value;
        if (rageShakeThreshold.HasValue) Configuration.RageShakeReversalThreshold = Math.Max(3, rageShakeThreshold.Value);
        if (enableAppOpenNotify.HasValue) Configuration.EnableAppOpenNotifyPrompt = enableAppOpenNotify.Value;
        if (watchedAppsCsv != null) Configuration.WatchedAppsCsv = watchedAppsCsv;
        if (appOpenCooldownSeconds.HasValue) Configuration.AppOpenNotifyCooldownSeconds = Math.Max(0, appOpenCooldownSeconds.Value);
        if (!string.IsNullOrWhiteSpace(appOpenPrompt)) Configuration.AppOpenPromptTemplate = appOpenPrompt;

        if (enableKeyWatchNotify.HasValue) Configuration.EnableKeyWatchNotifyPrompt = enableKeyWatchNotify.Value;
        if (watchedKeysSpec != null) Configuration.WatchedKeysSpec = watchedKeysSpec;
        if (keyWatchTriggerCount.HasValue) Configuration.KeyWatchTriggerCount = Math.Max(1, keyWatchTriggerCount.Value);
        if (keyWatchWindowSeconds.HasValue) Configuration.KeyWatchWindowSeconds = Math.Max(1, keyWatchWindowSeconds.Value);
        if (keyWatchCooldownSeconds.HasValue) Configuration.KeyWatchCooldownSeconds = Math.Max(1, keyWatchCooldownSeconds.Value);
        if (!string.IsNullOrWhiteSpace(keyWatchPrompt)) Configuration.KeyWatchPromptTemplate = keyWatchPrompt;

        if (enableContinuousWorkNotify.HasValue) Configuration.EnableContinuousWorkNotifyPrompt = enableContinuousWorkNotify.Value;
        if (continuousWorkMinutes.HasValue) Configuration.ContinuousWorkThresholdMinutes = Math.Max(5, continuousWorkMinutes.Value);
        if (!string.IsNullOrWhiteSpace(continuousWorkPrompt)) Configuration.ContinuousWorkPromptTemplate = continuousWorkPrompt;

        if (longReturnMinutes.HasValue) Configuration.LongReturnThresholdMinutes = Math.Max(5, longReturnMinutes.Value);
        if (!string.IsNullOrWhiteSpace(longReturnPrompt)) Configuration.LongReturnPromptTemplate = longReturnPrompt;
        if (enableLateNightNotify.HasValue) Configuration.EnableLateNightNotifyPrompt = enableLateNightNotify.Value;
        if (!string.IsNullOrWhiteSpace(lateNightTime)) Configuration.LateNightTriggerTime = lateNightTime.Trim();
        if (!string.IsNullOrWhiteSpace(lateNightPrompt)) Configuration.LateNightPromptTemplate = lateNightPrompt;
        if (deferWhenFullscreen.HasValue) Configuration.DeferNotifyWhenFullscreen = deferWhenFullscreen.Value;
        

        if (enableIdle.HasValue) Configuration.EnableIdleWatch = enableIdle.Value;
        if (idleSeconds.HasValue) Configuration.IdleThresholdSeconds = Math.Max(30, idleSeconds.Value);
        if (fullscreenIdleSeconds.HasValue) Configuration.FullscreenIdleThresholdSeconds = Math.Max(60, fullscreenIdleSeconds.Value);
        if (audioAntiIdle.HasValue) Configuration.EnableAudioAntiIdle = audioAntiIdle.Value;
        if (lockWatch.HasValue) Configuration.EnableSessionLockWatch = lockWatch.Value;
        if (suspendWatch.HasValue) Configuration.EnablePowerSuspendWatch = suspendWatch.Value;

        if (enableAudio.HasValue) Configuration.EnableAudioWatch = enableAudio.Value;
        if (audioFilterSeconds.HasValue) Configuration.AudioFilterDurationSeconds = Math.Max(1, audioFilterSeconds.Value);
        if (enableMedia.HasValue) Configuration.EnableMediaWatch = enableMedia.Value;

        if (enableDevice.HasValue) Configuration.EnableDeviceWatch = enableDevice.Value;
        if (deviceNotify.HasValue) Configuration.DeviceNotifyPrompt = deviceNotify.Value;
        if (audioEndpointNotify.HasValue)
        {
            Configuration.EnableAudioEndpointNotifyPrompt = audioEndpointNotify.Value;
        }
        if (enableReturnNotify.HasValue) Configuration.EnableReturnNotifyPrompt = enableReturnNotify.Value;
        if (!string.IsNullOrEmpty(returnPrompt)) Configuration.ReturnPromptTemplate = returnPrompt;
        if (enableIdleNotify.HasValue) Configuration.EnableIdleNotifyPrompt = enableIdleNotify.Value;
        if (!string.IsNullOrEmpty(idlePrompt)) Configuration.IdlePromptTemplate = idlePrompt;
        if (!string.IsNullOrEmpty(headphonePluggedPrompt)) Configuration.HeadphonePluggedPromptTemplate = headphonePluggedPrompt;
        if (!string.IsNullOrEmpty(headphoneUnpluggedPrompt)) Configuration.HeadphoneUnpluggedPromptTemplate = headphoneUnpluggedPrompt;
        if (rageShakeNotify.HasValue)
        {
            Configuration.EnableRageShakeNotifyPrompt = rageShakeNotify.Value;
        }
        if (!string.IsNullOrEmpty(rageShakePrompt)) Configuration.RageShakePromptTemplate = rageShakePrompt;
        if (!string.IsNullOrEmpty(notifyDispatchMode)) Configuration.NotifyDispatchMode = notifyDispatchMode;

        if (enableClipboard.HasValue) Configuration.EnableClipboardWatch = enableClipboard.Value;
        if (clipboardMinLen.HasValue) Configuration.ClipboardMinLength = Math.Max(1, clipboardMinLen.Value);
        if (clipboardFilterSensitive.HasValue) Configuration.ClipboardFilterSensitive = clipboardFilterSensitive.Value;
        if (clipboardDedup.HasValue) Configuration.ClipboardDedup = clipboardDedup.Value;

        if (enableScreenshotFlash.HasValue) Configuration.EnableScreenshotFlashWatch = enableScreenshotFlash.Value;
        if (screenshotQuality.HasValue) Configuration.ScreenshotJpegQuality = Math.Clamp(screenshotQuality.Value, 10, 100);

        if (enableDailyProfile.HasValue) Configuration.EnableDailyFocusProfile = enableDailyProfile.Value;
        if (profileIgnoreProcesses != null) Configuration.ProfileIgnoreProcesses = profileIgnoreProcesses;
        if (autoSaveInterval.HasValue) Configuration.ProfileAutoSaveIntervalMinutes = Math.Max(1, autoSaveInterval.Value);

        bool watcherChanged = false;
        if (enableFilePulse.HasValue)
        {
            Configuration.EnableFilePulseWatch = enableFilePulse.Value;
            watcherChanged = true;
        }
        if (watchedDirsCsv != null)
        {
            Configuration.WatchedDirectoriesCsv = watchedDirsCsv;
            watcherChanged = true;
        }
        if (watcherChanged)
        {
            _engine?.RefreshWatchersIfNeeded();
        }

        if (maxTransientEvents.HasValue) Configuration.MaxTransientEventsCount = Math.Max(50, maxTransientEvents.Value);
        if (enablePromptInjection.HasValue)
        {
            Configuration.EnablePromptInjection = enablePromptInjection.Value;
            RegisterSystemPrompt();
        }

        _engine?.UpdateConfig(Configuration);

        interactor.Poke(
            $"小黑盒配置已更新生效：窗口={Configuration.EnableWindowTimeline}(阈限:{Configuration.WindowDwellThresholdSeconds}s, 伴读:{Configuration.EnableCoReadingWatch}/{Configuration.CoReadingUnoccludedRatio:P0}), " +
            $"键盘={Configuration.EnableKeyboardWatch}, 滚轮={Configuration.EnableMouseWheelWatch}, 鼠标动力学={Configuration.EnableMouseKinematicsWatch}(甩鼠门槛:{Configuration.RageShakeReversalThreshold}次), " +
            $"空闲={Configuration.EnableIdleWatch}(常规:{Configuration.IdleThresholdSeconds}s, 全屏:{Configuration.FullscreenIdleThresholdSeconds}s), " +
            $"声卡={Configuration.EnableAudioWatch}, 媒体={Configuration.EnableMediaWatch}, " +
            $"剪贴板={Configuration.EnableClipboardWatch}, 截图闪存库={Configuration.EnableScreenshotFlashWatch}(JPEG:{Configuration.ScreenshotJpegQuality}), " +
            $"文件脉搏={Configuration.EnableFilePulseWatch}(目录:{Configuration.WatchedDirectoriesCsv})。"
        );
        return Task.CompletedTask;
    }

    protected override Task OnDestroy()
    {
        BlackBoxEngine.OnActiveNotificationTyped -= OnActiveNotifyTyped;
        BlackBoxEngine.OnGlobalDeviceNotification -= OnDeviceNotify;
        BlackBoxEngine.OnCustomTriggerDispatch -= OnCustomTriggerDispatch;

        try
        {
            string roleName = Character?.Name ?? "";
            if (!string.IsNullOrWhiteSpace(roleName)) _engine?.UnregisterTriggerRole(roleName);
        }
        catch { }

        BlackBoxEngine.Release();
        _engine = null;
        return Task.CompletedTask;
    }
}
