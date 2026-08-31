---
name: field-not-visible
description: 排查字段未出现在当前 D365 窗体、被隐藏，或随条件消失的问题。
version: 1.1
triggers: 字段不显示,字段看不到,字段消失,为什么不显示,隐藏字段,没有这个字段
required-tools: find_business_logic,trace_evidence
required-when-available: read_current_form_values
---

必须区分“当前 FormXML 没有这个控件”和“控件存在但运行时不可见”。依次核对字段控件、所在节和选项卡的静态可见性，再检查当前窗体事件及调用 `setVisible` 的相关脚本。用户已授权时，读取目标字段的当前窗体实时值和控件 `visible` 状态；只有存在相关函数时才读取函数片段。

若证据仍不足，应明确还需要当前实际窗体、业务规则或权限证据；不能把“没有搜索到”写成“字段一定不在窗体上”。
