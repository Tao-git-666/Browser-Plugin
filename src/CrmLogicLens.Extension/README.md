# CRM 逻辑透镜 Edge 扩展

这是一个无需构建工具的 Edge Manifest V3 侧栏扩展。它在当前 Dynamics 365 页面中使用公开的 `Xrm` 上下文，执行只读 Web API 查询，并把代码和配置快照送到独立的内网分析服务器。

## 当前能力

- 兼容 UCI `Xrm.Utility.getPageContext()`，并以 `Xrm.Page` 支持较老的本地版 CRM 窗体。
- 仅读取当前未托管自定义 `SystemForm.formxml`，并提取窗体事件引用的自定义 JavaScript Web Resource；对脚本明确打开的未托管 HTML Web Resource，继续采集其外部和内嵌脚本。
- 故障录制覆盖同一 CRM 页签内的同源 iframe，并记录成功 Dataverse GET 的脱敏查询结构与返回条数，用于解释自定义页面空列表。
- 读取当前实体 Ribbon 后只保留绑定未托管自定义 JavaScript 的按钮、命令和相关规则，排除微软原生定义。
- 从 FormXML 和 Ribbon XML 中解析关联脚本名称，并仅下载 `ismanaged=false` 或处于自定义层的 JavaScript。
- 仅查询未托管/自定义插件程序集、类型和步骤；消息与筛选器按自定义步骤引用的 GUID 精确读取，不再扫描全部微软目录。
- 默认允许预取当前实体关联的数据库 DLL；扩展只下载由当前实体插件步骤明确引用的程序集。AI 只有在选中相关步骤且确实需要代码细节时，才让独立服务器的隔离工作器按需反编译；管理员仍可关闭此选项。
- 读取当前实体及字段的元数据，供分析服务把逻辑名称翻译为业务名称。
- 上传 `SnapshotUpload` 后轮询分析任务，并在完成后显示证据链和启用问答。
- 问答结果按 AI 针对问题生成的原始顺序展示；Markdown 会转换为安全 DOM，技术证据和节点 ID 默认折叠。
- 每次回答可展开“AI 分析过程”，查看匹配并采用的诊断 Skill、必查项完成度、实际工具调用、查询目标、结果数量和耗时；不展示不可审计的模型内部隐藏思维文本。
- “允许 AI 按需读取 CRM 业务数据和当前窗体值”默认关闭；用户显式授权后，AI 可按需读取必要字段的客户端实时值（包括未保存修改），也可通过浏览器以当前登录用户身份执行有界只读查询。CRM 权限继续生效，不会向分析服务器发送 Cookie 或令牌。
- 采集前读取 `/api/v1/capabilities`，自动把单项、总量和数量预算收窄到服务器实际限制；服务器不可用时才使用保守默认值。

插件程序集目录查询**刻意不包含 `content` 字段**。默认开启相关插件代码分析时，扩展才会对当前实体明确关联、数据库部署的程序集逐个读取 `content`；最多 12 个、单个最多约 24 MiB，且所有 DLL 合计最多 30 MiB。上传不等于立即反编译，AI 选中相关步骤后才按需处理。磁盘/GAC 部署 DLL 不在数据库内容中，仍需要 CRM 服务器文件权限或由管理员另行提供文件。

## 在 Edge 中加载

1. 打开 `edge://extensions`。
2. 打开“开发人员模式”。
3. 选择“加载解压缩的扩展”。
4. 选择本目录 `src/CrmLogicLens.Extension`。
5. 打开一条 Dynamics 365 记录，单击工具栏中的“CRM 逻辑透镜”。
6. 在侧栏底部填写独立分析服务器地址，保存后单击“采集并分析当前逻辑”。

默认开发服务器是 `http://localhost:5165`。实际部署时，应改为独立 Windows Server 的 HTTPS 地址，例如 `https://logiclens.contoso.local`。

## 分析服务器接口

扩展使用以下接口：

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| `GET` | `/health` | 测试连接 |
| `GET` | `/api/v1/capabilities` | 协商快照容量与可用能力 |
| `POST` | `/api/v1/snapshots` | 上传 `SnapshotUpload` |
| `GET` | `/api/v1/jobs/{jobId}` | 查询分析状态 |
| `GET` | `/api/v1/snapshots/{snapshotId}/evidence` | 读取证据图 |
| `POST` | `/api/v1/chat` | 针对快照提问 |

