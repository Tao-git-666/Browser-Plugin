---
name: button-not-visible-or-disabled
description: 排查 D365 命令栏按钮不显示、灰色或不可点击的问题。
version: 1.1
triggers: 按钮不显示,按钮看不到,按钮灰色,按钮不可用,按钮不能点击,为什么没有按钮,命令栏按钮消失
required-tools: find_business_logic,trace_evidence
---

## 取证顺序

先定位按钮和绑定命令，区分“不显示”和“显示但禁用”，再分别检查 DisplayRule、EnableRule 和它们引用的函数。

## 条件分支

- 规则调用 JavaScript 时，只读取对应规则函数。
- 规则依赖字段值、选择数量或窗体状态时，在用户授权后读取判断所需的最小数据。
- 找不到按钮定义时，保留“当前采集范围未找到”的边界，不得断言按钮不存在。

## 停止条件

已确认控制按钮状态的规则及其当前条件，或已明确缺少应用级 Ribbon、运行时状态或权限证据。不要只根据按钮标签推断规则。
