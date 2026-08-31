# CrmLogicLens API — Windows Server 部署

该 API 部署在独立 Windows Server 上，不需要登录 CRM 后端服务器，也不保存浏览器 Cookie 或 CRM 密码。Edge 扩展用当前用户的 CRM 会话读取只读配置，再将快照发送到本 API。

## 1. 服务器准备

1. 安装 .NET 10 Hosting Bundle，并重启 IIS。
2. 新建独立站点和应用程序池；应用程序池选择“无托管代码”。
3. 为站点配置可信的 HTTPS 证书。
4. 建立专用数据目录，例如 `D:\CrmLogicLens\Data`，只向应用程序池身份授予读写权限。不要把磁盘根目录、网站根目录或 CRM 共享目录作为数据目录。

发布命令：

```powershell
dotnet publish .\src\CrmLogicLens.Api\CrmLogicLens.Api.csproj -c Release -o .\publish\api
```

将 `publish\api` 的内容复制到 IIS 站点目录。

## 2. 生产配置

在部署目录中创建 `appsettings.Production.json`，例如：

```json
{
  "AllowedHosts": "logiclens.contoso.local",
  "Storage": {
    "DataDirectory": "D:\\CrmLogicLens\\Data",
    "MaxArtifactBytes": 26214400,
    "MaxSnapshotBytes": 67108864,
    "MaxRequestBodyBytes": 100663296,
    "MaxArtifactsPerSnapshot": 128,
    "ExcludeSecureConfiguration": true
  },
  "Decompiler": {
    "WorkerPath": "D:\\CrmLogicLens\\Decompiler\\CrmLogicLens.Decompiler.Worker.exe",
    "TempDirectory": "D:\\CrmLogicLens\\DecompilerTemp",
    "TimeoutSeconds": 60,
    "MaxInputBytes": 26214400,
    "MaxOutputBytes": 8388608,
    "MaxAssembliesPerSnapshot": 8,
    "MaxDiagnosticCharacters": 4096
  },
  "Security": {
    "EnableWindowsAuthentication": true,
    "CorsAllowedOrigins": [
      "chrome-extension://EDGE_EXTENSION_ID"
    ],
    "RateLimit": {
      "PermitLimit": 60,
      "WindowSeconds": 60,
      "QueueLimit": 4
    }
  }
}
```

`CorsAllowedOrigins` 必须是精确的扩展 Origin，不支持通配符，末尾不能带 `/`。扩展发起请求时需要使用 `credentials: "include"`。

## DeepSeek / OpenAI 兼容大模型

服务端使用 OpenAI 兼容的 `chat/completions` 协议调用模型服务，默认配置为 DeepSeek。非敏感配置放在 `appsettings.json` 的 `AI` 节；`Provider` 只用于状态展示，实际调用地址和模型由 `BaseUrl`、`Model` 决定。

本地开发可复制 `appsettings.Local.example.json` 为 `appsettings.Local.json`，填写 `AI:BaseUrl`、`AI:Model` 和 `AI:ApiKey`。该本机文件已被 Git 忽略并且默认不进入发布目录；环境变量和命令行配置优先级更高。由于它仍是本机明文文件，应限制文件 ACL，不要共享或提交。

开发环境启动前可在当前进程中设置：

```powershell
$env:AI__BaseUrl = "https://api.deepseek.com"
$env:AI__Model = "deepseek-v4-pro"
$env:AI__ApiKey = "<DEEPSEEK_API_KEY>"
dotnet run --project src\CrmLogicLens.Api\CrmLogicLens.Api.csproj --launch-profile http
```

生产 Windows Server 建议使用 IIS 加密配置、专用机器级密钥库或企业秘密管理系统注入 `AI__ApiKey`，并限制只有分析服务身份能读取。`GET /api/v1/capabilities` 仅返回提供商、是否已配置及模型名，绝不返回 Key。

问答先由本地静态分析选出证据，再交给已配置模型解释。默认 `MaxContextTokens` 为 `262144`，调查轮输出 `MaxInvestigationCompletionTokens` 为 `4096`，最终回答 `MaxCompletionTokens` 为 `8192`。总上下文包含系统提示、工具历史、代码证据和输出。服务会在请求前估算总预算，并将“调查取证”与“最终业务回答”分成两次调用，避免工具历史挤占最终回答。调查阶段不限制工具总次数或轮数，`MaxInvestigationSeconds` 默认 `900`；到期后停止新工具调用并用已有证据完成回答。每个后续调查请求都会继续携带之前的 assistant 推理状态、工具调用与工具结果。单请求超时也设为 900 秒，由调查剩余时间进一步约束。

