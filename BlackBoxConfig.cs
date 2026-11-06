using System.ComponentModel;

namespace Alife.BlackBoxSuite;

public class BlackBoxConfig
{
    #region 1. 应用时间线与同屏伴读拓扑
    [DisplayName("应用时间线黑盒")]
    [Description("是否静默记录前台窗口与应用切换时间线")]
    public bool EnableWindowTimeline { get; set; } = true;

    [DisplayName("窗口记录有效阈限(秒)")]
    [Description("前台应用停留超过此秒数才算作有效记录并计入专注画像（防抖，过滤频繁Alt+Tab），默认10秒")]
    public int WindowDwellThresholdSeconds { get; set; } = 10;

    [DisplayName("同屏伴读空间拓扑监测")]
    [Description("是否通过DWM真实视觉边界识别分屏并列与双屏协同：当辅助窗口未遮挡面积达标且主窗口活跃时，自动累加同屏伴读时长")]
    public bool EnableCoReadingWatch { get; set; } = true;

    [DisplayName("同屏伴读未遮挡比例门槛")]
    [Description("辅助窗口未被遮挡面积占自身面积的比例门槛，默认0.60(即60%以上可见)")]
    public double CoReadingUnoccludedRatio { get; set; } = 0.60;
    #endregion

    #region 2. 键盘与打字节拍
    [DisplayName("键盘活跃度黑盒")]
    [Description("是否静默记录键盘打字节拍与活跃度（绝不记录按键内容，仅统计敲击次数与APM）")]
    public bool EnableKeyboardWatch { get; set; } = true;

    [DisplayName("键盘切片周期(秒)")]
    [Description("打字节拍聚合统计的周期，默认60秒")]
    public int KeyboardSliceSeconds { get; set; } = 60;
    #endregion

    #region 3. 鼠标滚轮与动力学体征
    [DisplayName("鼠标滚轮活跃黑盒")]
    [Description("是否静默记录鼠标滚轮翻页次数与强度（专用于捕捉阅读文献、长文与网页时的真实活跃度，绝不掉帧）")]
    public bool EnableMouseWheelWatch { get; set; } = true;

    [DisplayName("滚轮切片周期(秒)")]
    [Description("滚轮翻页动作聚合统计的周期，默认60秒")]
    public int MouseWheelSliceSeconds { get; set; } = 60;

    [DisplayName("高强度速读翻页门槛(次)")]
    [Description("切片周期内滚轮滚动次数超过此值判定为高强度速读/文献精读，默认30次")]
    public int MouseWheelIntenseThreshold { get; set; } = 30;

    [DisplayName("滚轮重置空闲防误判")]
    [Description("检测到手指拨动滚轮翻页时，是否立即重置系统物理活跃时间（防看长文误判离席），默认开启")]
    public bool EnableMouseWheelAntiIdle { get; set; } = true;

    [DisplayName("鼠标动力学体征监测")]
    [Description("是否50ms低频采样鼠标物理向量（零坐标记录），识别暴躁甩鼠标、沉思游走审题（防假离线）与高频连点脉冲")]
    public bool EnableMouseKinematicsWatch { get; set; } = true;

    [DisplayName("暴躁甩鼠方向反转门槛(次)")]
    [Description("1秒内高速移动方向正负反转达到此次数判定为卡壳/烦躁狂晃鼠标，默认4次")]
    public int RageShakeReversalThreshold { get; set; } = 4;
    #endregion

    #region 4. 空闲、锁屏与电源休眠
    [DisplayName("空闲挂机/离席检测")]
    [Description("是否基于硬件物理输入检测系统离席与空闲状态（记录离开与返回电脑的时间线）")]
    public bool EnableIdleWatch { get; set; } = true;

    [DisplayName("常规空闲离席阈值(秒)")]
    [Description("常规桌面下鼠标键盘无任何输入超过此秒数，判定为进入离席/挂机空闲状态，默认180秒(3分钟)")]
    public int IdleThresholdSeconds { get; set; } = 180;