`ArtifactUpload.kind` 使用 camelCase 字符串枚举：`formXml`、`javaScript`、`ribbonXml`、`pluginCatalog`、`pluginAssembly`、`entityMetadata`、`customPageCatalog`。`contentBase64` 是对应原始内容的 Base64；D365 Web Resource 和 Plugin Assembly 的 `content` 已经是 Base64，因此不会重复编码。

插件目录 JSON 的结构为：

```json
{
  "steps": [
    {
      "id": "...",
      "name": "...",
      "messageId": "...",
      "filterId": "...",
      "eventHandlerId": "...",
      "stage": 40,
      "mode": 0,
      "rank": 1,
      "filteringAttributes": "name,statuscode",
      "stateCode": 0
    }
  ],
  "messages": [{ "id": "...", "name": "Update" }],
  "filters": [{ "id": "...", "primaryObjectTypeCode": "account", "secondaryObjectTypeCode": null }],
  "types": [{ "id": "...", "typeName": "Contoso.AccountPlugin", "name": "...", "assemblyId": "..." }],
  "assemblies": [{ "id": "...", "name": "...", "version": "...", "sourceType": 0, "isolationMode": 1, "path": null, "sourceHash": "..." }]
}
```

其中所有 lookup GUID 均去除花括号并转换为小写。

## 身份与数据边界

- CRM 查询在当前 CRM 页面主执行环境中发起，只允许同一组织 URL 下的 Web API `GET` 请求，使用用户当前的 CRM 会话。
- 发送到分析服务器的是显式构造的 `SnapshotUpload` JSON；代码不会读取或复制 CRM Cookie、Bearer token 或网页存储凭据。
- 分析服务器请求使用它自己来源下的浏览器凭据，以兼容内网 Windows Authentication。浏览器的同源 Cookie 规则不会把 CRM Cookie 发送给另一台服务器。
- 未授权时扩展不采集当前记录字段值；授权后只在 AI 明确请求时读取当前窗体的必要字段或执行有界只读查询。扩展不读取插件 secure/unsecure configuration。`pluginassembly.content` 仅在“允许分析当前实体相关插件代码”开启时读取，并严格限定为当前实体插件步骤引用的数据库程序集。
- 服务器地址禁止包含用户名、密码、查询参数或 URL 片段；上传地址也不能与 CRM 使用同一来源。
- CRM 查询中的单项失败会成为侧栏 warning，其余 Form、Ribbon、脚本、元数据或插件目录仍会继续采集。

## 部署注意事项

- `manifest.json` 为兼容任意本地 CRM 和独立分析服务器声明了 `http://*/*` 与 `https://*/*` 主机权限。企业部署时建议由 Edge 管理策略固定允许的 CRM 与分析服务器域名，并相应收窄这里的权限。
- 分析服务器必须允许此扩展的 `chrome-extension://...` Origin 进行 CORS 请求。若启用 Windows Authentication，还需同时允许 credentials，且 `Access-Control-Allow-Origin` 不能使用 `*`。
- 生产环境使用受企业信任的 HTTPS 证书。默认 HTTP 地址只用于同机开发。
- 使用插件代码分析时，分析服务必须配置并发布 `CrmLogicLens.Decompiler.Worker`，且 IIS 应用池应使用专用低权限身份；不应把该身份加入 CRM 或服务器管理员组。
- Web API 版本默认根据 CRM 客户端版本和只读探针检测，也可以在侧栏中固定为 `v8.2`、`v9.0`、`v9.1` 或 `v9.2`。
- 直接访问 D365 DOM 不属于本实现的依赖。若具体 CRM 补丁版本没有暴露 `Xrm`，应通过一个受支持的轻量 CRM Solution 在窗体加载时暴露上下文快照。

## 文件说明

- `manifest.json`：MV3 权限、侧栏与后台入口。
- `background.js`：上下文定位、只读 CRM 查询、快照组装和分析服务器通信。
- `sidepanel.html` / `sidepanel.css` / `sidepanel.js`：中文证据线路、告警、证据链和对话界面。
