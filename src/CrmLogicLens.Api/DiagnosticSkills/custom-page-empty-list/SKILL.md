---
name: custom-page-empty-list
description: 排查 D365 自定义页面、HTML Web Resource、弹窗、查找器或派工页面中列表为空、没有可用人员或筛选条件不清楚的问题。
version: 1.0
triggers: 自定义页面没有数据,Web Resource没有数据,没有可用的服务人员,没有可用人员,派工没有人员,列表为空,查找器没有数据,筛选条件是什么,为什么查不到人员
required-tools: find_business_logic,read_recorded_dataverse_queries
required-when-available: read_javascript_function
---

先定位打开自定义页面或 HTML Web Resource 的按钮、命令和页面组件，再读取故障录制中实际发生的 Dataverse 查询。优先检查返回 0 条的查询，依据其中的实体、字段、运算符、联表和排序结构说明页面实际用了哪些筛选条件，不得只根据按钮名称或字段显示名猜测。

沿 `opens-custom-page` 和 `loads-script` 关系找到页面脚本；读取构造 `$filter`、FetchXML、查询参数或服务人员集合的相关函数，确认每个条件来自固定配置、当前窗体值、当前用户、组织机构、技能、区域、状态还是时间。若筛选值已脱敏且判断必须知道实际值，仅在用户已授权时按最小范围调用 `read_current_form_values` 或 `query_crm_data`。

若录制期间没有查询，明确说明页面可能在录制器接入前已加载、使用了 PCF/非 Dataverse 接口或本地缓存，并建议重新录制“打开页面直到列表为空”的完整过程。不得把“请求成功但返回 0 条”说成接口报错，也不得重放写请求。
