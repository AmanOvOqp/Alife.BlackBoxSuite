using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Alife.Framework;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;

namespace Alife.BlackBoxSuite;

public class BlackBoxSuiteUI : ModuleUIBase<BlackBoxSuiteModule, BlackBoxConfig>, IDisposable
{
    private string _activeTab = "all";
    private string _saveToast = "";
    private bool _isRecordingKey = false;
    private int _recordDefaultCount = 1;
    private List<(string ProcessName, string WindowTitle)> _runningApps = new();
    private int _seq = 0;

    private BlackBoxConfig Config => Configuration;

    protected override void OnInitialized()
    {
        RefreshRunningApps();
    }

    public void Dispose()
    {
        if (_isRecordingKey)
        {
            try { BlackBoxEngine.Instance?.CancelKeyRecording(); } catch { }
            _isRecordingKey = false;
        }
    }

    private void RefreshRunningApps()
    {
        try
        {
            _runningApps = BlackBoxEngine.GetRunningVisibleApps();
        }
        catch
        {
            _runningApps = new();
        }
    }

    private void Save(string? customToast = null)
    {
        Module?.SyncConfigToEngine();
        if (Module == null && BlackBoxEngine.Instance != null && Configuration != null)
        {
            BlackBoxEngine.Instance.UpdateConfig(Configuration);
            BlackBoxEngine.Instance.RefreshWatchersIfNeeded();
        }
        _saveToast = customToast ?? $"✨ 已实时热同步至引擎 ({DateTime.Now:HH:mm:ss})，离开前别忘点底部保存哦~";
        StateHasChanged();
    }

    private void FireTestPrompt(string title, string template, string? keyName = null, int count = 1)
    {
        var eng = BlackBoxEngine.Instance;
        if (eng == null)
        {
            _saveToast = "⚠️ 引擎尚未启动，请先激活角色后再试发~";
            StateHasChanged();
            return;
        }
        eng.TriggerTestNotification(template, keyName, count);
        _saveToast = $"⚡ 已向桌宠试发 [{title}] 提醒 ({DateTime.Now:HH:mm:ss})";
        StateHasChanged();
    }

