---
name: button-execution-error
description: 排查点击 D365 按钮后出现错误、没有完成动作或收到服务端失败响应的问题。
version: 1.2
triggers: 点击按钮报错,按钮出现报错,按钮执行失败,点了没反应,按钮失败,点击后报错,按钮操作失败
required-tools: find_business_logic,trace_evidence,read_javascript_function
required-when-available: read_recorded_runtime_events,read_runtime_errors
---

## 取证顺序

从按钮、命令和绑定函数追踪实际调用。读取按钮函数后，区分直接保存、Dataverse Web API、自定义 API/Action 和纯客户端逻辑。

## 条件分支

- 脚本明确调用自定义 API/Action 时，调用 `resolve_custom_api`；解析成功后继续读取实现插件。
- 存在已观测失败响应时，把请求路径、状态码和有限响应正文与对应代码分支对照。
- 只有代码分支依赖当前记录值时，才按授权读取必要字段。

## 停止条件

已定位失败发生在按钮规则、客户端函数、服务端接口或未知边界之一，并取得支持该定位的直接证据；否则列出尚缺的运行时响应或字段条件。不得用同实体的任意 Create/Update 插件步骤替代按钮的真实调用链。
