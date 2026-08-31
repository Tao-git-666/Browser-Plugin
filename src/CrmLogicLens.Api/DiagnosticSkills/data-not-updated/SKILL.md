---
name: data-not-updated
description: 排查操作完成后字段或关联数据没有按预期更新的问题。
version: 1.0
triggers: 数据没有更新,字段没更新,值没有变化,操作成功但没变化,状态没变,没有写入
required-tools: find_business_logic,trace_evidence,list_current_entity_plugin_steps
---

沿前端动作确认写入目标、字段和调用方式，再检查当前实体相关插件是否覆盖、回滚或异步更新数据。只有用户授权且答案依赖实际值时，才查询当前记录的必要字段。

应区分没有发起写入、写入被拒绝、事务回滚、后续逻辑覆盖和界面未刷新。不能仅根据最终页面值判断服务端没有执行。
