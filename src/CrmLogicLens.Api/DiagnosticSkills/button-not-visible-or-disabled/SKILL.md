---
name: button-not-visible-or-disabled
description: 排查 D365 命令栏按钮不显示、灰色或不可点击的问题。
version: 1.0
triggers: 按钮不显示,按钮看不到,按钮灰色,按钮不可用,按钮不能点击,为什么没有按钮
required-tools: find_business_logic,trace_evidence
---

先定位按钮和绑定命令，再检查 DisplayRule、EnableRule 及其调用的 JavaScript 函数。若规则依赖当前记录字段、选择数量或窗体状态，用户授权后只读取判断所需的数据。

必须区分“不显示”和“显示但禁用”；两者使用的规则可能不同。不要只根据按钮标签推断规则。