最终回答只携带经过排序和截断的高价值证据，默认最多 `180000` 字符。模型返回的证据 ID 会在服务端校验；如果编造引用、超时、限流或鉴权失败，系统会自动降级为本地证据回答。

## 诊断 Skills

发布输出必须保留 `DiagnosticSkills/<skill-name>/SKILL.md`。服务启动时会一次性验证并加载这些服务器维护的排查流程；名称、格式、文件大小或必查工具不合法时会拒绝启动，防止模型读取未受控流程。

每次问答只先暴露 Skill 名称和说明。模型选中后才读取完整流程，并在最终回答前调用进度检查；服务端还会独立验证必查工具是否实际调用。Skill 只能引用代码内允许的只读工具，不能注册脚本、扩大 CRM 数据权限或重放写请求。修改 Skill 后需要重新部署或重启服务，建议将 Skill 版本与变更评审记录一并纳入发布流程。

`Decompiler:WorkerPath` 指向单独发布的反编译 Worker。可使用以下命令单独发布：

```powershell
dotnet publish .\src\CrmLogicLens.Decompiler.Worker\CrmLogicLens.Decompiler.Worker.csproj -c Release -r win-x64 --self-contained false -o D:\CrmLogicLens\Decompiler
```

API 会为每个 DLL 创建随机的独立临时目录，用参数列表启动 Worker，限制输入、输出、超时和诊断文本大小，并在结束后删除该次临时目录。超时时会终止整个 Worker 进程树。Worker 只解析 PE/.NET 元数据，不使用 `Assembly.Load` 也不运行目标 DLL。生成的 C# 会作为服务器内部的 `DecompiledCSharp` artifact 进入证据分析；客户端不能直接上传该派生类型。

capabilities 端点报告 Worker 是否已配置、是否存在及实际资源上限，但不向客户端暴露服务器路径。未配置、超时或某个 DLL 失败会进入 EvidenceGraph warnings，其他窗体、Ribbon、JS 和插件分析仍会完成。

## 3. Windows 身份验证

生产环境默认启用 Negotiate：

1. 在 IIS 站点的“身份验证”中启用 Windows 身份验证。
2. 禁用匿名身份验证。
3. 用 AD 组限制站点访问，并将服务器地址放入域内浏览器的本地 Intranet 区域。
4. 应用程序池使用专用的低权限域账号或虚拟账号；只授予 API 数据/临时目录读写权限和 Worker 目录读取/执行权限。Worker 继承该低权限身份，不应授予 CRM 服务器、数据库、GAC 或业务网络共享的权限；建议通过 Windows Firewall 禁止该账号/进程的出站网络访问。

开发环境始终允许匿名访问；这个行为不由生产配置覆盖。如果生产环境确实要放开匿名访问，必须显式将 `EnableWindowsAuthentication` 设为 `false`。

## 4. IIS 请求大小

Base64 会使 DLL 和代码内容增大约三分之一。ASP.NET Core 限制由 `MaxRequestBodyBytes` 控制，IIS 还有独立的 `maxAllowedContentLength` 限制。在站点 `web.config` 的 `system.webServer/security/requestFiltering/requestLimits` 中将其设为大于或等于 `MaxRequestBodyBytes`。不应无限制放大这两个值。

## 5. 运行检查

- `GET /health` 必须返回 Healthy；该检查会验证数据目录可写。
- `GET /api/v1/capabilities` 用于扩展读取大小限制、媒体类型、身份验证模式和 Worker 状态。
- 将 IIS 应用程序池的空闲超时设为 `0`，并启用“始终运行”和站点预加载，避免长时反编译/分析任务被回收。
- 备份数据目录。其中快照、作业、分析结果使用原子替换，artifact 按 SHA-256 内容寻址去重。
- 不要将 `Storage:ExcludeSecureConfiguration` 设为 `false`，除非组织已完成专门的密钥和隐私评审。
