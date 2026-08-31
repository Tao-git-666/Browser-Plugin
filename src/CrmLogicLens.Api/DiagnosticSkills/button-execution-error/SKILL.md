---
name: button-execution-error
description: 排查点击 D365 按钮后出现错误、没有完成动作或收到服务端失败响应的问题。
version: 1.1
triggers: 点击按钮报错,按钮出现报错,按钮执行失败,点了没反应,按钮失败,为什么会报错,发生一个或多个错误
required-tools: find_business_logic,trace_evidence,read_javascript_function,resolve_custom_api
required-when-available: read_recorded_runtime_events,read_runtime_errors
---

从按钮、命令和绑定函数追踪实际调用，不得从同实体的任意 Create/Update 插件步骤猜测原因。读取按钮函数后，确认它是直接保存、调用 Web API，还是调用自定义 API/Action。

若解析到自定义 API，必须继续读取其实现插件；若存在浏览器已观测的失败响应，必须把状态码和响应正文与实现代码对照。不得重放写请求。只有代码判断依赖当前记录值时才按授权查询必要字段。
