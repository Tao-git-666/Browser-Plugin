---
name: reverse-entity-writer
description: 从目标实体反查负责创建或更新它的程序集、注册步骤与业务条件，排查没有生成、触发不符或执行后无结果。
version: 1.0
triggers: 谁创建,哪个插件创建,哪个插件更新,没有创建,没有生成,没有更新,设备档案,触发条件不对,插件未触发,插件没触发
required-tools: search_environment_code
requires-signals: environment-code-library
---

## 从目标反查来源

1. 区分源操作实体和预期写入的目标实体。用户当前位于设备档案，创建它的插件可能注册在维修单、派工单或自定义 Action 上，不能只查询设备档案 Create/Update 步骤。
2. 优先确认目标逻辑名。先查现有元数据；不确定时可用中文业务词搜索代码注释定位候选，再用代码中的精确实体名检索，不凭中文编造 new_ 前缀。
3. search_environment_code 返回 DLL、哈希、索引时间、静态片段和该 DLL 的候选步骤。按 nextOffset 继续翻页。找到 EntityLogicalName 常量、早绑定类型、Retrieve 或查询条件不等于找到 Create/Update。用 occurrence 和 nextOccurrence 检查后续出现位置。
4. read_environment_code 必须用实际返回的 assembly_id，沿命中方法、公共方法、Execute/接口路由确认实际调用链。用 nextStepOffset 查看其余注册步骤。方法正文未读完时保持 keyword/occurrence 不变，用 nextContextOffset 作为 context_offset 继续读取；更换 keyword/occurrence 时重置 context_offset 为 0。不能把同 DLL 内所有步骤都说成触发该方法的步骤；跨程序集或反射调用缺证时保留断点。

## 核对触发条件

依次检查注册消息、源实体、启用状态、执行阶段、同步/异步、过滤字段；再检查源码中的状态判断、Depth、Target 类型、映像/参数缺失、提前 return、查询为空、重复记录判断及异常处理。过滤字段表示请求包含指定列，不等同于业务值实际发生变化；不能只凭最终字段值判定请求是否包含它。

注册信息来自同步时快照。涉及最近调整的注册步骤时，先建议同步更新；已授权业务数据读取时，可通过 query_crm_data 对已知步骤 ID 读取当前注册信息，不能把旧快照冒充当前配置。

## 判断这一次运行

源码说明可能路径，不能证明这次执行。已有失败响应时先读运行证据；用户授权数据读取且环境支持时，query_crm_data 可按已确认插件类型、时间窗口查询 plugintracelog 的 typename、createdon、messageblock、exceptiondetails、correlationid，top 保持较小。没有时间或关联标识时询问必要信息，不用最新任意日志归因。异步步骤还需按对应任务检查 system job/asyncoperation 状态，未找到可关联任务时说明缺证。

日志未启用、已清理或没有读权限时，“查不到日志”不是“没有触发”。外部 API、工作流或 Power Automate 的实现不可见时不能排除。只做查询，不开启日志设置、不重放创建/更新、不修改注册或业务数据。

## 完成标准

向用户直接说明有证据支持的来源、触发条件及失败位置，区分未符合注册条件、进入代码后提前返回、异常/回滚、异步未完成与证据不足。说明代码库覆盖度和时间对结论的影响，不强制固定答案模板。

## 回归案例

- 源工单 Update 插件创建目标设备档案：不能误答成设备档案 Create 插件。
- 实体名只出现在 Retrieve：不能当作创建者。
- 同 DLL 有两个不同入口：必须确认哪一个会调用命中方法。
- 日志为空且跟踪未启用：不能断言插件未执行。
- DLL 相同但步骤过滤字段变更：复用反编译代码仍须更新注册信息。
