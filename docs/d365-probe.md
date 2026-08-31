# D365 只读可行性探针

在开发扩展的正式部署策略前，使用系统管理员账号验证以下项目。所有操作都应为 GET，不创建、不修改、不发布 CRM 组件。

## 必须记录的信息

- Dynamics 365 完整版本号。
- 当前使用 Unified Interface 还是经典 Web Client。
- 组织 URL、认证方式（Windows/Claims/IFD）和浏览器是否允许集成认证。
- `$metadata` 中实际存在的 Web API 版本和实体集名称。

## 六项通过条件

1. `Xrm.Utility.getPageContext()` 或兼容回退能够取得当前实体和窗体 ID。
2. 当前用户能够读取指定 `SystemForm` 的 `name` 和 `formxml`。
3. 能够读取 FormXML 中至少一个 JavaScript Web Resource 的 `content`。
4. `RetrieveEntityRibbon` 可返回当前实体的压缩 Ribbon XML。
5. 能够读取 `sdkmessageprocessingstep`、`sdkmessage`、`sdkmessagefilter`、`plugintype` 和 `pluginassembly` 的目录字段。
6. 至少选择一个 `sourcetype=Database` 的自有程序集，确认 `content` 是否可读；不要批量下载。

若 Ribbon Function 在该 on-prem 版本的 Web API 中不存在，应记录错误并改用 Organization Service 实现，不要猜测接口版本。

## 失败分类

- `401/403`：登录上下文或 CRM 权限问题。
- `404`：Web API 版本、实体集或 Function 在当前版本不存在。
- Form ID 为空：启用兼容回退；仍不准确时再考虑安装 OnLoad 快照桥。
- `PluginAssembly.content` 为空且 `sourcetype=Disk`：当前权限条件下不能继续获取 DLL。

