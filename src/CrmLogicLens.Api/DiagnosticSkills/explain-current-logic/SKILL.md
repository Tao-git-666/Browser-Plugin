---
name: explain-current-logic
description: 解释当前 D365 窗体、按钮、字段或保存动作的业务逻辑；用于没有命中特定故障类型的一般问题。
version: 1.0
triggers: 有什么用,会发生什么,业务逻辑,做什么,怎么运行
required-tools: find_business_logic,trace_evidence
fallback: true
---

先定位用户所说的界面元素或动作，再沿实际绑定关系追踪。只读取与问题直接相关的脚本或插件实现；不要为了“全面”扫描无关组件。

最终回答应先说明业务效果和触发条件。除非用户明确询问技术实现，否则把函数名、逻辑名和节点编号留在技术依据中。
