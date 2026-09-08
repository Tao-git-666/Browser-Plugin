# CRM Logic Lens

CRM Logic Lens 是一个面向 Dynamics 365 Customer Engagement（本地部署）的业务逻辑解释器。它由 Edge 侧栏扩展和独立的 Windows 分析服务组成：扩展使用当前用户已经登录的 CRM 会话执行只读采集，服务端解析窗体、JavaScript、Ribbon 和插件注册信息，再把结果转换成带证据引用的中文说明。

## 当前实现范围

- 识别当前 UCI/经典窗体上下文。
- 读取 `SystemForm.formxml`、关联 JavaScript Web Resource 和实体 Ribbon；继续识别按钮脚本打开的未托管 HTML Web Resource、自定义页面目录及其外部/内嵌脚本，建立“按钮 → 页面 → 脚本”证据链。
- 从当前实体的消息筛选器开始，只读取相关自定义插件步骤、类型和程序集；可预取这些步骤引用的数据库 DLL。
- 识别 `invokeHiddenApiAsync` 等项目封装调用，只按脚本实际引用的名称追踪 Custom API/旧式 Action、实现插件和 DLL，不扫描全组织 API 目录。
- 将快照发送到独立分析服务器，异步生成证据图。
- 解析窗体事件、Ribbon Command、常见 Xrm/formContext 调用和插件步骤关系。
- 在独立分析服务器上由 DeepSeek（OpenAI 兼容接口）使用受限只读工具逐步查找、追踪、读取脚本和当前实体插件，并用中文解释业务逻辑；每条结论的证据引用由服务器校验。
- AI 的模型、Base URL 和 API Key 均由 `AI` 配置节控制；调查工具历史与最终业务回答分开生成，并在发送前检查上下文预算。调查不再按工具次数或轮数提前终止，而是在最多 15 分钟内持续调用必要工具；每次后续请求都会携带此前 assistant 推理状态、工具调用和工具结果，保持调查上下文连续。
- 分析服务器内置版本化 D365 诊断 Skills。AI 必须先匹配并读取相关 `SKILL.md`，服务端会验证必查工具是否实际调用，未完成时阻止模型提前回答；采用的 Skill 和检查进度会显示在“AI 分析过程”中。
- 13 个诊断 Skill 的适用场景、工具条件和验证结果见 [Skill 检查与优化记录](docs/diagnostic-skills-audit.md)。
- 在用户明确开启数据读取授权后，可记录页面中已实际发生的 Dataverse 4xx/5xx 请求方法、路径、状态码和有限响应正文；不记录请求体、Cookie 或令牌，不会重放 POST。
- 在同一授权下，AI 可按需读取当前打开窗体中最多 20 个必要字段的客户端实时值、显示文本、修改状态和控件状态，包括尚未保存的用户修改；不会无差别上传整张窗体。
- 支持用户主动录制一次故障复现：点击“录制报错”后跨 CRM 同源 iframe 记录安全化的点击步骤、JavaScript/Promise/控制台异常、D365 错误提示、失败的 Fetch/XHR，以及成功 Dataverse GET 的脱敏查询结构和返回条数；点击“停止并分析”后冻结时间线并自动让 AI 从最后操作追踪到页面脚本、自定义 API 和插件实现。录制不采集键盘输入、字段值、Cookie、令牌、请求体或响应记录，也不会自动重放操作。
- 对“派工页面没有可用人员/列表为空/筛选条件是什么”类问题，AI 会优先读取录制到的 0 条结果查询，再沿自定义页面脚本定位 `$filter`、FetchXML、联表及条件来源；实际筛选值默认脱敏，需要时只能在用户已授权后按最小范围读取。
- 对用户问题生成确定性说明，并返回证据位置与置信度。
- 对上传的相关 DLL 先做 PE/CLR 元数据检查；只有 AI 选中当前实体的具体插件步骤并需要源码时，才由独立低权限进程按需反编译为 C#。全过程绝不加载或执行目标程序集。

尚未承诺：CRM 服务器磁盘/GAC DLL 获取、微软内置按钮内部实现、混淆代码完整还原、没有失败响应或日志时对运行时分支的绝对判定。

## 项目结构

```text
src/CrmLogicLens.Extension/   Edge Manifest V3 侧栏扩展
src/CrmLogicLens.Core/        解析器、证据图、回答引擎
src/CrmLogicLens.Api/         独立 Windows 分析服务
src/CrmLogicLens.Api/DiagnosticSkills/ 可维护的 D365 故障排查流程
src/CrmLogicLens.Decompiler.Worker/ 隔离 DLL 反编译进程
tests/CrmLogicLens.Core.Tests/核心分析测试
tests/fixtures/               端到端插件测试程序集
samples/                      可离线演示的 D365 样例
docs/                         架构、探针和部署说明
```

## 本地启动

要求：.NET 10 SDK、Microsoft Edge。

首次启动前，在 `src/CrmLogicLens.Api/appsettings.Local.json` 的 `AI` 节填写 `BaseUrl`、`Model` 和 `ApiKey`。默认是 DeepSeek 的 `https://api.deepseek.com` 和 `deepseek-v4-pro`。该文件不会进入 Git，也不会复制到发布目录；以后启动不需要再输入 Key。环境变量 `AI__BaseUrl`、`AI__Model`、`AI__ApiKey` 和命令行配置仍可覆盖本机文件。

```powershell
dotnet restore .\CrmLogicLens.sln
dotnet test .\CrmLogicLens.sln
dotnet build .\src\CrmLogicLens.Decompiler.Worker
dotnet build .\tests\fixtures\CrmLogicLens.SamplePlugin
node .\tests\extension\background.test.cjs
dotnet run --project .\src\CrmLogicLens.Api
```

然后打开 `edge://extensions`，启用“开发人员模式”，选择“加载解压缩的扩展”，目录指向 `src/CrmLogicLens.Extension`。首次打开侧栏后，把分析服务器地址设置为 API 控制台显示的地址。

复现页面错误或空列表时，在侧栏问答区域点击“录制报错”，回到 CRM 完成会触发问题的操作（例如打开派工页面直到显示“没有可用人员”），再点击“停止并分析”。插件会立即冻结本次时间线并自动发起诊断，无需手工整理开发人员工具中的请求。

真实 CRM 接入前，先执行 [只读可行性探针](docs/d365-probe.md)。Windows Server 部署参见 [部署说明](docs/deployment-windows.md)。

## 权限和数据边界

- 扩展不把 CRM Cookie、ADFS Token 或用户密码发送给分析服务器。
- 默认不采集业务记录字段值和插件 Secure Configuration。
- 插件 DLL 只针对当前实体步骤或当前脚本明确引用的自定义 API 按设置读取；只接受数据库中可读取的托管程序集。
- 磁盘/GAC 部署的 DLL 无法凭系统管理员安全角色从浏览器取得，必须由有服务器文件权限的管理员另行提供。
- 生产环境必须使用 HTTPS、固定扩展 ID、AD 组授权、来源白名单和访问审计。
- 分析第三方程序集前，需确认组织和软件权利人的授权。
