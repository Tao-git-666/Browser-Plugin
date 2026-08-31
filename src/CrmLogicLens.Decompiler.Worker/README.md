# Decompiler Worker

该进程使用 `ICSharpCode.Decompiler` 将一个托管 DLL 静态转换成 C# 文本。它不会使用 `Assembly.Load`，也不会调用目标程序集中的任何代码。

```powershell
dotnet run --project .\src\CrmLogicLens.Decompiler.Worker -- `
  --input C:\Staging\Plugin.dll `
  --output C:\Staging\Plugin.decompiled.cs
```

生产环境应由分析服务在受限临时目录中启动 Worker，并进一步施加低权限身份、网络隔离、CPU/内存限制和超时。Worker 成功时向标准输出写入 JSON 清单，失败时向标准错误写入结构化错误。