    [DisplayName("全屏静止容差门槛(秒)")]
    [Description("全屏专注或影音状态下静止容差，达到此秒数才判定离席，默认300秒(5分钟)")]
    public int FullscreenIdleThresholdSeconds { get; set; } = 300;

    [DisplayName("影音发声防假离线")]
    [Description("即使键鼠无操作，若声卡持续有声或正在播放媒体，一票判定为沉浸影音倾听，绝不误判离席，默认开启")]
    public bool EnableAudioAntiIdle { get; set; } = true;

    [DisplayName("监听Windows锁屏")]
    [Description("按Win+L或系统锁屏时，是否秒级记录锁屏并进入挂机状态，默认开启")]
    public bool EnableSessionLockWatch { get; set; } = true;

    [DisplayName("监听电源睡眠休眠")]
    [Description("电脑进入睡眠/休眠时，是否自动挂起黑盒并在唤醒时结算，默认开启")]
    public bool EnablePowerSuspendWatch { get; set; } = true;
    #endregion

    #region 5. 声卡与媒体检测 (SMTC + 进程级音频会话)
    [DisplayName("声卡活跃度黑盒")]
    [Description("是否静默监测声卡发声状态、持续时间及具体发声进程（不主动提醒，供实时切片查阅）")]
    public bool EnableAudioWatch { get; set; } = true;

    [DisplayName("声卡采样间隔(秒)")]
    [Description("声卡峰值分贝与进程级音频会话检测周期，默认5秒")]
    public int AudioCheckIntervalSeconds { get; set; } = 5;

    [DisplayName("声卡发声判定阈值")]
    [Description("峰值分贝超过此值判定为有声音输出，默认0.02")]
    public float AudioPeakThreshold { get; set; } = 0.02f;

    [DisplayName("短音/系统蜂鸣过滤门槛(秒)")]
    [Description("发声持续达到此秒数才记为有效音频事件，过滤微信提示音、弹窗蜂鸣等杂音碎片，默认5秒")]
    public int AudioFilterDurationSeconds { get; set; } = 5;

    [DisplayName("媒体记录黑盒")]
    [Description("是否通过Windows原生SMTC结合Core Audio进程级峰值静默记录B站、播客、网易云等正在播放的媒体与曲目")]
    public bool EnableMediaWatch { get; set; } = true;

    [DisplayName("媒体采样间隔(秒)")]
    [Description("媒体状态检测周期，默认5秒")]
    public int MediaCheckIntervalSeconds { get; set; } = 5;
    #endregion

    #region 6. 外设监督
    [DisplayName("外设监督")]
    [Description("是否监测游戏手柄与蓝牙外设的插拔与连接状态")]
    public bool EnableDeviceWatch { get; set; } = true;

    [DisplayName("外设变动主动提醒")]
    [Description("手柄与蓝牙外设连断时是否主动通知AI桌宠，默认开启")]
    public bool DeviceNotifyPrompt { get; set; } = true;
    #endregion

    #region 7. 核心四大主动通知桌宠设置 (阿瞒专属心灵相通)
    [DisplayName("1. 毫秒级归座唤醒通知")]
    [Description("离开达到离席阈值后，第一次触碰鼠标或按压键盘瞬间（微秒级边缘触发），立即主动唤醒桌宠睁眼迎接，默认开启")]
    public bool EnableReturnNotifyPrompt { get; set; } = true;

    [DisplayName("1. 归座唤醒提示词模板(短暂离开)")]
    [Description("短暂离开归座时发送给桌宠的提示词模板，支持 {AwayMinutes}、{TimeOfDay}、{ProcessName} 宏置换")]
    public string ReturnPromptTemplate { get; set; } = "阿瞒短暂离开 {AwayMinutes} 分钟后回到屏幕前了（当前时段：{TimeOfDay}）。";

