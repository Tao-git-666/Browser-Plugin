---
name: save-failed
description: 排查 D365 记录保存被阻止、保存时报错或保存后回滚的问题。
version: 1.1
triggers: 保存失败,保存报错,无法保存,保存被阻止,保存回滚,保存时错误
required-tools: find_business_logic,trace_evidence,list_current_entity_plugin_steps
required-when-available: read_runtime_errors
---

## 取证顺序

先检查窗体 OnSave 及相关前端校验，再按当前实体核对 Create/Update 的启用插件步骤、阶段和过滤字段。

## 条件分支

- OnSave 调用 `preventDefault` 或校验函数时，读取对应脚本条件。
- 存在实际失败响应时，优先用错误文本定位同步服务端逻辑。
- 只有找到相关步骤且注册信息不足时，才读取插件实现。
- 保存成功后才发生异常时，检查异步或后操作逻辑，不把它描述成保存前校验。

## 停止条件

已区分前端取消、同步插件抛错、平台校验、事务回滚或保存后的异步处理；证据不足时说明缺少实际错误响应或插件跟踪日志。
