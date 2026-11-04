using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;

namespace Alife.BlackBoxSuite;


public partial class BlackBoxSuiteModule
{
    /// <summary>AI 自建规则的默认归属标记</summary>
    private const string AiCreator = "AI";

    /// <summary>每个角色规则条数上限，防止 AI 批量乱建</summary>
    private const int MaxRulesPerRole = 30;

    /// 文本型信号集合，用于区分值与文本解析分支
    private static readonly HashSet<string> TriggerTextSignals = new(StringComparer.OrdinalIgnoreCase)
    {
        "ForegroundApp", "ForegroundTitle", "AudioProcess", "MediaTitle",
        "ClipboardText", "LastFileName", "LastFileExt"
    };

    private string MyRoleName => Character?.Name ?? "";

    /// <summary>
    /// 解析条件文本。格式：信号 运算符 值，多条用分号分隔。
    /// 例：IdleSeconds&gt;=300;IsFullscreen==0
    /// </summary>
    private static bool TryParseConditions(string text, out List<TriggerCondition> list, out string error)
{
    list = new List<TriggerCondition>();
    error = "";
    if (string.IsNullOrWhiteSpace(text))
    {
        error = "条件不能为空";
        return false;
    }

    var parts = text.Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries);
    foreach (var raw in parts)
    {
        string part = raw.Trim();
        if (part.Length == 0) continue;

        // 时长后缀： at 30s 或 at 30s grace 5s
        double durationSeconds = 0;
        double graceSeconds = 0;
        var dm = System.Text.RegularExpressions.Regex.Match(
            part,
            @"\s+at\s+([0-9]+(?:\.[0-9]+)?)s(?:\s+grace\s+([0-9]+(?:\.[0-9]+)?)s)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (dm.Success)
        {
            double.TryParse(dm.Groups[1].Value, out durationSeconds);
            if (dm.Groups[2].Success) double.TryParse(dm.Groups[2].Value, out graceSeconds);
            part = part.Substring(0, dm.Index).Trim();
            if (part.Length == 0)
            {
                error = "条件缺少主体：" + raw.Trim();
                return false;
            }
            if (durationSeconds <= 0)
            {
                error = "持续时长必须大于零：" + raw.Trim();
                return false;
            }
            if (graceSeconds <= 0) graceSeconds = 3;
        }

        string opText = "";
        string[] ops = { ">=", "<=", "!=", "==", "包含", "contains", ">", "<" };
        int opIndex = -1;
        foreach (var op in ops)
        {
            opIndex = part.IndexOf(op, StringComparison.OrdinalIgnoreCase);
            if (opIndex > 0) { opText = op; break; }
        }
        if (opIndex <= 0)
        {
            error = "条件格式错误，缺少比较符：" + part;
            return false;
        }

        string signalText = part.Substring(0, opIndex).Trim();
        string valueText = part.Substring(opIndex + opText.Length).Trim();

        if (!Enum.TryParse<TriggerSignal>(signalText, true, out var signal))
        {
            error = "未知信号：" + signalText;
            return false;
        }

        TriggerOperator op2 = opText.ToLowerInvariant() switch
        {
            ">=" => TriggerOperator.GreaterOrEqual,
            "<=" => TriggerOperator.LessOrEqual,
            "!=" => TriggerOperator.NotEqual,
            "==" => TriggerOperator.Equal,
            ">" => TriggerOperator.Greater,
            "<" => TriggerOperator.Less,
            "包含" => TriggerOperator.Contains,
            "contains" => TriggerOperator.Contains,
            _ => TriggerOperator.Equal
        };

        if (valueText.Length == 0)
        {
            error = "条件缺少比较值：" + part;
            return false;
        }

        var cond = new TriggerCondition
        {
            Signal = signal.ToString(),
            Operator = op2.ToString(),
            DurationSeconds = durationSeconds,
            GraceSeconds = durationSeconds > 0 ? graceSeconds : 0
        };

        if (TriggerTextSignals.Contains(signal.ToString()))
        {
            cond.Value = 0;
            cond.TextValue = valueText;
        }
        else
        {
            if (op2 == TriggerOperator.Contains)
            {
                error = "包含运算符只能用于文本型信号：" + part;
                return false;
            }
            if (!double.TryParse(valueText, out double num))
            {
                error = "数值型信号的值必须是数字：" + part;
                return false;
            }
            cond.Value = num;
            cond.TextValue = null;
        }
        list.Add(cond);
    }

    if (list.Count == 0)
    {
        error = "没有解析到任何有效条件";
        return false;
    }
    return true;
}