    [DisplayName("1. 久别归座分界门槛(分钟)")]
    [Description("离开达到此分钟数及以上时，归座自动切换为「久别重逢」专属提示词模板，默认25分钟")]
    public int LongReturnThresholdMinutes { get; set; } = 25;

    [DisplayName("1. 久别重逢归座提示词模板")]
    [Description("离开较长时间（如吃饭、上课、外出）归座时的专属提示词模板，支持 {AwayMinutes}、{TimeOfDay}、{ProcessName} 宏置换")]
    public string LongReturnPromptTemplate { get; set; } = "阿瞒离开工位 {AwayMinutes} 分钟后终于回来啦（当前时段：{TimeOfDay}，焦点：{ProcessName}），请热情温柔地迎接他归座！";

    [DisplayName("2. 防误判真离席通知")]
    [Description("结合声卡发声与全屏防假离线，确认真走开达到门槛时，主动通知桌宠挂起与记录离席，默认开启")]
    public bool EnableIdleNotifyPrompt { get; set; } = true;

    [DisplayName("2. 真离席提示词模板")]
    [Description("确认离席时发送给桌宠的提示词文本模板，默认：阿瞒离开工位了。")]
    public string IdlePromptTemplate { get; set; } = "阿瞒离开工位了。";

    [DisplayName("3. 音频输出切换通知(耳机插拔)")]
    [Description("默认音频端点切换（如插拔耳机）时主动推送，便于桌宠秒级变轨语音/宿舍静默纯文本，默认开启")]
    public bool EnableAudioEndpointNotifyPrompt { get; set; } = true;

    [DisplayName("3. 耳机连接提示词模板")]
    [Description("检测到切为耳机时的提示词模板，支持 {DeviceName} 宏置换")]
    public string HeadphonePluggedPromptTemplate { get; set; } = "阿瞒已连接/切换到耳机（{DeviceName}），私密听音模式已就绪，可正常进行语音互动。";

    [DisplayName("3. 扬声器外放提示词模板")]
    [Description("检测到切为扬声器外放时的提示词模板，支持 {DeviceName} 宏置换")]
    public string HeadphoneUnpluggedPromptTemplate { get; set; } = "阿瞒已切换为扬声器外放（{DeviceName}），请注意周围环境，建议转为静默纯文本模式防外放打扰。";

    [DisplayName("3. 音频切换通用提示词模板")]
    [Description("无法确切区分为耳机或扬声器时的通用音频切换模板，支持 {DeviceName} 宏置换")]
    public string AudioEndpointGenericPromptTemplate { get; set; } = "默认音频输出设备已切换为：{DeviceName}。";

    [DisplayName("4. 暴躁甩鼠/卡点告警通知")]
    [Description("检测到高速剧烈反转甩动鼠标体征时主动通知桌宠温柔关切（遇到难题或Bug），默认开启")]
    public bool EnableRageShakeNotifyPrompt { get; set; } = true;

    [DisplayName("4. 甩鼠告警提示词模板")]
    [Description("甩鼠告警发送给桌宠的提示词模板，支持 {ProcessName} 与 {WindowTitle} 宏置换")]
    public string RageShakePromptTemplate { get; set; } = "检测到阿瞒在应用 [{ProcessName}] 中频繁高速往复晃动鼠标，疑似遇到难题、报错或思路卡壳，请温柔关切安抚。";

    [DisplayName("5. 指定应用打开/切入提醒开关")]
    [Description("当打开或切入监控名单中的特定应用时，是否主动通知桌宠，默认开启")]
    public bool EnableAppOpenNotifyPrompt { get; set; } = true;

    [DisplayName("5. 监控提醒的目标应用名单")]
    [Description("需要触发提醒的应用进程名或窗口标题关键词（多个用英文逗号分隔，如：SumatraPDF,Yuanshen,StarRail,steam,Code；留空则不触发）")]
    public string WatchedAppsCsv { get; set; } = "";