    private void AddWatchedApp(string procName)
    {
        if (string.IsNullOrWhiteSpace(procName)) return;
        var list = (Config.WatchedAppsCsv ?? "")
            .Split(new[] { ',', ';', '，', '|' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();

        if (!list.Contains(procName, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(procName);
            Config.WatchedAppsCsv = string.Join(", ", list);
            Save($"➕ 已将 [{procName}] 加入监控应用名单！");
        }
        else
        {
            list.RemoveAll(x => x.Equals(procName, StringComparison.OrdinalIgnoreCase));
            Config.WatchedAppsCsv = string.Join(", ", list);
            Save($"➖ 已将 [{procName}] 从监控名单移除");
        }
    }

    private void ToggleKeyRecording()
    {
        var eng = BlackBoxEngine.Instance;
        if (eng == null)
        {
            _saveToast = "⚠️ 请先激活角色启动黑盒钩子后再录制按键~";
            StateHasChanged();
            return;
        }

        if (_isRecordingKey)
        {
            eng.CancelKeyRecording();
            _isRecordingKey = false;
            _saveToast = "已取消按键录制";
            StateHasChanged();
            return;
        }

        _isRecordingKey = true;
        _saveToast = "🎹 请直接在键盘上按下你想监测的单键或组合键 (如 Ctrl+Z / F5 / Alt+Q)...";
        StateHasChanged();

        eng.StartOneShotKeyRecording(combo =>
        {
            _isRecordingKey = false;
            int cnt = Math.Max(1, _recordDefaultCount);
            string newRule = $"{combo}:{cnt}";
            var list = (Config.WatchedKeysSpec ?? "")
                .Split(new[] { ',', ';', '，', '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            // 替换同名按键或追加
            list.RemoveAll(x => x.Split(':')[0].Trim().Equals(combo, StringComparison.OrdinalIgnoreCase));
            list.Add(newRule);
            Config.WatchedKeysSpec = string.Join(", ", list);

            InvokeAsync(() =>
            {
                Save($"🎹 成功录制并添加按键规则：[{newRule}]！");
            });
        });
    }

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        if (Configuration == null)
        {
            b.OpenElement(0, "div");
            b.AddContent(1, "🌸 星穹感知中枢配置加载中...");
            b.CloseElement();
            return;
        }
        _seq = 0;

        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-root");

        b.AddMarkupContent(Next(), @"<style>
    .bb-root {
        display: flex;
        flex-direction: column;
        gap: 14px;
        padding: 6px 4px 22px 4px;
        font-family: ""Microsoft YaHei"", ""HarmonyOS Sans SC"", ""PingFang SC"", system-ui, -apple-system, sans-serif;
        color: #2b2d42;
    }
    .bb-hero {
        position: relative;
        overflow: hidden;
        border-radius: 18px;
        padding: 16px 20px;
        background: linear-gradient(135deg, rgba(255, 240, 246, 0.95) 0%, rgba(243, 232, 255, 0.95) 50%, rgba(224, 242, 254, 0.95) 100%);
        border: 1.5px solid rgba(236, 72, 153, 0.28);
        box-shadow: 0 10px 26px -8px rgba(236, 72, 153, 0.18);
    }
    .bb-hero-top {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 12px;
        flex-wrap: wrap;
    }
    .bb-brand {
        display: flex;
        align-items: center;
        gap: 12px;
    }
    .bb-logo {
        width: 42px;
        height: 42px;
        border-radius: 14px;
        background: linear-gradient(135deg, #ec4899 0%, #8b5cf6 55%, #06b6d4 100%);
        color: #fff;
        display: flex;
        align-items: center;
        justify-content: center;
        font-size: 21px;
        box-shadow: 0 6px 14px rgba(236, 72, 153, 0.35);
    }
    .bb-title {
        font-size: 16.5px;
        font-weight: 800;
        background: linear-gradient(90deg, #db2777 0%, #7c3aed 55%, #0284c7 100%);
        -webkit-background-clip: text;
        -webkit-text-fill-color: transparent;
        letter-spacing: 0.4px;
    }
    .bb-subtitle {
        font-size: 11.5px;
        color: #6b7280;
        margin-top: 2px;
    }
    .bb-live-grid {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(210px, 1fr));
        gap: 8px;
        margin-top: 12px;
        padding-top: 12px;
        border-top: 1px dashed rgba(236, 72, 153, 0.25);
    }
    .bb-live-pill {
        background: rgba(255, 255, 255, 0.82);
        border: 1px solid rgba(139, 92, 246, 0.22);
        border-radius: 10px;
        padding: 6px 10px;
        font-size: 11.5px;
        display: flex;
        flex-direction: column;
        gap: 2px;
    }
    .bb-live-label {
        font-size: 10.5px;
        font-weight: 700;
        color: #9333ea;
    }
    .bb-live-val {
        color: #1f2937;
        font-weight: 600;
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
    }
    .bb-tabs {
        display: flex;
        flex-wrap: wrap;
        gap: 7px;
        padding: 6px 8px;
        border-radius: 14px;
        background: linear-gradient(90deg, rgba(253, 242, 248, 0.9), rgba(245, 243, 255, 0.9));
        border: 1px solid rgba(236, 72, 153, 0.2);
    }
    .bb-tab {
        padding: 6px 13px;
        border-radius: 10px;
        font-size: 12px;
        font-weight: 700;
        cursor: pointer;
        border: 1px solid transparent;
        background: rgba(255, 255, 255, 0.75);
        color: #4b5563;
        transition: all 0.18s ease;
    }
    .bb-tab:hover {
        color: #db2777;
        border-color: rgba(236, 72, 153, 0.35);
        transform: translateY(-1px);
    }
    .bb-tab.active {
        background: linear-gradient(135deg, #ec4899, #8b5cf6);
        color: #ffffff;
        box-shadow: 0 4px 12px rgba(236, 72, 153, 0.32);
    }
    .bb-section-grid {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(300px, 1fr));
        gap: 14px;
    }
    .bb-card {
        border-radius: 16px;
        padding: 15px 17px;
        background: rgba(255, 255, 255, 0.92);
        border: 1.5px solid rgba(243, 232, 255, 0.95);
        box-shadow: 0 6px 18px -6px rgba(139, 92, 246, 0.1);
        display: flex;
        flex-direction: column;
        gap: 10px;
        transition: all 0.2s ease;
    }
    .bb-card:hover {
        border-color: rgba(236, 72, 153, 0.4);
        box-shadow: 0 8px 22px -6px rgba(236, 72, 153, 0.16);
    }
    .bb-card.full-width {
        grid-column: 1 / -1;
    }
    .bb-card-head {
        display: flex;
        align-items: center;
        justify-content: space-between;
        padding-bottom: 8px;
        border-bottom: 1.5px dashed rgba(236, 72, 153, 0.22);
        flex-wrap: wrap;
        gap: 8px;
    }
    .bb-card-title {
        font-size: 14px;
        font-weight: 800;
        color: #1f2937;
        display: flex;
        align-items: center;
        gap: 7px;
    }
    .bb-tag {
        font-size: 10.5px;
        font-weight: 700;
        padding: 2px 8px;
        border-radius: 999px;
        background: linear-gradient(135deg, #fdf2f8, #f3e8ff);
        color: #db2777;
        border: 1px solid rgba(236, 72, 153, 0.3);
    }
    .bb-notify-grid {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(315px, 1fr));
        gap: 12px;
    }
    .bb-subcard {
        border-radius: 13px;
        padding: 12px 14px;
        background: linear-gradient(145deg, rgba(253, 242, 248, 0.55), rgba(245, 243, 255, 0.55));
        border: 1px solid rgba(236, 72, 153, 0.22);
        display: flex;
        flex-direction: column;
        gap: 8px;
    }
    .bb-subcard-top {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 6px;
        padding-bottom: 4px;
        border-bottom: 1px dashed rgba(236, 72, 153, 0.18);
    }
    .bb-row {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 10px;
    }
    .bb-label-wrap {
        display: flex;
        flex-direction: column;
        gap: 1px;
        flex: 1;
    }
    .bb-label {
        font-size: 12.5px;
        font-weight: 700;
        color: #374151;
    }
    .bb-desc {
        font-size: 11px;
        color: #6b7280;
        line-height: 1.35;
    }
    .bb-input, .bb-select, .bb-textarea {
        border: 1.5px solid #e5e7eb;
        border-radius: 9px;
        padding: 5px 10px;
        font-size: 12px;
        background: #ffffff;
        color: #1f2937;
        outline: none;
        transition: all 0.15s ease;
    }
    .bb-input:focus, .bb-select:focus, .bb-textarea:focus {
        border-color: #ec4899;
        box-shadow: 0 0 0 3px rgba(236, 72, 153, 0.15);
    }
    .bb-textarea {
        width: 100%;
        min-height: 46px;
        resize: vertical;
        line-height: 1.4;
    }
    .bb-switch {
        position: relative;
        display: inline-block;
        width: 40px;
        height: 22px;
        flex-shrink: 0;
    }
    .bb-switch input {
        opacity: 0;
        width: 0;
        height: 0;
    }
    .bb-slider {
        position: absolute;
        cursor: pointer;
        top: 0; left: 0; right: 0; bottom: 0;
        background-color: #e5e7eb;
        transition: .22s;
        border-radius: 999px;
    }
    .bb-slider:before {
        position: absolute;
        content: """";
        height: 16px;
        width: 16px;
        left: 3px;
        bottom: 3px;
        background-color: white;
        transition: .22s;
        border-radius: 50%;
        box-shadow: 0 1px 4px rgba(0,0,0,0.2);
    }
    .bb-switch input:checked + .bb-slider {
        background: linear-gradient(135deg, #ec4899, #8b5cf6);
    }
    .bb-switch input:checked + .bb-slider:before {
        transform: translateX(18px);
    }
    .bb-btn {
        display: inline-flex;
        align-items: center;
        gap: 4px;
        padding: 4px 10px;
        border-radius: 8px;
        font-size: 11px;
        font-weight: 700;
        cursor: pointer;
        border: 1px solid rgba(236, 72, 153, 0.35);
        background: linear-gradient(135deg, #fdf2f8, #f5f3ff);
        color: #db2777;
        transition: all 0.15s ease;
        flex-shrink: 0;
    }
    .bb-btn:hover {
        background: linear-gradient(135deg, #ec4899, #8b5cf6);
        color: #fff;
    }
    .bb-btn.recording {
        background: linear-gradient(135deg, #ef4444, #f97316);
        color: #fff;
        border-color: #ef4444;
        animation: bb-pulse 1.2s infinite;
    }
    @keyframes bb-pulse {
        0%, 100% { opacity: 1; }
        50% { opacity: 0.65; }
    }
    .bb-chip-list {
        display: flex;
        flex-wrap: wrap;
        gap: 5px;
        margin-top: 2px;
    }
    .bb-chip {
        font-size: 10.5px;
        font-weight: 600;
        padding: 2px 8px;
        border-radius: 999px;
        cursor: pointer;
        border: 1px solid rgba(139, 92, 246, 0.28);
        background: rgba(255, 255, 255, 0.85);
        color: #5b21b6;
        transition: all 0.15s ease;
    }
    .bb-chip:hover {
        border-color: #ec4899;
        color: #db2777;
    }
    .bb-chip.selected {
        background: linear-gradient(135deg, #ec4899, #8b5cf6);
        color: #fff;
        border-color: transparent;
    }
    .bb-macro-hint {
        font-size: 10.5px;
        color: #7c3aed;
        background: rgba(243, 232, 255, 0.7);
        padding: 2px 7px;
        border-radius: 6px;
        display: inline-block;
    }
    .bb-bar-bg {
        width: 100%;
        height: 7px;
        border-radius: 999px;
        background: rgba(236, 72, 153, 0.12);
        overflow: hidden;
    }
    .bb-bar-fill {
        height: 100%;
        border-radius: 999px;
        background: linear-gradient(90deg, #ec4899, #8b5cf6, #06b6d4);
    }
    .bb-log-item {
        font-size: 11.5px;
        padding: 6px 9px;
        border-radius: 8px;
        background: rgba(253, 242, 248, 0.5);
        border-left: 3px solid #ec4899;
        color: #374151;
        line-height: 1.4;
    }
</style>");

        BuildHeroBanner(b);
        BuildCategoryTabs(b);

        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-section-grid");

        if (_activeTab == "dashboard")
        {
            BuildRealtimeDashboardSection(b);
        }
        else
        {
            if (_activeTab == "all" || _activeTab == "notify")
                BuildActiveNotifySection(b);

            if (_activeTab == "all" || _activeTab == "input")
                BuildKeyboardMouseSection(b);

            if (_activeTab == "all" || _activeTab == "window")
                BuildWindowAndProfileSection(b);

            if (_activeTab == "all" || _activeTab == "media")
                BuildAudioMediaDeviceSection(b);

            if (_activeTab == "all" || _activeTab == "idle")
                BuildIdlePowerSection(b);

            if (_activeTab == "all" || _activeTab == "memory")
                BuildClipboardFilePromptSection(b);

            if (_activeTab == "all" || _activeTab == "trigger")
                BuildTriggerSignalSection(b);
        }

        b.CloseElement();
        b.CloseElement();
    }

    private void BuildHeroBanner(RenderTreeBuilder b)
    {
        var eng = BlackBoxEngine.Instance;
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-hero");

        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-hero-top");

        b.AddMarkupContent(Next(), @"<div class=""bb-brand"">
            <div class=""bb-logo"">🔮</div>
            <div>
                <div class=""bb-title"">全息感知黑盒 · 星穹灵犀中枢</div>
                <div class=""bb-subtitle"">8大模块化感知引擎 · 8重桌宠灵犀提醒 · 全屏免打扰护盾与实时手账</div>
            </div>
        </div>");

        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "style", "display:flex; align-items:center; gap:8px; flex-wrap:wrap;");
        if (!string.IsNullOrEmpty(_saveToast))
        {
            b.OpenElement(Next(), "span");
            b.AddAttribute(Next(), "class", "bb-tag");
            b.AddContent(Next(), _saveToast);
            b.CloseElement();
        }
        b.OpenElement(Next(), "button");
        b.AddAttribute(Next(), "class", "bb-btn");
        b.AddAttribute(Next(), "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () =>
        {
            RefreshRunningApps();
            StateHasChanged();
        }));
        b.AddContent(Next(), "🔄 刷新实时脉搏");
        b.CloseElement();
        b.CloseElement();

        b.CloseElement(); // bb-hero-top

        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-live-grid");

        BuildLivePill(b, "🎯 当前焦点视界", eng?.CurrentForegroundApp ?? "引擎待命中");
        BuildLivePill(b, "💫 物理活跃状态", eng?.CurrentIdleStatusSummary ?? "检测中...");
        BuildLivePill(b, "⌨️ 本分钟键鼠律动", eng != null ? $"击键 {eng.CurrentMinuteKeystrokes}次 | 滚轮 {eng.CurrentMinuteWheelClicks}次" : "0");
        BuildLivePill(b, "🎧 音频与次元外设", eng != null ? $"{(string.IsNullOrEmpty(eng.CurrentAudioDeviceFriendlyName) ? "默认声卡" : eng.CurrentAudioDeviceFriendlyName)} | {eng.ConnectedBluetoothDevicesSummary}" : "检测中...");

        b.CloseElement();
        b.CloseElement();
    }

    private void BuildLivePill(RenderTreeBuilder b, string label, string val)
    {
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-live-pill");
        b.OpenElement(Next(), "span");
        b.AddAttribute(Next(), "class", "bb-live-label");
        b.AddContent(Next(), label);
        b.CloseElement();
        b.OpenElement(Next(), "span");
        b.AddAttribute(Next(), "class", "bb-live-val");
        b.AddAttribute(Next(), "title", val);
        b.AddContent(Next(), val);
        b.CloseElement();
        b.CloseElement();
    }

    private void ToggleTriggerPause(string ruleId)
    {
        var eng = BlackBoxEngine.Instance;
        string roleName = Module?.Character?.Name ?? "";
        if (eng == null || string.IsNullOrWhiteSpace(roleName)) return;
        var rules = eng.GetRoleRules(roleName);
        var target = rules.FirstOrDefault(x => x.Id == ruleId);
        if (target == null) return;
        target.Paused = !target.Paused;
        eng.SetRoleRules(roleName, rules);
        StateHasChanged();
    }

    private void DeleteTriggerRule(string ruleId)
    {
        var eng = BlackBoxEngine.Instance;
        string roleName = Module?.Character?.Name ?? "";
        if (eng == null || string.IsNullOrWhiteSpace(roleName)) return;
        var rules = eng.GetRoleRules(roleName);
        rules.RemoveAll(x => x.Id == ruleId);
        eng.SetRoleRules(roleName, rules);
        StateHasChanged();
    }

    // ==================== 💠 自定义触发规则与信号面板 ====================
    private void BuildTriggerSignalSection(RenderTreeBuilder b)
    {
        var eng = BlackBoxEngine.Instance;
        string roleName = Module?.Character?.Name ?? "";

        // ---------- 卡片一：当前角色规则清单 ----------
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-card full-width");

        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-card-head");
        b.AddMarkupContent(Next(), @"<div class=""bb-card-title"">💠 当前角色自定义触发规则</div>");
        b.CloseElement();

        if (eng == null || string.IsNullOrWhiteSpace(roleName))
        {
            b.AddMarkupContent(Next(), @"<div class=""bb-desc"">引擎或角色尚未就绪，规则面板暂不可用。</div>");
        }
        else
        {
            var rules = eng.GetRoleRules(roleName);
            if (rules.Count == 0)
            {
                b.AddMarkupContent(Next(), @"<div class=""bb-desc"">该角色还没有自定义触发规则。可以让桌宠按需求创建，例如空闲满五分钟且不在全屏时提醒，或连续工作满九十分钟提醒活动。</div>");
            }
            else
            {
                b.OpenElement(Next(), "div");
                b.AddAttribute(Next(), "class", "bb-live-grid");
                foreach (var r in rules)
                {
                    string cond = string.Join(" 且 ", r.Conditions.Select(c =>
                    {
                        string v = c.TextValue ?? c.Value.ToString("F0");
                        return c.Signal + BlackBoxSuiteModule.OperatorToSymbol(c.Operator) + v + (c.DurationSeconds > 0 ? " 持续 " + c.DurationSeconds.ToString("F0") + " 秒" : "");
                    }));
                    string state = r.Paused ? "已暂停" : (r.Enabled ? "生效中" : "已停用");
                    string remain = string.Equals(r.Lifecycle, "Count", StringComparison.OrdinalIgnoreCase)
                        ? " 剩余 " + r.RemainingTimes + "/" + r.MaxTimes
                        : "";
                    string daily = r.DailyMaxTimes > 0 ? " | 今日 " + r.TodayFireCount + "/" + r.DailyMaxTimes : "";
                    string detail = cond + " | 周期 " + r.Lifecycle + remain + daily + " | 冷却 " + r.CooldownSeconds + "秒 | 已触发 " + r.FireCount + "次";

                    b.OpenElement(Next(), "div");
                    b.AddAttribute(Next(), "class", "bb-live-pill");

                    b.OpenElement(Next(), "span");
                    b.AddAttribute(Next(), "class", "bb-live-label");
                    b.AddContent(Next(), "【" + r.Id + "】" + r.Name + " · " + state);
                    b.CloseElement();

                    b.OpenElement(Next(), "span");
                    b.AddAttribute(Next(), "class", "bb-live-val");
                    b.AddAttribute(Next(), "title", detail);
                    b.AddContent(Next(), detail);
                    b.CloseElement();

                    string rid = r.Id;
                    bool paused = r.Paused;

                    b.OpenElement(Next(), "button");
                    b.AddAttribute(Next(), "class", "bb-btn");
                    b.AddAttribute(Next(), "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => ToggleTriggerPause(rid)));
                    b.AddContent(Next(), paused ? "▶ 恢复" : "⏸ 暂停");
                    b.CloseElement();

                    b.OpenElement(Next(), "button");
                    b.AddAttribute(Next(), "class", "bb-btn");
                    b.AddAttribute(Next(), "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => DeleteTriggerRule(rid)));
                    b.AddContent(Next(), "🗑 删除");
                    b.CloseElement();

                    b.CloseElement();
                }
                b.CloseElement();
            }
        }
        b.CloseElement();

        // ---------- 卡片二：数值型信号 ----------
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-card");

        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-card-head");
        b.AddMarkupContent(Next(), @"<div class=""bb-card-title"">🔢 数值型信号清单</div>");
        b.CloseElement();
        b.AddMarkupContent(Next(), @"<div class=""bb-desc"">写法：信号名 运算符 数字，运算符支持 大于、小于、大于等于、小于等于、等于、不等于。</div>");

        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-live-grid");
        BuildLivePill(b, "IdleSeconds", "系统空闲秒数，最常用");
        BuildLivePill(b, "ForegroundDwellSeconds", "前台窗口已停留秒数");
        BuildLivePill(b, "IsFullscreen", "是否全屏，1 是 0 否");
        BuildLivePill(b, "KeystrokesPerMinute", "每分钟击键数");
        BuildLivePill(b, "BackspacePerMinute", "每分钟退格数");
        BuildLivePill(b, "WheelPerMinute", "每分钟滚轮格数");
        BuildLivePill(b, "MousePixelsPerMinute", "每分钟鼠标移动像素");
        BuildLivePill(b, "ClickBurstCount", "连点次数");
        BuildLivePill(b, "RageShakeCount", "暴躁甩鼠次数");
        BuildLivePill(b, "AudioPeak", "音频峰值 0 到 1");
        BuildLivePill(b, "IsAudioPlaying", "是否在发声，1 是 0 否");
        BuildLivePill(b, "IsHeadphone", "是否耳机输出，1 是 0 否");
        BuildLivePill(b, "TodayActiveMinutes", "今日累计活跃分钟");
        BuildLivePill(b, "TodayFullscreenMinutes", "今日累计全屏分钟");
        BuildLivePill(b, "ContinuousWorkMinutes", "连续工作分钟数，久坐提醒核心");
        BuildLivePill(b, "ClipboardLength", "剪贴板文本长度");
        BuildLivePill(b, "MinuteOfDay", "当天第几分钟 0 到 1439");
        BuildLivePill(b, "DayOfWeek", "星期几 1 到 7");
        b.CloseElement();
        b.CloseElement();

        // ---------- 卡片三：文本型信号 ----------
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-card");

        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-card-head");
        b.AddMarkupContent(Next(), @"<div class=""bb-card-title"">📝 文本型信号清单</div>");
        b.CloseElement();
        b.AddMarkupContent(Next(), @"<div class=""bb-desc"">写法：信号名 等于 文本。例如前台是浏览器可写 ForegroundApp 等于 chrome。</div>");

        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-live-grid");
        BuildLivePill(b, "ForegroundApp", "前台进程名，如 chrome、Code");
        BuildLivePill(b, "ForegroundTitle", "前台窗口标题");
        BuildLivePill(b, "AudioProcess", "发声进程名");
        BuildLivePill(b, "MediaTitle", "当前播放曲目");
        BuildLivePill(b, "ClipboardText", "剪贴板文本内容");
        BuildLivePill(b, "LastFileName", "最近落盘文件名");
        BuildLivePill(b, "LastFileExt", "最近落盘文件后缀");
        b.CloseElement();
        b.CloseElement();

        // ---------- 卡片四：事件型信号 ----------
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-card full-width");

        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-card-head");
        b.AddMarkupContent(Next(), @"<div class=""bb-card-title"">⚡ 事件型信号清单</div>");
        b.CloseElement();
        b.AddMarkupContent(Next(), @"<div class=""bb-desc"">事件型条件写法：信号 等于 1，表示只要该事件发生即命中，一次事件只触发一次。已接入：AppOpened、AppClosed、FileDropped。ClipboardChanged、ScreenLocked、ScreenUnlocked、SystemResumed 为预留位，当前不产生事件。</div>");

        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-live-grid");
        BuildLivePill(b, "AppOpened", "某个应用被切到前台");
        BuildLivePill(b, "AppClosed", "某个应用退出前台");
        BuildLivePill(b, "FileDropped", "有文件落盘到监控目录");
        BuildLivePill(b, "ClipboardChanged", "剪贴板内容发生变化");
        BuildLivePill(b, "ScreenLocked", "屏幕锁定");
        BuildLivePill(b, "ScreenUnlocked", "屏幕解锁");
        BuildLivePill(b, "SystemResumed", "系统从休眠唤醒");
        b.CloseElement();
        b.CloseElement();
    }

    private void BuildCategoryTabs(RenderTreeBuilder b)
    {
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-tabs");

        AddTabButton(b, "all", "🌸 全部配置总览");
        AddTabButton(b, "dashboard", "📊 实时手账看板");
        AddTabButton(b, "notify", "💖 灵犀主动提醒 (8项)");
        AddTabButton(b, "input", "⌨️ 键鼠动力学律动");
        AddTabButton(b, "window", "🪟 视界与专注伴读");
        AddTabButton(b, "media", "🎧 声波与次元外设");
        AddTabButton(b, "idle", "🌙 离席与结界守望");
        AddTabButton(b, "memory", "📸 闪存记忆与脉搏");
        AddTabButton(b, "trigger", "💠 自定义触发规则");

        b.CloseElement();
    }

    private void AddTabButton(RenderTreeBuilder b, string tabKey, string text)
    {
        b.OpenElement(Next(), "button");
        b.AddAttribute(Next(), "class", _activeTab == tabKey ? "bb-tab active" : "bb-tab");
        b.AddAttribute(Next(), "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () =>
        {
            _activeTab = tabKey;
            if (tabKey == "dashboard")
            {
                BlackBoxEngine.Instance?.FlushCurrentActiveWindowSegment();
            }
        }));
        b.AddContent(Next(), text);
        b.CloseElement();
    }

    // ==================== 📊 实时手账可视化看板 Tab ====================
    private void BuildRealtimeDashboardSection(RenderTreeBuilder b)
    {
        var storage = BlackBoxStorage.Instance;
        var profile = storage.GetDailyProfile("today", Config.ProfileIgnoreProcesses);
        var timeline = storage.QueryTransientEvents(120, "All").Take(12).ToList();
        var (clips, totalClips, _) = storage.QueryClipboard(1, 6, "All", "");

        // 1. 今日专注与软件排行大卡片
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-card full-width");

        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-card-head");
        b.AddMarkupContent(Next(), @"<div class=""bb-card-title"">📊 今日星穹专注手账 · 实时软件停留排行</div>");
        b.OpenElement(Next(), "button");
        b.AddAttribute(Next(), "class", "bb-btn");
        b.AddAttribute(Next(), "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () =>
        {
            BlackBoxEngine.Instance?.FlushCurrentActiveWindowSegment();
            StateHasChanged();
        }));
        b.AddContent(Next(), "⚡ 立即结算刷新今日数据");
        b.CloseElement();
        b.CloseElement();

        if (profile == null || profile.TotalActiveSeconds <= 0)
        {
            b.OpenElement(Next(), "div");
            b.AddAttribute(Next(), "class", "bb-desc");
            b.AddContent(Next(), "今日暂无足够活跃时长记录，切换几个窗口或操作片刻后即可生成手账~");
            b.CloseElement();
        }
        else
        {
            b.OpenElement(Next(), "div");
            b.AddAttribute(Next(), "class", "bb-live-grid");
            BuildLivePill(b, "⏱️ 今日有效活跃总时长", BlackBoxEvent.FormatDuration(profile.TotalActiveSeconds));
            BuildLivePill(b, "🎮 全屏沉浸专注时长", $"{BlackBoxEvent.FormatDuration(profile.TotalFullscreenSeconds)} ({(profile.FullscreenRatio * 100):F0}%)");
            BuildLivePill(b, "📖 同屏伴读累计时长", BlackBoxEvent.FormatDuration(profile.TotalCoReadSeconds));
            BuildLivePill(b, "💾 Ctrl+S 保存与新文件", $"保存 {profile.FileSavePulseCount} 次 | 新落盘 {profile.NewDownloadFileCount} 个");
            b.CloseElement();

            var topApps = profile.Apps.Values
                .OrderByDescending(a => a.TotalActiveSeconds + a.CoReadSeconds)
                .Take(8)
                .ToList();
            double maxSec = topApps.Count > 0 ? Math.Max(1.0, topApps[0].TotalActiveSeconds + topApps[0].CoReadSeconds) : 1.0;

            b.OpenElement(Next(), "div");
            b.AddAttribute(Next(), "style", "display:flex; flex-direction:column; gap:8px; margin-top:6px;");
            int rank = 1;
            foreach (var app in topApps)
            {
                double totalAppSec = app.TotalActiveSeconds + app.CoReadSeconds;
                double pct = Math.Clamp((totalAppSec / maxSec) * 100.0, 4.0, 100.0);

                b.OpenElement(Next(), "div");
                b.AddAttribute(Next(), "style", "display:flex; flex-direction:column; gap:3px;");

                b.OpenElement(Next(), "div");
                b.AddAttribute(Next(), "class", "bb-row");
                b.OpenElement(Next(), "span");
                b.AddAttribute(Next(), "class", "bb-label");
                b.AddContent(Next(), $"#{rank++} [{app.ProcessName}] {(string.IsNullOrEmpty(app.LastWindowTitle) ? "" : $"· {app.LastWindowTitle}")}");
                b.CloseElement();
                b.OpenElement(Next(), "span");
                b.AddAttribute(Next(), "class", "bb-tag");
                b.AddContent(Next(), $"主焦点 {BlackBoxEvent.FormatDuration(app.TotalActiveSeconds)}" + (app.CoReadSeconds > 0 ? $" + 伴读 {BlackBoxEvent.FormatDuration(app.CoReadSeconds)}" : ""));
                b.CloseElement();
                b.CloseElement();

                b.OpenElement(Next(), "div");
                b.AddAttribute(Next(), "class", "bb-bar-bg");
                b.OpenElement(Next(), "div");
                b.AddAttribute(Next(), "class", "bb-bar-fill");
                b.AddAttribute(Next(), "style", $"width:{pct.ToString("F1", CultureInfo.InvariantCulture)}%;");
                b.CloseElement();
                b.CloseElement();

                b.CloseElement();
            }
            b.CloseElement();
        }
        b.CloseElement();

        // 2. 近期瞬态行为时间线卡片
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-card");
        b.AddMarkupContent(Next(), @"<div class=""bb-card-head"">
            <div class=""bb-card-title"">⚡ 最近 12 条实时行为轨迹</div>
            <span class=""bb-tag"">Timeline</span>
        </div>");
        if (timeline.Count == 0)
        {
            b.OpenElement(Next(), "div");
            b.AddAttribute(Next(), "class", "bb-desc");
            b.AddContent(Next(), "暂无近期离散事件记录。");
            b.CloseElement();
        }
        else
        {
            foreach (var ev in timeline)
            {
                b.OpenElement(Next(), "div");
                b.AddAttribute(Next(), "class", "bb-log-item");
                b.AddContent(Next(), ev.ToString());
                b.CloseElement();
            }
        }
        b.CloseElement();

        // 3. 近期剪贴板与截图闪存卡片
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-card");
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-card-head");
        b.AddMarkupContent(Next(), @"<div class=""bb-card-title"">📋 最近剪贴板与截图闪存</div>");
        b.OpenElement(Next(), "span");
        b.AddAttribute(Next(), "class", "bb-tag");
        b.AddContent(Next(), $"累计 {totalClips} 条");
        b.CloseElement();
        b.CloseElement();

        if (clips.Count == 0)
        {
            b.OpenElement(Next(), "div");
            b.AddAttribute(Next(), "class", "bb-desc");
            b.AddContent(Next(), "剪贴板知识库暂无记录。");
            b.CloseElement();
        }
        else
        {
            foreach (var c in clips)
            {
                b.OpenElement(Next(), "div");
                b.AddAttribute(Next(), "class", "bb-log-item");
                b.AddContent(Next(), c.ToString());
                b.CloseElement();
            }
        }
        b.CloseElement();
    }

    // ==================== 1. 灵犀主动提醒工坊 (8大开关 + ⚡试发按钮 + 点选软件 + 🎹录制按键) ====================
    private void BuildActiveNotifySection(RenderTreeBuilder b)
    {
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-card full-width");

        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-card-head");
        b.AddMarkupContent(Next(), @"<div class=""bb-card-title"">💖 灵犀主动共鸣工坊 · 8大桌宠提醒开关与提示词定制</div>");

        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "style", "display:flex; align-items:center; gap:14px; flex-wrap:wrap;");

        // 全屏沉浸免打扰延后护盾开关
        b.OpenElement(Next(), "label");
        b.AddAttribute(Next(), "style", "display:flex; align-items:center; gap:6px; font-size:12px; font-weight:700; color:#7c3aed; cursor:pointer;");
        b.OpenElement(Next(), "input");
        b.AddAttribute(Next(), "type", "checkbox");
        b.AddAttribute(Next(), "checked", BindConverter.FormatValue(Config.DeferNotifyWhenFullscreen));
        b.AddAttribute(Next(), "onchange", EventCallback.Factory.CreateBinder(this, v => { Config.DeferNotifyWhenFullscreen = v; Save(); }, Config.DeferNotifyWhenFullscreen));
        b.CloseElement();
        b.AddContent(Next(), "🛡️ 全屏游戏/看番免打扰护盾 (久坐与深夜提醒自动延后至退出全屏)");
        b.CloseElement();

        // 派发模式选择
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "style", "display:flex; align-items:center; gap:6px;");
        b.OpenElement(Next(), "span");
        b.AddAttribute(Next(), "style", "font-size:12px; font-weight:700; color:#db2777;");
        b.AddContent(Next(), "派发模式：");
        b.CloseElement();
        b.OpenElement(Next(), "select");
        b.AddAttribute(Next(), "class", "bb-select");
        b.AddAttribute(Next(), "value", Config.NotifyDispatchMode);
        b.AddAttribute(Next(), "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            Config.NotifyDispatchMode = e.Value?.ToString() ?? "Poke";
            Save();
        }));
        AddSelectOption(b, "Poke", "Poke (轻戳潜意识，自然回应推荐)");
        AddSelectOption(b, "Chat", "Chat (立即触发主动开口对话)");
        b.CloseElement();
        b.CloseElement();

        b.CloseElement();
        b.CloseElement(); // bb-card-head

        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-notify-grid");

        // ① 归座唤醒 (支持短暂离开与久别重逢分时长差异化问候)
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-subcard");
        BuildSubcardHeader(b, "① 毫秒级归座唤醒 (分时长问候)", Config.EnableReturnNotifyPrompt, v => { Config.EnableReturnNotifyPrompt = v; Save(); },
            () => FireTestPrompt("归座唤醒", Config.ReturnPromptTemplate));
        BuildTextareaRow(b, "短暂离开归座模板", "{AwayMinutes} {TimeOfDay} {ProcessName}", Config.ReturnPromptTemplate, v => { Config.ReturnPromptTemplate = v; Save(); });
        BuildNumberRow(b, "久别重逢分界门槛(分钟)", "离开达此分钟切换久别模板", Config.LongReturnThresholdMinutes, 5, 360, v => { Config.LongReturnThresholdMinutes = v; Save(); });
        BuildTextareaRow(b, "久别重逢归座模板", "{AwayMinutes} {TimeOfDay} {ProcessName}", Config.LongReturnPromptTemplate, v => { Config.LongReturnPromptTemplate = v; Save(); });
        b.CloseElement();

        // ② 真离席通知
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-subcard");
        BuildSubcardHeader(b, "② 防误判真离席挂起通知", Config.EnableIdleNotifyPrompt, v => { Config.EnableIdleNotifyPrompt = v; Save(); },
            () => FireTestPrompt("真离席通知", Config.IdlePromptTemplate));
        BuildTextareaRow(b, "真离席提示词模板", "结合声卡与全屏防假离线确认走开时发送", Config.IdlePromptTemplate, v => { Config.IdlePromptTemplate = v; Save(); });
        b.CloseElement();

        // ③ 音频端点/耳机防社死
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-subcard");
        BuildSubcardHeader(b, "③ 耳机/外放切换防社死提醒", Config.EnableAudioEndpointNotifyPrompt, v => { Config.EnableAudioEndpointNotifyPrompt = v; Save(); },
            () => FireTestPrompt("耳机切换提醒", Config.HeadphonePluggedPromptTemplate));
        BuildTextRow(b, "切为耳机模板", "{DeviceName}", Config.HeadphonePluggedPromptTemplate, v => { Config.HeadphonePluggedPromptTemplate = v; Save(); });
        BuildTextRow(b, "切为外放模板", "{DeviceName}", Config.HeadphoneUnpluggedPromptTemplate, v => { Config.HeadphoneUnpluggedPromptTemplate = v; Save(); });
        b.CloseElement();

        // ④ 暴躁甩鼠告警
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-subcard");
        BuildSubcardHeader(b, "④ 暴躁甩鼠标/卡壳关切提醒", Config.EnableRageShakeNotifyPrompt, v => { Config.EnableRageShakeNotifyPrompt = v; Save(); },
            () => FireTestPrompt("暴躁甩鼠告警", Config.RageShakePromptTemplate));
        BuildNumberRow(b, "1.5秒内反转次数门槛", "建议 8~16 次防误触", Config.RageShakeReversalThreshold, 3, 50, v => { Config.RageShakeReversalThreshold = v; Save(); });
        BuildTextareaRow(b, "甩鼠提示词模板", "{ProcessName} {WindowTitle}", Config.RageShakePromptTemplate, v => { Config.RageShakePromptTemplate = v; Save(); });
        b.CloseElement();

        // ⑤ 指定应用打开/切入提醒 (带一键点选正在运行的软件胶囊)
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-subcard");
        BuildSubcardHeader(b, "⑤ 指定应用打开/切入提醒", Config.EnableAppOpenNotifyPrompt, v => { Config.EnableAppOpenNotifyPrompt = v; Save(); },
            () => FireTestPrompt("指定应用打开", Config.AppOpenPromptTemplate));
        BuildTextRow(b, "监控应用名单(逗号分隔)", "进程名或窗口标题关键词", Config.WatchedAppsCsv, v => { Config.WatchedAppsCsv = v; Save(); });

        // 一键点选当前运行的软件胶囊栏
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "style", "display:flex; flex-direction:column; gap:3px;");
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-row");
        b.OpenElement(Next(), "span");
        b.AddAttribute(Next(), "class", "bb-desc");
        b.AddContent(Next(), "➕ 点击下方正在运行的软件即可一键加入/移除监控：");
        b.CloseElement();
        b.OpenElement(Next(), "button");
        b.AddAttribute(Next(), "class", "bb-btn");
        b.AddAttribute(Next(), "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => { RefreshRunningApps(); StateHasChanged(); }));
        b.AddContent(Next(), "🔄 刷新列表");
        b.CloseElement();
        b.CloseElement();

        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-chip-list");
        var currentWatchedSet = new HashSet<string>(
            (Config.WatchedAppsCsv ?? "").Split(new[] { ',', ';', '，', '|' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()),
            StringComparer.OrdinalIgnoreCase);

        if (_runningApps.Count == 0)
        {
            b.OpenElement(Next(), "span");
            b.AddAttribute(Next(), "class", "bb-desc");
            b.AddContent(Next(), "未检测到其他可见窗口");
            b.CloseElement();
        }
        else
        {
            foreach (var app in _runningApps)
            {
                bool isSel = currentWatchedSet.Contains(app.ProcessName);
                b.OpenElement(Next(), "span");
                b.AddAttribute(Next(), "class", isSel ? "bb-chip selected" : "bb-chip");
                b.AddAttribute(Next(), "title", app.WindowTitle);
                b.AddAttribute(Next(), "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => AddWatchedApp(app.ProcessName)));
                b.AddContent(Next(), (isSel ? "✓ " : "+ ") + app.ProcessName);
                b.CloseElement();
            }
        }
        b.CloseElement();
        b.CloseElement();

        BuildNumberRow(b, "同应用防刷屏冷却(秒)", "防Alt+Tab重复触发", Config.AppOpenNotifyCooldownSeconds, 0, 7200, v => { Config.AppOpenNotifyCooldownSeconds = v; Save(); });
        BuildTextareaRow(b, "应用打开提示词模板", "{ProcessName} {WindowTitle}", Config.AppOpenPromptTemplate, v => { Config.AppOpenPromptTemplate = v; Save(); });
        b.CloseElement();

        // ⑥ 自定义按键/组合键监测提醒 (带 🎹 一键按键录制器)
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-subcard");
        BuildSubcardHeader(b, "⑥ 自定义按键/组合键监测提醒", Config.EnableKeyWatchNotifyPrompt, v => { Config.EnableKeyWatchNotifyPrompt = v; Save(); },
            () => FireTestPrompt("自定义按键触发", Config.KeyWatchPromptTemplate, "Ctrl+Z", Config.KeyWatchTriggerCount));
        BuildTextRow(b, "监测按键/组合键规则", "如: Back:20, Ctrl+Z:5, F5:1", Config.WatchedKeysSpec, v => { Config.WatchedKeysSpec = v; Save(); });

        // 🎹 一键按键录制操作行
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-row");
        b.AddAttribute(Next(), "style", "background:rgba(255,255,255,0.7); padding:5px 8px; border-radius:8px; border:1px dashed rgba(139,92,246,0.3);");
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "style", "display:flex; align-items:center; gap:6px;");
        b.OpenElement(Next(), "span");
        b.AddAttribute(Next(), "class", "bb-desc");
        b.AddContent(Next(), "录制预设次数:");
        b.CloseElement();
        b.OpenElement(Next(), "input");
        b.AddAttribute(Next(), "type", "number");
        b.AddAttribute(Next(), "class", "bb-input");
        b.AddAttribute(Next(), "style", "width:58px; padding:2px 6px; text-align:center;");
        b.AddAttribute(Next(), "min", "1");
        b.AddAttribute(Next(), "max", "200");
        b.AddAttribute(Next(), "value", _recordDefaultCount.ToString());
        b.AddAttribute(Next(), "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            if (int.TryParse(e.Value?.ToString(), out int v)) _recordDefaultCount = Math.Clamp(v, 1, 200);
        }));
        b.CloseElement();
        b.CloseElement();

        b.OpenElement(Next(), "button");
        b.AddAttribute(Next(), "class", _isRecordingKey ? "bb-btn recording" : "bb-btn");
        b.AddAttribute(Next(), "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, ToggleKeyRecording));
        b.AddContent(Next(), _isRecordingKey ? "🔴 正在倾听按键 (点此取消)..." : "🎹 点击录制按键/组合键");
        b.CloseElement();
        b.CloseElement();

        BuildNumberRow(b, "默认触发次数门槛(次)", "1=按一下即触发", Config.KeyWatchTriggerCount, 1, 500, v => { Config.KeyWatchTriggerCount = v; Save(); });
        BuildNumberRow(b, "统计时间窗口(秒)", "在此秒数内累计按压次数", Config.KeyWatchWindowSeconds, 1, 600, v => { Config.KeyWatchWindowSeconds = v; Save(); });
        BuildNumberRow(b, "触发后冷却时间(秒)", "防连续刷屏冷却秒数", Config.KeyWatchCooldownSeconds, 1, 3600, v => { Config.KeyWatchCooldownSeconds = v; Save(); });
        BuildTextareaRow(b, "按键触发提示词模板", "{KeyName} {Count} {WindowSeconds} {ProcessName}", Config.KeyWatchPromptTemplate, v => { Config.KeyWatchPromptTemplate = v; Save(); });
        b.CloseElement();

        // ⑦ 连续久坐/爆肝防疲劳提醒
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-subcard");
        BuildSubcardHeader(b, "⑦ 连续久坐/爆肝防疲劳提醒", Config.EnableContinuousWorkNotifyPrompt, v => { Config.EnableContinuousWorkNotifyPrompt = v; Save(); },
            () => FireTestPrompt("连续久坐防疲劳", Config.ContinuousWorkPromptTemplate));
        BuildNumberRow(b, "连续操作提醒门槛(分钟)", "中途离席自动重置计时", Config.ContinuousWorkThresholdMinutes, 5, 480, v => { Config.ContinuousWorkThresholdMinutes = v; Save(); });
        BuildTextareaRow(b, "久坐提醒提示词模板", "{Minutes} {ProcessName} {WindowTitle}", Config.ContinuousWorkPromptTemplate, v => { Config.ContinuousWorkPromptTemplate = v; Save(); });
        b.CloseElement();

        // ⑧ 深夜修仙/熬夜温柔哄睡开关
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-subcard");
        BuildSubcardHeader(b, "⑧ 深夜修仙/熬夜温柔哄睡开关", Config.EnableLateNightNotifyPrompt, v => { Config.EnableLateNightNotifyPrompt = v; Save(); },
            () => FireTestPrompt("深夜温柔哄睡", Config.LateNightPromptTemplate));
        BuildTextRow(b, "深夜哄睡触发时间(HH:mm)", "每晚仅触发一次，如 00:30 或 01:00", Config.LateNightTriggerTime, v => { Config.LateNightTriggerTime = v; Save(); });
        BuildTextareaRow(b, "深夜哄睡提示词模板", "{CurrentTime} {ProcessName} {WindowTitle}", Config.LateNightPromptTemplate, v => { Config.LateNightPromptTemplate = v; Save(); });
        b.CloseElement();

        b.CloseElement(); // bb-notify-grid
        b.CloseElement(); // bb-card
    }

    private void BuildSubcardHeader(RenderTreeBuilder b, string title, bool enabled, Action<bool> onToggle, Action onTest)
    {
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-subcard-top");

        b.OpenElement(Next(), "span");
        b.AddAttribute(Next(), "class", "bb-label");
        b.AddAttribute(Next(), "style", "color:#db2777;");
        b.AddContent(Next(), title);
        b.CloseElement();

        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "style", "display:flex; align-items:center; gap:6px;");

        b.OpenElement(Next(), "button");
        b.AddAttribute(Next(), "class", "bb-btn");
        b.AddAttribute(Next(), "title", "立即代入当前环境向桌宠试发一次此提醒");
        b.AddAttribute(Next(), "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, onTest));
        b.AddContent(Next(), "⚡ 试发");
        b.CloseElement();

        b.OpenElement(Next(), "label");
        b.AddAttribute(Next(), "class", "bb-switch");
        b.OpenElement(Next(), "input");
        b.AddAttribute(Next(), "type", "checkbox");
        b.AddAttribute(Next(), "checked", BindConverter.FormatValue(enabled));
        b.AddAttribute(Next(), "onchange", EventCallback.Factory.CreateBinder(this, onToggle, enabled));
        b.CloseElement();
        b.OpenElement(Next(), "span");
        b.AddAttribute(Next(), "class", "bb-slider");
        b.CloseElement();
        b.CloseElement();

        b.CloseElement();
        b.CloseElement();
    }

    // ==================== 2. 键鼠动力学律动 ====================
    private void BuildKeyboardMouseSection(RenderTreeBuilder b)
    {
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-card");

        b.AddMarkupContent(Next(), @"<div class=""bb-card-head"">
            <div class=""bb-card-title"">⌨️ 键鼠动力学律动</div>
            <span class=""bb-tag"">Keyboard &amp; Mouse</span>
        </div>");

        BuildToggleRow(b, "键盘打字节拍黑盒", "静默统计击键密度、APM 与退格频率（绝不记录按键内容）", Config.EnableKeyboardWatch, v => { Config.EnableKeyboardWatch = v; Save(); });
        BuildNumberRow(b, "打字节拍切片周期(秒)", "默认 60 秒聚合一次", Config.KeyboardSliceSeconds, 10, 600, v => { Config.KeyboardSliceSeconds = v; Save(); });

        BuildToggleRow(b, "鼠标滚轮阅读黑盒", "180ms 脉冲防抖捕捉文献/网页翻页次数与滚动强度", Config.EnableMouseWheelWatch, v => { Config.EnableMouseWheelWatch = v; Save(); });
        BuildNumberRow(b, "滚轮切片周期(秒)", "默认 60 秒聚合一次", Config.MouseWheelSliceSeconds, 10, 600, v => { Config.MouseWheelSliceSeconds = v; Save(); });
        BuildNumberRow(b, "高强度速读翻页门槛(次)", "单周期超过此次数标记为深度速读", Config.MouseWheelIntenseThreshold, 5, 300, v => { Config.MouseWheelIntenseThreshold = v; Save(); });
        BuildToggleRow(b, "滚轮翻页重置物理空闲", "看长文/PDF只滚轮不打字时防止误判离席", Config.EnableMouseWheelAntiIdle, v => { Config.EnableMouseWheelAntiIdle = v; Save(); });

        BuildToggleRow(b, "鼠标 50ms 动力学监测", "识别沉思游走审题、高频连点脉冲与暴躁甩鼠", Config.EnableMouseKinematicsWatch, v => { Config.EnableMouseKinematicsWatch = v; Save(); });

        b.CloseElement();
    }

    // ==================== 3. 视界与专注伴读 ====================
    private void BuildWindowAndProfileSection(RenderTreeBuilder b)
    {
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-card");

        b.AddMarkupContent(Next(), @"<div class=""bb-card-head"">
            <div class=""bb-card-title"">🪟 视界时间线与同屏伴读画像</div>
            <span class=""bb-tag"">Window &amp; Co-Read</span>
        </div>");

        BuildToggleRow(b, "前台应用时间线监测", "静默记录前台软件切换与全屏沉浸状态", Config.EnableWindowTimeline, v => { Config.EnableWindowTimeline = v; Save(); });
        BuildNumberRow(b, "窗口有效驻留防抖(秒)", "过滤快速 Alt+Tab 切屏碎片", Config.WindowDwellThresholdSeconds, 1, 120, v => { Config.WindowDwellThresholdSeconds = v; Save(); });

        BuildToggleRow(b, "DWM 同屏伴读网格拓扑", "精准识别分屏/双屏未遮挡辅助窗口并累加伴读时长", Config.EnableCoReadingWatch, v => { Config.EnableCoReadingWatch = v; Save(); });
        BuildDoubleRow(b, "伴读窗口可见比例门槛", "0.25 ~ 0.95 (默认 0.60 即 60% 可见)", Config.CoReadingUnoccludedRatio, 0.25, 0.95, v => { Config.CoReadingUnoccludedRatio = v; Save(); });

        BuildToggleRow(b, "每日专注画像永久聚合", "按天统计有效活跃、全屏沉浸与伴读排行", Config.EnableDailyFocusProfile, v => { Config.EnableDailyFocusProfile = v; Save(); });
        BuildTextRow(b, "专注排行忽略进程", "英文逗号分隔", Config.ProfileIgnoreProcesses, v => { Config.ProfileIgnoreProcesses = v; Save(); });
        BuildNumberRow(b, "画像后台自动存盘(分钟)", "定时落盘防丢失", Config.ProfileAutoSaveIntervalMinutes, 1, 120, v => { Config.ProfileAutoSaveIntervalMinutes = v; Save(); });

        b.CloseElement();
    }

    // ==================== 4. 声波与次元外设 ====================
    private void BuildAudioMediaDeviceSection(RenderTreeBuilder b)
    {
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-card");

        b.AddMarkupContent(Next(), @"<div class=""bb-card-head"">
            <div class=""bb-card-title"">🎧 声波会话与次元外设</div>
            <span class=""bb-tag"">Audio &amp; Device</span>
        </div>");

        BuildToggleRow(b, "Core Audio 声卡与发声进程监测", "监测主音量峰值并精准定位正在发声的软件", Config.EnableAudioWatch, v => { Config.EnableAudioWatch = v; Save(); });
        BuildNumberRow(b, "声卡采样间隔(秒)", "默认 5 秒", Config.AudioCheckIntervalSeconds, 1, 60, v => { Config.AudioCheckIntervalSeconds = v; Save(); });
        BuildNumberRow(b, "短音/系统蜂鸣过滤门槛(秒)", "过滤微信提示音等短碎杂音", Config.AudioFilterDurationSeconds, 1, 60, v => { Config.AudioFilterDurationSeconds = v; Save(); });

        BuildToggleRow(b, "SMTC 原生媒体曲目捕获", "静默识别网易云、B站、浏览器正在播放的歌名/视频", Config.EnableMediaWatch, v => { Config.EnableMediaWatch = v; Save(); });
        BuildNumberRow(b, "媒体采样间隔(秒)", "默认 5 秒", Config.MediaCheckIntervalSeconds, 1, 60, v => { Config.MediaCheckIntervalSeconds = v; Save(); });

        BuildToggleRow(b, "蓝牙外设与游戏手柄监督", "SetupAPI 真实连断探测与 Xbox 手柄状态监听", Config.EnableDeviceWatch, v => { Config.EnableDeviceWatch = v; Save(); });
        BuildToggleRow(b, "手柄/蓝牙连断主动提醒桌宠", "连接或断开手柄/蓝牙设备时通知桌宠", Config.DeviceNotifyPrompt, v => { Config.DeviceNotifyPrompt = v; Save(); });

        b.CloseElement();
    }

    // ==================== 5. 离席与结界守望 ====================
    private void BuildIdlePowerSection(RenderTreeBuilder b)
    {
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-card");

        b.AddMarkupContent(Next(), @"<div class=""bb-card-head"">
            <div class=""bb-card-title"">🌙 离席仲裁与电源结界守望</div>
            <span class=""bb-tag"">Idle &amp; Power</span>
        </div>");

        BuildToggleRow(b, "物理空闲与离席检测", "综合硬件输入与低级钩子判定离开与归座", Config.EnableIdleWatch, v => { Config.EnableIdleWatch = v; Save(); });
        BuildNumberRow(b, "常规桌面离席阈值(秒)", "默认 180 秒 (3分钟)", Config.IdleThresholdSeconds, 30, 3600, v => { Config.IdleThresholdSeconds = v; Save(); });
        BuildNumberRow(b, "全屏沉浸静止容差(秒)", "默认 300 秒 (5分钟)", Config.FullscreenIdleThresholdSeconds, 60, 7200, v => { Config.FullscreenIdleThresholdSeconds = v; Save(); });
        BuildToggleRow(b, "影音发声一票防假离线", "有持续声音或媒体播放时视为倾听，绝不误判离席", Config.EnableAudioAntiIdle, v => { Config.EnableAudioAntiIdle = v; Save(); });
        BuildToggleRow(b, "监听 Windows 锁屏 (Win+L)", "锁屏瞬间自动结算并挂起专注记账", Config.EnableSessionLockWatch, v => { Config.EnableSessionLockWatch = v; Save(); });
        BuildToggleRow(b, "监听系统睡眠与休眠", "睡眠挂起与唤醒恢复自动闭环记账", Config.EnablePowerSuspendWatch, v => { Config.EnablePowerSuspendWatch = v; Save(); });

        b.CloseElement();
    }

    // ==================== 6. 闪存记忆与文件脉搏 ====================
    private void BuildClipboardFilePromptSection(RenderTreeBuilder b)
    {
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-card");

        b.AddMarkupContent(Next(), @"<div class=""bb-card-head"">
            <div class=""bb-card-title"">📸 闪存记忆库、文件脉搏与心智注入</div>
            <span class=""bb-tag"">Memory &amp; Pulse</span>
        </div>");

        BuildToggleRow(b, "剪贴板长期知识库", "永久归档复制的代码、链接与笔记并绑定窗口标签", Config.EnableClipboardWatch, v => { Config.EnableClipboardWatch = v; Save(); });
        BuildNumberRow(b, "剪贴板最小字数门槛", "过滤单字符误触", Config.ClipboardMinLength, 1, 500, v => { Config.ClipboardMinLength = v; Save(); });
        BuildToggleRow(b, "敏感 Token/API Key 自动脱敏", "自动打码保护密钥凭据", Config.ClipboardFilterSensitive, v => { Config.ClipboardFilterSensitive = v; Save(); });
        BuildToggleRow(b, "连续重复复制去重", "避免重复记录相同文本", Config.ClipboardDedup, v => { Config.ClipboardDedup = v; Save(); });

        BuildToggleRow(b, "上下文截图视觉闪存库", "Win+Shift+S 截图自动压缩归档并打上当前软件标题标签", Config.EnableScreenshotFlashWatch, v => { Config.EnableScreenshotFlashWatch = v; Save(); });
        BuildNumberRow(b, "截图 JPEG 保存画质(10~100)", "默认 80 兼顾清晰度与轻量体积", Config.ScreenshotJpegQuality, 10, 100, v => { Config.ScreenshotJpegQuality = v; Save(); });

        BuildToggleRow(b, "工作区 Ctrl+S 脉搏与下载管家", "ReadDirectoryChangesW 原生异步监听保存与新落盘文件", Config.EnableFilePulseWatch, v => { Config.EnableFilePulseWatch = v; Save(); });
        BuildTextRow(b, "文件脉搏监听目录(逗号分隔)", @"默认 D:\awa,D:\ded", Config.WatchedDirectoriesCsv, v => { Config.WatchedDirectoriesCsv = v; Save(); });

        BuildNumberRow(b, "内存瞬态事件队列上限(条)", "FIFO 滑动窗口容量", Config.MaxTransientEventsCount, 50, 2000, v => { Config.MaxTransientEventsCount = v; Save(); });
        BuildToggleRow(b, "向角色注入黑盒能力说明", "开启后自动向角色系统提示词注入黑盒函数指南", Config.EnablePromptInjection, v => { Config.EnablePromptInjection = v; Save(); });

        b.CloseElement();
    }

    // ==================== 通用二次元 UI 控件构建辅助 ====================
    private void BuildToggleRow(RenderTreeBuilder b, string title, string desc, bool value, Action<bool> onChanged)
    {
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-row");

        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-label-wrap");
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-label");
        b.AddContent(Next(), title);
        b.CloseElement();
        if (!string.IsNullOrEmpty(desc))
        {
            b.OpenElement(Next(), "div");
            b.AddAttribute(Next(), "class", "bb-desc");
            b.AddContent(Next(), desc);
            b.CloseElement();
        }
        b.CloseElement();

        b.OpenElement(Next(), "label");
        b.AddAttribute(Next(), "class", "bb-switch");
        b.OpenElement(Next(), "input");
        b.AddAttribute(Next(), "type", "checkbox");
        b.AddAttribute(Next(), "checked", BindConverter.FormatValue(value));
        b.AddAttribute(Next(), "onchange", EventCallback.Factory.CreateBinder(this, onChanged, value));
        b.CloseElement();
        b.OpenElement(Next(), "span");
        b.AddAttribute(Next(), "class", "bb-slider");
        b.CloseElement();
        b.CloseElement();

        b.CloseElement();
    }

    private void BuildNumberRow(RenderTreeBuilder b, string title, string desc, int value, int min, int max, Action<int> onChanged)
    {
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-row");

        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-label-wrap");
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-label");
        b.AddContent(Next(), title);
        b.CloseElement();
        if (!string.IsNullOrEmpty(desc))
        {
            b.OpenElement(Next(), "div");
            b.AddAttribute(Next(), "class", "bb-desc");
            b.AddContent(Next(), desc);
            b.CloseElement();
        }
        b.CloseElement();

        b.OpenElement(Next(), "input");
        b.AddAttribute(Next(), "type", "number");
        b.AddAttribute(Next(), "class", "bb-input");
        b.AddAttribute(Next(), "style", "width:82px; text-align:center;");
        b.AddAttribute(Next(), "min", min.ToString());
        b.AddAttribute(Next(), "max", max.ToString());
        b.AddAttribute(Next(), "value", BindConverter.FormatValue(value, CultureInfo.InvariantCulture));
        b.AddAttribute(Next(), "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            if (int.TryParse(e.Value?.ToString(), out int parsed))
            {
                onChanged(Math.Clamp(parsed, min, max));
            }
        }));
        b.CloseElement();

        b.CloseElement();
    }

    private void BuildDoubleRow(RenderTreeBuilder b, string title, string desc, double value, double min, double max, Action<double> onChanged)
    {
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-row");

        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-label-wrap");
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-label");
        b.AddContent(Next(), title);
        b.CloseElement();
        if (!string.IsNullOrEmpty(desc))
        {
            b.OpenElement(Next(), "div");
            b.AddAttribute(Next(), "class", "bb-desc");
            b.AddContent(Next(), desc);
            b.CloseElement();
        }
        b.CloseElement();

        b.OpenElement(Next(), "input");
        b.AddAttribute(Next(), "type", "number");
        b.AddAttribute(Next(), "step", "0.05");
        b.AddAttribute(Next(), "class", "bb-input");
        b.AddAttribute(Next(), "style", "width:82px; text-align:center;");
        b.AddAttribute(Next(), "value", value.ToString("F2", CultureInfo.InvariantCulture));
        b.AddAttribute(Next(), "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            if (double.TryParse(e.Value?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed))
            {
                onChanged(Math.Clamp(parsed, min, max));
            }
        }));
        b.CloseElement();

        b.CloseElement();
    }

    private void BuildTextRow(RenderTreeBuilder b, string title, string hint, string value, Action<string> onChanged)
    {
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "style", "display:flex; flex-direction:column; gap:4px;");

        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-row");
        b.OpenElement(Next(), "span");
        b.AddAttribute(Next(), "class", "bb-label");
        b.AddContent(Next(), title);
        b.CloseElement();
        if (!string.IsNullOrEmpty(hint))
        {
            b.OpenElement(Next(), "span");
            b.AddAttribute(Next(), "class", "bb-macro-hint");
            b.AddContent(Next(), hint);
            b.CloseElement();
        }
        b.CloseElement();

        b.OpenElement(Next(), "input");
        b.AddAttribute(Next(), "type", "text");
        b.AddAttribute(Next(), "class", "bb-input");
        b.AddAttribute(Next(), "style", "width:100%;");
        b.AddAttribute(Next(), "value", value ?? "");
        b.AddAttribute(Next(), "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            onChanged(e.Value?.ToString() ?? "");
        }));
        b.CloseElement();

        b.CloseElement();
    }

    private void BuildTextareaRow(RenderTreeBuilder b, string title, string macroHint, string value, Action<string> onChanged)
    {
        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "style", "display:flex; flex-direction:column; gap:4px;");

        b.OpenElement(Next(), "div");
        b.AddAttribute(Next(), "class", "bb-row");
        b.OpenElement(Next(), "span");
        b.AddAttribute(Next(), "class", "bb-label");
        b.AddContent(Next(), title);
        b.CloseElement();
        if (!string.IsNullOrEmpty(macroHint))
        {
            b.OpenElement(Next(), "span");
            b.AddAttribute(Next(), "class", "bb-macro-hint");
            b.AddContent(Next(), macroHint);
            b.CloseElement();
        }
        b.CloseElement();

        b.OpenElement(Next(), "textarea");
        b.AddAttribute(Next(), "class", "bb-textarea");
        b.AddAttribute(Next(), "value", value ?? "");
        b.AddAttribute(Next(), "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            onChanged(e.Value?.ToString() ?? "");
        }));
        b.CloseElement();

        b.CloseElement();
    }

    private void AddSelectOption(RenderTreeBuilder b, string val, string text)
    {
        b.OpenElement(Next(), "option");
        b.AddAttribute(Next(), "value", val);
        b.AddContent(Next(), text);
        b.CloseElement();
    }

    private int Next() => ++_seq;
}
