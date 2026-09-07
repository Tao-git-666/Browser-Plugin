---
name: custom-api-error
description: 排查 D365 自定义 API、Action 或项目封装接口返回 4xx/5xx 的问题。
version: 1.2
triggers: 自定义API报错,自定义 API 报错,Action报错,接口400,接口500,API报错,invokeHiddenApiAsync
required-tools: find_business_logic,read_javascript_function,resolve_custom_api,read_custom_api_implementation
required-when-available: read_runtime_errors
---

## 取证顺序

确认前端传入的操作名、业务路由和可见参数，再解析 API 的注册类型、程序集和实现代码。不能用当前实体普通插件步骤代替 API 实现。

## 条件分支

- 存在实际失败响应时，优先使用 operation、路由、状态码和错误文本定位实现分支。
- 参数或当前记录条件决定分支时，只读取已授权的必要字段。
- 没有响应正文时，不得猜测服务器最终抛出的具体信息。

## 停止条件

已把前端调用、API 注册和实现中的成功或抛错分支连成一条证据链；无法定位具体分支时，明确缺少的参数或运行时响应。

## 公共分发入口

对于 new_service 一类公共入口，ServiceHostPlugin 只是分发层。必须继续查操作路由（例如 CSTechnicalSupport/SubmitRdSupport）、路由注册、反射查找或实际业务方法。读取片段缺少被调用方法时，换用精确类型或方法作为搜索锚点，而不是反复读取入口相同片段。

只有找到同名方法而没有分发对应关系，不能宣布已找到实际实现。跨程序集依赖未采集、反射目标无法确定、磁盘 DLL 不可读取或外部服务实现不可见时，明确指出断点。

## 错误证据优先级

失败响应或可读取的运行日志用于确认本次执行，代码中的 throw/catch 用于解释可能路径。“发生一个或多个错误”是包装错误，不足以确定内部异常。没有内部异常和实际分支证据时，给出已确认的失败位置及缺少的证据，不列举与已发生请求矛盾的前置校验作为本次原因。