    [DisplayName("5. 同应用提醒冷却时间(秒)")]
    [Description("同一个目标应用触发提醒后的防刷屏冷却时间（防止Alt+Tab来回切屏重复提醒），默认300秒(5分钟)")]
    public int AppOpenNotifyCooldownSeconds { get; set; } = 300;

    [DisplayName("5. 指定应用打开提示词模板")]
    [Description("检测到打开/切入目标应用时发送给桌宠的提示词模板，支持 {ProcessName} 与 {WindowTitle} 宏置换")]
    public string AppOpenPromptTemplate { get; set; } = "检测到阿瞒打开并切入了目标应用 [{ProcessName}]（窗口：{WindowTitle}）。";

    [DisplayName("6. 自定义按键/组合键监测提醒开关")]
    [Description("检测到按下指定的自定义按键或组合键达到设定次数时，是否主动通知桌宠，默认开启")]
    public bool EnableKeyWatchNotifyPrompt { get; set; } = true;

    [DisplayName("6. 自定义监测按键/组合键列表")]
    [Description("支持单键（如 Back,Esc,F5,Delete,Enter,Space,A-Z）或组合键（如 Ctrl+Z,Ctrl+S,Alt+Q,Ctrl+Shift+F），多个用逗号分隔；也支持单独指定某键次数如 Back:20,Ctrl+Z:5")]
    public string WatchedKeysSpec { get; set; } = "Back:20, Ctrl+Z:8";

    [DisplayName("6. 自定义按键触发次数门槛(次)")]
    [Description("在统计时间窗内按下目标按键/组合键达到此次数时触发提醒（设为 1 则按一下即提醒；设为 20 则连按20次提醒），默认10次")]
    public int KeyWatchTriggerCount { get; set; } = 10;

    [DisplayName("6. 自定义按键统计时间窗(秒)")]
    [Description("统计自定义按键累计按压次数的时间窗口（秒），默认30秒")]
    public int KeyWatchWindowSeconds { get; set; } = 30;

    [DisplayName("6. 自定义按键提醒冷却(秒)")]
    [Description("同一自定义按键触发提醒后的防刷屏冷却时间（秒），默认60秒")]
    public int KeyWatchCooldownSeconds { get; set; } = 60;

    [DisplayName("6. 自定义按键提醒提示词模板")]
    [Description("触发自定义按键监测时发送给桌宠的提示词模板，支持 {KeyName}、{Count}、{WindowSeconds}、{ProcessName}、{WindowTitle} 宏置换")]
    public string KeyWatchPromptTemplate { get; set; } = "检测到阿瞒在应用 [{ProcessName}] 中 {WindowSeconds}秒内按下监测按键 [{KeyName}] 达 {Count} 次，请根据当前场景主动关怀或响应。";

    [DisplayName("7. 连续久坐/防疲劳提醒开关")]
    [Description("当连续在电脑前活跃操作达到设定分钟数（期间未离席休息）时，是否主动通知桌宠提醒休息，默认开启")]
    public bool EnableContinuousWorkNotifyPrompt { get; set; } = true;

    [DisplayName("7. 连续久坐提醒门槛(分钟)")]
    [Description("连续活跃未离席达到此分钟数时触发一次防疲劳提醒，默认90分钟")]
    public int ContinuousWorkThresholdMinutes { get; set; } = 90;

    [DisplayName("7. 连续久坐提醒提示词模板")]
    [Description("触发连续久坐防疲劳提醒时发送给桌宠的提示词模板，支持 {Minutes}、{ProcessName}、{WindowTitle} 宏置换")]
    public string ContinuousWorkPromptTemplate { get; set; } = "阿瞒已经连续在电脑前专注操作满 {Minutes} 分钟没有离开工位休息了（当前应用：{ProcessName}），请温柔提醒他喝口水、活动一下颈椎和眼睛。";

