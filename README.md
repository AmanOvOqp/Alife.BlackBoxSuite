# 全息感知黑盒套件 (Alife.BlackBoxSuite)

专注体征记账 × 双轨分层存储 × 毫秒边缘唤醒 × 桌宠灵犀主动赋能

---

## 一、套件定位与双轨哲学
1. 底层绝对静默记账：五维体征（应用前台与同屏伴读、键盘节拍与滚轮、鼠标动力学、声卡会话峰值与GSMTC原生媒体、外设与文件脉搏）在底层静默高精度采样与防抖入库，平时不产生任何无效汇报弹窗，杜绝上下文污染与大模型 KV Cache 显存击穿。
2. 双轨数据分层架构：
   - 长期核心资产（永久落盘）：剪贴板知识库与上下文截图闪存库（自动关联窗口标题标签）、每日应用画像（按天统计全天时长、全屏专注、同屏伴读与Ctrl+S产出）。
   - 瞬态体征切片（内存滑动队列）：击键节拍、滚轮强度、鼠标甩动/沉思/连点、声卡进程峰值、外设连断与文件脉搏仅在内存滑动（默认上限300条，关机自清，零磁盘垃圾）。
3. 纯节拍隐私绝缘：低级键盘钩子仅统计击键频次与退格次数（APM 与纠错比率），底层严禁记录任何按键键码与字符内容；剪贴板密钥正则脱敏（GitHub Token、OpenAI Key）。

---

## 二、四大主动提醒事件（主动赋能桌宠）
为前台桌宠（子美大人）装填毫秒级灵犀弹药，支持独立开关与自定义文案模板，派发方式支持 Poke（默认轻量戳一戳/事件注入）与 Chat（触发回复）。

1. 毫秒级归座唤醒 (EnableReturnNotifyPrompt)
   - 机制：底层低级键盘/鼠标钩子微秒级边缘触发。用户从离席状态重新触碰物理键盘或鼠标的第一瞬间直接唤醒桌宠睁眼迎接，无需等待轮询定时器。
   - 默认模板 (ReturnPromptTemplate)："阿瞒回到屏幕前了。"

2. 防误判真离席提醒 (EnableIdleNotifyPrompt)
   - 机制：结合全屏专注静止门槛（默认300秒）、常规桌面空闲（默认180秒）及影音防假离线仲裁，确认用户真正走开时，单次边缘触发通知桌宠挂起与记录。
   - 默认模板 (IdlePromptTemplate)："阿瞒离开工位了。"

3. 音频端点切换与防社死 (EnableAudioEndpointNotifyPrompt)
   - 机制：Win32 Core Audio 端点感知与智能名称识别（Headphone/Arctis/Earphone/AirPods等）。
   - 耳机连入模板 (HeadphonePluggedPromptTemplate)："阿瞒已连接/切换到耳机（{DeviceName}），私密听音模式已就绪，可正常进行语音互动。"
   - 扬声器外放模板 (HeadphoneUnpluggedPromptTemplate)："阿瞒已切换为扬声器外放（{DeviceName}），请注意周围环境，建议转为静默纯文本模式防外放打扰。"
   - 通用切换模板 (AudioEndpointGenericPromptTemplate)："默认音频输出设备已切换为：{DeviceName}。"

4. 暴躁甩鼠标/卡壳告警 (EnableRageShakeNotifyPrompt)
   - 机制：鼠标动力学 50ms 降采样（最高20Hz，零绝对坐标落盘），捕捉 1 秒内高频方向反转（默认>=6次），识别用户在写代码或阅读时遇到难题、报错或思路卡壳。
   - 默认模板 (RageShakePromptTemplate)："检测到阿瞒在应用 [{ProcessName}] 中频繁高速往复晃动鼠标，疑似遇到难题、报错或思路卡壳，请温柔关切安抚。"
   - 支持模板宏替换：{ProcessName}、{WindowTitle}。

---

## 三、调优与控制接口 (XML Functions)
桌宠或跟班可通过以下 XML 函数调阅与调优：

1. 调阅黑盒切片与实时状态：
   <GetBlackBoxStatus/>
2. 调阅应用驻留时间线：
   <QueryBlackBoxTimeline minDurationSeconds="10" count="20"/>
3. 调阅键盘打字节拍：
   <QueryTypingCadence count="10"/>
4. 调阅剪贴板长期知识库：
   <QueryClipboardHistory keyword="考研" count="10"/>
5. 调阅上下文截图视觉闪存：
   <QueryScreenshotFlash windowTitle="SumatraPDF" count="5"/>
6. 调阅每日应用与伴读专注画像：
   <QueryDailyFocusProfile targetDate="2026-11-01"/>
7. 动态调优黑盒参数与四大主动提醒：
   <ConfigureBlackBox enableReturnNotify="true" returnPrompt="阿瞒回到屏幕前了。" enableIdleNotify="true" idlePrompt="阿瞒离开工位了。" audioEndpointNotify="true" rageShakeNotify="true" notifyDispatchMode="Poke"/>

---

## 四、作者信息
- 插件开发者：AmanOvO
- 架构专供：Alife Framework桌宠前台灵犀赋能底座