    [XmlFunction(FunctionMode.OneShot)]
    [Description("【新增自定义触发规则】让小黑盒在满足指定体征条件时主动播报提示。注意：纯到点闹钟请改用 SystemEventBoost 的定时任务，不要用本函数，本函数的独有价值在于叠加体征条件（到点但人不在工位就不响、全屏打游戏就延后、久坐九十分钟且没离席才提醒）。条件文本格式为「信号 运算符 值，多条用分号分隔」。常用信号示例：IdleSeconds（空闲秒数）、ContinuousWorkMinutes（连续工作分钟）、TodayActiveMinutes（今日活跃分钟）、KeystrokesPerMinute（每分钟击键）、IsFullscreen（是否全屏 1 或 0）、MinuteOfDay（当天第几分钟）、ForegroundApp（前台进程名，文本比较）。条件示例：IdleSeconds>=300;IsFullscreen==0 表示空闲满五分钟且不在全屏。播报模板支持宏置换：{RuleName} {CurrentTime} {IdleSeconds} {ContinuousWorkMinutes} {TodayActiveMinutes} {KeystrokesPerMinute} {ProcessName} {WindowTitle} {MediaTitle}。生命周期：Once 一次性、Count 限次（配合 maxTimes）、Daily 每天、Permanent 永久。建议先传 dryRun 为 true 预演一次，看当前快照是否满足条件，避免规则建了不响。持续型条件：在条件末尾加 at 加秒数加 s，例如 IdleSeconds>=300 at 30s 表示空闲满五分钟且连续保持三十秒才触发，可选 grace 加秒数加 s 自定义宽限（默认三秒）。包含运算符：文本型信号可写 包含 或 contains，例如 ForegroundTitle包含B站。")]
    public Task AddTrigger(
        [Description("规则名称，简短可读，如「久坐提醒」")] string name,
        [Description("条件文本，格式：信号 运算符 值，多条用分号分隔。如 IdleSeconds>=300;IsFullscreen==0")] string conditions,
        [Description("条件全部满足时发送给桌宠的播报提示词，支持宏置换，如「阿瞒已经连续工作 {ContinuousWorkMinutes} 分钟了，请温柔提醒他站起来活动一下」")] string prompt,
        [Description("触发生命周期：Once（一次性）、Count（限次，需配 maxTimes）、Daily（每天，需配 hour/minute 条件自控）、Permanent（永久）。默认 Once")] string lifecycle = "Once",
        [Description("限次模式下最多触发几次，默认 1")] int maxTimes = 1,
        [Description("冷却秒数，两次触发之间的最小间隔，默认 300")] int cooldownSeconds = 300,
        [Description("优先级，数字越大越优先，同秒多规则命中时高的先播报，默认 0")] int priority = 0,
        [Description("派发方式：Poke（戳一戳，默认）或 Chat（正式对话）")] string dispatchMode = "Poke",
        [Description("每日触发上限，0 表示不限。用于「每天最多提醒几次」这类约束，跨日自动清零。默认 0")] int dailyMaxTimes = 0,
        [Description("预演开关。true 表示只试算不落库，返回当前快照是否满足条件以及各项信号实际值，用于验证规则是否合理。建议首次创建先预演")] bool dryRun = false
    )
    {
        var engine = _engine ?? BlackBoxEngine.Instance;
        if (engine == null) { interactor.Poke("小黑盒引擎未就绪。"); return Task.CompletedTask; }

        string role = MyRoleName;
        if (string.IsNullOrWhiteSpace(role)) { interactor.Poke("无法识别当前角色名。"); return Task.CompletedTask; }

        if (!TryParseConditions(conditions, out var condList, out string err))
        {
            interactor.Poke("条件解析失败：" + err);
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            interactor.Poke("播报提示词不能为空。");
            return Task.CompletedTask;
        }

        TriggerLifecycle life = TriggerLifecycle.Once;
        if (!string.IsNullOrWhiteSpace(lifecycle))
        {
            if (!Enum.TryParse<TriggerLifecycle>(lifecycle, true, out life)) life = TriggerLifecycle.Once;
        }

        DateTime now = DateTime.Now;
        var rule = new TriggerRule
        {
            Id = Guid.NewGuid().ToString("N").Substring(0, 8),
            Name = name ?? "未命名规则",
            OwnerRole = role,
            Conditions = condList,
            Prompt = prompt,
            DispatchMode = string.IsNullOrWhiteSpace(dispatchMode) ? "Poke" : dispatchMode,
            Lifecycle = life.ToString(),
            MaxTimes = Math.Max(1, maxTimes),
            RemainingTimes = Math.Max(1, maxTimes),
            CooldownSeconds = Math.Max(0, cooldownSeconds),
            Priority = priority,
            Enabled = true,
            Paused = false,
            CreatedBy = AiCreator,
            CreatedAt = now,
            FireCount = 0,
            DailyMaxTimes = Math.Max(0, dailyMaxTimes)
        };

        if (dryRun)
        {
            var snap = engine.CollectSnapshot(now);
            var res = engine.EvaluateRule(rule, snap, now, out string detail);
            var sb = new System.Text.StringBuilder();
            sb.Append("预演结果：").Append(res).Append('\n');
            sb.Append("条件明细：").Append(detail).Append('\n');
            sb.Append("当前快照：").Append(BlackBoxEngine.SummarizeSnapshot(snap));
            interactor.Poke(sb.ToString());
            return Task.CompletedTask;
        }

        var rules = engine.GetRoleRules(role);
        if (rules.Count(r => r.Enabled) >= MaxRulesPerRole)
        {
            interactor.Poke("当前角色启用中的自定义规则已达上限 " + MaxRulesPerRole + " 条，请先清理旧规则。");
            return Task.CompletedTask;
        }

        rules.Add(rule);
        if (!engine.SetRoleRules(role, rules))
        {
            interactor.Poke("规则落盘失败，请检查存储目录权限。");
            return Task.CompletedTask;
        }

        string dailyTip = rule.DailyMaxTimes > 0 ? "，每日上限 " + rule.DailyMaxTimes + " 次" : "";
        interactor.Poke("已创建规则 [" + rule.Id + "] " + rule.Name + "，条件：" + conditions + "，生命周期：" + life + dailyTip);
        return Task.CompletedTask;
    }
}


