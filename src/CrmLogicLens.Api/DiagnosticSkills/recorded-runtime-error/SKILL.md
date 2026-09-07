---
name: recorded-runtime-error
description: 排查用户主动录制并复现的一次 D365 页面、按钮、脚本、请求或自定义 API 错误。
version: 1.1
triggers: 刚才录制,录制报错,停止录制,复现报错,操作步骤报错,分析刚才的错误
requires-signals: runtime-recording
required-tools: read_recorded_runtime_events,find_business_logic
required-when-available: read_runtime_errors
---

## 取证顺序

按时间顺序读取录制事件，确定报错前最后一次用户操作和第一条异常。点击文本只能定位入口，不能证明后台逻辑。

## 条件分支

- 时间线包含失败的 Dataverse API/Action 时，从实际路径和响应确定 operation，再追踪相关脚本与实现。
- 只有 JavaScript 或 Promise 异常时，围绕函数名、文件名和错误文本读取脚本。
- 只有证据链明确落到 Create/Update/Delete 消息时，才读取当前实体插件步骤。

## 停止条件

已将最后操作、第一错误和相关静态代码对应起来，或已明确缺少响应正文、脚本工件或调用关系。回答先说明直接原因，再列证据链和未知项。