    [DisplayName("8. 深夜修仙/熬夜温柔哄睡开关")]
    [Description("在设定的深夜时段检测到仍在高频打字或活跃操作时，是否触发一次温柔哄睡提醒（每晚最多触发一次，绝不唠叨），默认开启")]
    public bool EnableLateNightNotifyPrompt { get; set; } = true;

    [DisplayName("8. 深夜哄睡触发时间(HH:mm)")]
    [Description("深夜开始检测熬夜的时间点（24小时制，如 00:30 或 01:00，有效窗口至凌晨 05:00），默认 01:00")]
    public string LateNightTriggerTime { get; set; } = "01:00";

    [DisplayName("8. 深夜哄睡提示词模板")]
    [Description("触发深夜修仙提醒时发送给桌宠的提示词模板，支持 {CurrentTime}、{ProcessName}、{WindowTitle} 宏置换")]
    public string LateNightPromptTemplate { get; set; } = "现在已经是深夜 {CurrentTime} 了，阿瞒还在应用 [{ProcessName}] 前忙碌，请温柔心疼地哄他早点保存进度去休息，别熬太晚。";

    [DisplayName("9. 允许 AI 主动自建触发规则")]
    [Description("开启后允许搭载本模块的角色在你没有明确要求的情况下，主动依据体征条件为你创建自定义触发规则；关闭后角色不会主动创建，只在你明确提出时才建；该约束通过提示词注入实现，不做硬拦截，角色仍可被你明确要求后创建。默认开启")]
    public bool EnableAiSelfCreateRule { get; set; } = true;

    [DisplayName("9. 自定义触发规则开关")]
    [Description("是否允许本角色接收自己创建的自定义触发规则播报。关闭后该角色的自定义规则一律静默，不影响其它角色。默认开启（规则本身由你显式创建，属于明确授权）")]
    public bool EnableCustomTriggerNotifyPrompt { get; set; } = true;

    [DisplayName("9. 自定义触发同秒合并播报")]
    [Description("同一秒内多条自定义规则同时命中时，是否合并成一条播报。关闭则只播报优先级最高的一条，其余记入日志。默认关闭")]
    public bool MergeCustomTriggerPrompts { get; set; } = false;

    [DisplayName("9. 自定义触发全局最小播报间隔(秒)")]
    [Description("两次自定义规则播报之间的最小间隔，防止连环轰炸。默认 15 秒")]
    public int CustomTriggerMinIntervalSeconds { get; set; } = 15;

    [DisplayName("全屏沉浸免打扰延后护盾")]
    [Description("当处于全屏游戏或全屏看视频时，非紧急提醒（如久坐防疲劳、深夜哄睡）自动静默排队，待退出全屏切回桌面瞬间再温柔送达，默认开启")]
    public bool DeferNotifyWhenFullscreen { get; set; } = true;

    [DisplayName("主动通知派发方式")]
    [Description("主动事件派发给角色的方式：Poke(轻量注入系统提示，角色在合适时机自然回应，不强行打断，推荐) 或 Chat(直接触发一轮主动对话)")]
    public string NotifyDispatchMode { get; set; } = "Poke";
    #endregion

    #region 8. 剪贴板长期记忆与截图视觉闪存库
    [DisplayName("剪贴板长期记忆黑盒")]
    [Description("是否静默记录复制的文本历史与时间戳（永久落盘、自动绑定窗口上下文、全文保存、绝不主动提醒）")]
    public bool EnableClipboardWatch { get; set; } = true;

    [DisplayName("剪贴板最小字数门槛")]
    [Description("复制文本字符数达到此值才记录入库，过滤单字符或误触复制，默认2字")]
    public int ClipboardMinLength { get; set; } = 2;

