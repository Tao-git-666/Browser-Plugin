# 独立 Windows Server 部署

## 推荐拓扑

- 独立域内 Windows Server，不与 CRM 服务器共机。
- IIS 反向托管 ASP.NET Core，使用企业 CA 签发的 HTTPS 证书。
- IIS Windows Authentication 开启、Anonymous 关闭；API 通过 AD 组授权。
- 数据目录位于非 Web 根目录，只有应用池身份和运维组可访问。
- 扩展 ID、CRM 域名和 API 域名由 Edge 企业策略固定。

## 发布

```powershell
dotnet publish .\src\CrmLogicLens.Api\CrmLogicLens.Api.csproj `
  -c Release -r win-x64 --self-contained false `
  -o C:\Deploy\CrmLogicLens

dotnet publish .\src\CrmLogicLens.Decompiler.Worker\CrmLogicLens.Decompiler.Worker.csproj `
  -c Release -r win-x64 --self-contained false `
  -o C:\Deploy\CrmLogicLens\Decompiler
```

在 IIS 中创建独立应用池和站点，将发布目录设为物理路径。配置项通过 IIS 配置或受保护的配置提供程序注入，不要把生产密码或证书私钥写入仓库。

将 `Decompiler:WorkerPath` 指向上述 Worker 可执行文件，并把临时目录放在网站根目录之外。完整配置示例见 [API 部署说明](../src/CrmLogicLens.Api/DEPLOYMENT.md)。

## 上线检查

- `/health` 只能暴露最小健康状态。
- CORS 只允许正式 `chrome-extension://<固定扩展ID>` 来源。
- 若浏览器持续收到 Negotiate 401，通过 Edge 企业策略把分析服务器加入 `AuthServerAllowlist`；本系统不需要 Kerberos 委派到 CRM。
- 配置快照和单文件上限，并启用请求限流。
- 定期备份证据索引；原始 DLL 按保留期限删除。
- DLL 静态检查 Worker 使用低权限身份，默认无外网访问。
- 审计同步人、查看人、快照、程序集哈希和删除操作。