public partial class BlackBoxSuiteModule
{
    [XmlFunction(FunctionMode.OneShot)]
    [Description("【查看自定义触发规则】列出当前角色已创建的全部自定义触发规则，含编号、名称、条件、生命周期、剩余次数、冷却、优先级与已触发次数。")]
    public Task ListTriggers()
    {
        var engine = _engine ?? BlackBoxEngine.Instance;
        if (engine == null) { interactor.Poke("小黑盒引擎未就绪。"); return Task.CompletedTask; }

        string role = MyRoleName;
        if (string.IsNullOrWhiteSpace(role)) { interactor.Poke("无法识别当前角色名。"); return Task.CompletedTask; }

        var rules = engine.GetRoleRules(role);
        if (rules.Count == 0)
        {
            interactor.Poke("当前角色还没有任何自定义触发规则。");
            return Task.CompletedTask;
        }

        var sb = new System.Text.StringBuilder();
        sb.Append("自定义触发规则共 ").Append(rules.Count).Append(" 条：\n");
        foreach (var r in rules)
        {
            string cond = string.Join(" 且 ", r.Conditions.Select(c =>
            {
                string v = c.TextValue ?? c.Value.ToString("F0");
                return c.Signal + OperatorToSymbol(c.Operator) + v + (c.DurationSeconds > 0 ? " 持续 " + c.DurationSeconds.ToString("F0") + " 秒" : "");
            }));
            sb.Append('[').Append(r.Id).Append("] ").Append(r.Name);
            sb.Append(" | 条件：").Append(cond);
            sb.Append(" | 周期：").Append(r.Lifecycle);
            if (!string.IsNullOrEmpty(r.Lifecycle) && r.Lifecycle.Equals("Count", StringComparison.OrdinalIgnoreCase))
                sb.Append(" 剩余 ").Append(r.RemainingTimes).Append('/').Append(r.MaxTimes);
            sb.Append(" | 冷却 ").Append(r.CooldownSeconds).Append(" 秒");
            sb.Append(" | 优先级 ").Append(r.Priority);
            sb.Append(" | 已触发 ").Append(r.FireCount).Append(" 次");
            if (r.DailyMaxTimes > 0)
            {
                int todayCount = string.Equals(r.TodayDateKey, DateTime.Now.ToString("yyyy-MM-dd"), StringComparison.Ordinal) ? r.TodayFireCount : 0;
                sb.Append(" | 今日 ").Append(todayCount).Append('/').Append(r.DailyMaxTimes);
            }
            if (r.Paused) sb.Append(" | 已暂停");
            if (!r.Enabled) sb.Append(" | 已停用");
            sb.Append('\n');
        }
        interactor.Poke(sb.ToString().TrimEnd());
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("【删除自定义触发规则】按编号删除当前角色的一条自定义触发规则。编号取自 ListTriggers 返回的方括号内字符。传 all 为 true 时清空当前角色全部自定义规则。")]
    public Task RemoveTrigger(
        [Description("要删除的规则编号，取自 ListTriggers 返回的方括号内 8 位字符；留空且 all 为 true 时清空全部")] string id = "",
        [Description("是否清空当前角色全部自定义规则，默认 false")] bool all = false
    )
    {
        var engine = _engine ?? BlackBoxEngine.Instance;
        if (engine == null) { interactor.Poke("小黑盒引擎未就绪。"); return Task.CompletedTask; }

        string role = MyRoleName;
        if (string.IsNullOrWhiteSpace(role)) { interactor.Poke("无法识别当前角色名。"); return Task.CompletedTask; }

        var rules = engine.GetRoleRules(role);

        if (all)
        {
            if (rules.Count == 0) { interactor.Poke("当前角色没有可删除的规则。"); return Task.CompletedTask; }
            int n = rules.Count;
            engine.SetRoleRules(role, new List<TriggerRule>());
            interactor.Poke("已清空当前角色全部 " + n + " 条自定义触发规则。");
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            interactor.Poke("请提供要删除的规则编号，或将 all 设为 true 清空全部。");
            return Task.CompletedTask;
        }

        var target = rules.FirstOrDefault(r => string.Equals(r.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
        if (target == null)
        {
            interactor.Poke("未找到编号为 " + id + " 的规则。");
            return Task.CompletedTask;
        }

        rules.Remove(target);
        engine.SetRoleRules(role, rules);
        interactor.Poke("已删除规则 [" + target.Id + "] " + target.Name);
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("【查询可用触发信号清单】列出自定义触发规则支持的全部信号名称、类型（数值或文本或事件）、含义与示例用法。创建规则前先查这份清单，避免写错信号名。")]
    public Task ListSignals()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("自定义触发可用信号清单：\n");
        sb.Append("数值型（写法：信号名 运算符 数字，运算符支持 > < >= <= == !=）：\n");
        sb.Append("IdleSeconds 系统空闲秒数\n");
        sb.Append("ForegroundDwellSeconds 前台窗口已驻留秒数\n");
        sb.Append("IsFullscreen 是否全屏，1 是 0 否\n");
        sb.Append("KeystrokesPerMinute 每分钟击键数\n");
        sb.Append("BackspacePerMinute 每分钟退格数\n");
        sb.Append("WheelPerMinute 每分钟滚轮格数\n");
        sb.Append("MousePixelsPerMinute 每分钟鼠标移动像素\n");
        sb.Append("ClickBurstCount 连点次数\n");
        sb.Append("RageShakeCount 暴躁甩鼠次数\n");
        sb.Append("AudioPeak 音频峰值 0 到 1\n");
        sb.Append("IsAudioPlaying 是否在发声，1 是 0 否\n");
        sb.Append("IsHeadphone 是否耳机输出，1 是 0 否\n");
        sb.Append("TodayActiveMinutes 今日累计活跃分钟\n");
        sb.Append("TodayFullscreenMinutes 今日累计全屏分钟\n");
        sb.Append("ContinuousWorkMinutes 连续工作分钟数\n");
        sb.Append("ClipboardLength 剪贴板文本长度\n");
        sb.Append("MinuteOfDay 当天第几分钟，0 到 1439\n");
        sb.Append("DayOfWeek 星期几，1 到 7\n");
        sb.Append("文本型（写法：信号名 == 文本 或 信号名 != 文本）：\n");
        sb.Append("ForegroundApp 前台进程名，如 chrome\n");
        sb.Append("ForegroundTitle 前台窗口标题\n");
        sb.Append("AudioProcess 发声进程名\n");
        sb.Append("MediaTitle 当前播放曲目\n");
        sb.Append("ClipboardText 剪贴板文本内容\n");
        sb.Append("LastFileName 最近落盘文件名\n");
        sb.Append("LastFileExt 最近落盘文件后缀\n");
        sb.Append("事件型（写法：信号名 == 1，只看本轮是否发生，不看阈值。已实装三项，其余为预留位暂不产生事件）：\n");
        sb.Append("AppOpened 有应用切到前台（已实装）\n");
        sb.Append("AppClosed 前台应用切走（已实装）\n");
        sb.Append("FileDropped 工作区有新文件落盘（已实装）\n");
        sb.Append("ClipboardChanged 剪贴板有新增内容（预留）\n");
        sb.Append("ScreenLocked 屏幕锁定（预留）\n");
        sb.Append("ScreenUnlocked 屏幕解锁（预留）\n");
        sb.Append("SystemResumed 系统从休眠唤醒（预留）\n");
        sb.Append("SystemSuspending 系统进入休眠挂起（预留）\n");
        sb.Append("DeviceAttached 外设接入（预留）\n");
        sb.Append("DeviceDetached 外设断开（预留）\n");
        sb.Append("AudioEndpointChanged 音频输出端点切换（预留）\n");
        sb.Append("事件型示例：AppOpened==1 表示这一秒有应用切到前台，一次切换只触发一次。\n");
        sb.Append("示例：IdleSeconds>=300;IsFullscreen==0 表示空闲满五分钟且不在全屏。\n");
        sb.Append("提示：纯到点闹钟请用 SystemEventBoost 的定时任务，本功能的价值在于叠加体征条件。");
        interactor.Poke(sb.ToString());
        return Task.CompletedTask;
    }
}


public partial class BlackBoxSuiteModule
{
    /// 把运算符枚举名转成可读符号，供规则列表展示
    internal static string OperatorToSymbol(string op)
    {
        switch (op)
        {
            case "Greater": return ">";
            case "Less": return "<";
            case "GreaterOrEqual": return ">=";
            case "LessOrEqual": return "<=";
            case "Equal": return "==";
            case "NotEqual": return "!=";
            case "Contains": return "包含";
            default: return op ?? "";
        }
    }
}