    [DisplayName("敏感Token自动脱敏")]
    [Description("复制内容中若检测到疑似 GitHub Token、OpenAI API Key 等密钥凭据时自动打码脱敏保护，默认开启")]
    public bool ClipboardFilterSensitive { get; set; } = true;

    [DisplayName("连续重复复制去重")]
    [Description("若多次连续复制相同文本，仅保留首条记录，防止产生冗余副本，默认开启")]
    public bool ClipboardDedup { get; set; } = true;

    [DisplayName("截图视觉闪存库(绑定上下文)")]
    [Description("使用Win+Shift+S或截图工具复制纯图像时，是否自动压缩转存并打上当前前台与伴读窗口标题标签，方便按软件回溯错题与报错图")]
    public bool EnableScreenshotFlashWatch { get; set; } = true;

    [DisplayName("截图JPEG压缩质量(10-100)")]
    [Description("截图视觉闪存库落盘时的JPEG质量（80兼顾高清晰度与百KB级轻量体积），默认80")]
    public int ScreenshotJpegQuality { get; set; } = 80;
    #endregion

    #region 9. 每日专注画像与作息
    [DisplayName("每日专注画像聚合")]
    [Description("是否按天统计各软件停留总时长、全屏专注深度与同屏伴读时长（长期记忆永久保存）")]
    public bool EnableDailyFocusProfile { get; set; } = true;

    [DisplayName("专注排行排除名单")]
    [Description("排除计入专注排行与活跃统计的系统进程名称（以英文逗号分隔）")]
    public string ProfileIgnoreProcesses { get; set; } = "Alife.Client.Windows,SearchHost,explorer,Program Manager,csrss";

    [DisplayName("画像定时自动存盘(分钟)")]
    [Description("专注画像与剪贴板在后台定时自动落盘的间隔，防止意外关机数据丢失，默认10分钟")]
    public int ProfileAutoSaveIntervalMinutes { get; set; } = 10;
    #endregion

    #region 10. 工作区文件脉搏与下载管家
    [DisplayName("文件脉搏与下载目录管家")]
    [Description("是否通过Win32原生ReadDirectoryChangesW异步监听下载目录新文件落盘与工作区Ctrl+S保存产出计数（纯元数据，零CPU轮询）")]
    public bool EnableFilePulseWatch { get; set; } = true;

    [DisplayName("文件脉搏监听目录列表")]
    [Description("需要监听新文件落地与Ctrl+S保存节拍的目录路径（以英文逗号分隔），默认 D:\\awa 与 D:\\ded")]
    public string WatchedDirectoriesCsv { get; set; } = @"D:\awa,D:\ded";
    #endregion

    #region 11. 内存与性能健康
    [DisplayName("瞬态队列内存容量(条)")]
    [Description("内存中保留的近期离散事件（打字、滚轮、应用切屏）最大条数，超出先进先出，默认300条")]
    public int MaxTransientEventsCount { get; set; } = 300;
    #endregion

    #region 12. 角色提示词与上下文心智注入
    [DisplayName("12. 是否向角色上下文注入功能说明")]
    [Description("总开关。关闭后本模块不再向角色注入任何提示词说明，功能仍可被显式调用，但角色不会知道它存在。默认开启")]
    public bool EnablePromptInjection { get; set; } = true;

    [DisplayName("自定义黑盒提示词模板(留空使用默认)")]
    [Description("若填写则使用自定义提示词替换默认功能说明注入；留空则使用官方全功能标准指引")]
    public string CustomPrompt { get; set; } = "";

    [DisplayName("12. 提示词注入档位")]
    [Description("精简=只注入功能概述、调用时机与函数要点，角色需要细节时自行输出模块标签加载完整文档，占用上下文最少，推荐；完整=注入全部能力速查与使用准则，字多但一次说全；关闭=等同于总开关关闭。自定义模板非空时一律以自定义模板为准。默认精简")]
    public string PromptInjectionStyle { get; set; } = "精简";
    #endregion
}
