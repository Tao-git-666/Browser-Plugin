---
name: recorded-runtime-error
description: 排查用户主动录制并复现的一次 D365 页面、按钮、脚本、请求或自定义 API 错误。
version: 1.0
triggers: 刚才录制,录制报错,停止录制,复现报错,操作步骤报错,分析刚才的错误,页面出现错误
required-tools: read_recorded_runtime_events,find_business_logic
required-when-available: read_runtime_errors
---

先按时间顺序读取录制事件，找出报错前最后一次用户操作和第一条异常。点击文本只能用于定位入口，不能证明后台逻辑。

若时间线包含失败的 Dataverse API/Action 请求，先从实际路径和响应中确定 operation，再追踪相关 JavaScript；脚本明确调用自定义 API 时，必须依次调用 resolve_custom_api 和 read_custom_api_implementation。若只是 JavaScript 或 Promise 异常，围绕函数名、文件名和错误文本读取相关脚本。只有证据链明确落到实体 Create/Update/Delete 消息时才读取该实体插件步骤。

不得重放请求，不得推断录制中没有采集的键盘输入、字段值、Cookie、令牌或请求体。最终回答先说明最可能的直接原因，再给出录制步骤、运行时错误和静态代码之间的对应关系；证据不足时明确指出还缺什么。
