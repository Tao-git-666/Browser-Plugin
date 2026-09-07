---
name: field-not-editable
description: 排查 D365 字段只读、灰色或无法编辑的问题。
version: 1.2
triggers: 字段不能编辑,字段不可编辑,字段只读,字段灰色,无法修改,不能修改
required-tools: find_business_logic,trace_evidence
required-when-available: read_current_form_values
---

## 取证顺序

确认当前 FormXML 中控件是否静态禁用，再追踪窗体加载和字段变更事件。发现 `setDisabled` 或项目封装时，只读取对应函数。

## 条件分支

- 用户授权时，读取目标字段的实时值、`isDirty` 和控件 `disabled` 状态。
- 只有判断条件依赖数据库值时，才额外查询必要字段。
- 静态配置和脚本均未禁用时，把字段安全、记录状态或权限列为待验证项，而不是直接归因。

## 停止条件

已确认静态禁用、脚本条件或运行时控件状态之一；否则明确仍缺字段安全或权限证据。
