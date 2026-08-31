---
name: save-failed
description: 排查 D365 记录保存被阻止、保存时报错或保存后回滚的问题。
version: 1.0
triggers: 保存失败,保存报错,无法保存,保存被阻止,保存回滚,保存时错误
required-tools: find_business_logic,list_current_entity_plugin_steps
required-when-available: read_runtime_errors
---

先检查窗体 OnSave 及相关前端校验，再按当前实体核对 Create/Update 的启用插件步骤和过滤字段。只有找到相关步骤且注册信息不足以解释时，才读取对应插件实现。

应区分前端取消保存、同步插件抛错、服务端校验失败和保存成功后的异步处理。不要把后操作插件当成保存前校验。
