---
name: custom-api-error
description: 排查 D365 自定义 API、Action 或项目封装接口返回 4xx/5xx 的问题。
version: 1.0
triggers: 自定义API,自定义 API,Action报错,接口400,接口500,API报错,new_service
required-tools: find_business_logic,read_javascript_function,resolve_custom_api,read_custom_api_implementation
required-when-available: read_runtime_errors
---

确认前端传入的操作名、业务路由和参数，再解析该 API 的注册类型和程序集。必须读取已注册实现的有限反编译片段；不能用当前实体普通插件步骤代替 API 实现。

存在实际失败响应时，优先使用其中的错误文本定位代码分支。把请求参数、当前记录条件、响应错误和抛错位置串成一条证据链；没有响应正文时明确说明无法确认服务器最终抛出的具体信息。
