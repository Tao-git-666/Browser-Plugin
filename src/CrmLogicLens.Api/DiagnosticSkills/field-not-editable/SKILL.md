---
name: field-not-editable
description: 排查 D365 字段只读、灰色或无法编辑的问题。
version: 1.1
triggers: 字段不能编辑,字段不可编辑,字段只读,字段灰色,无法修改,不能修改
required-tools: find_business_logic,trace_evidence
required-when-available: read_current_form_values
---

先确认当前 FormXML 中控件是否静态禁用，再追踪窗体加载和字段变更事件；若发现 `setDisabled` 或相关封装函数，读取对应脚本。不要在没有证据时直接归因于字段安全或用户权限。

用户授权读取数据时，读取目标字段的当前窗体实时值、`isDirty` 和控件 `disabled` 状态；只有判断条件依赖数据库记录值时才额外查询必要字段。回答应指出已确认的控制条件，以及仍需由字段安全或记录状态验证的边界。
