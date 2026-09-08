# D365 诊断 Skill 检查与优化记录（2026-09-08）

本次检查并更新项目 `src/CrmLogicLens.Api/DiagnosticSkills` 下全部 13 个 Skill。范围是 CRM Logic Lens 服务端的诊断流程；未修改用户的 Codex 全局技能、插件缓存、CRM 配置或业务数据。

## 优化方法

逐个阅读原流程，再对照 `DiagnosticSkillCatalog` 的匹配/加载约束、`EvidenceToolSession` 的工具参数和必查项校验，以及扩展实际采集的数据。按“现象 → 取证 → 条件判断 → 处理建议 → 证据缺口”补充可执行分支，保留原有取证与只读边界。

- 13 个 Skill 全部提升小版本；新增 81 行“场景与处理”说明。部分行细化已有场景，不代表新增 81 项采集能力。
- 触发短语从 94 个调整为 147 个，净增加 53 个；新增 54 个，删除容易误匹配的通用业务对象词“设备档案”。
- 补充部分用户失效、新建/编辑差异、异步回调、未保存值、跨实体写入、代码/注册快照过期等分支。
- 根据真实工具能力说明缺失情况：网格选择数量、父容器实时状态、请求体、未采集视图及外部实现不能凭空补齐。
- 完成标准区分“已确认原因”“只确认现象”“缺少证据”；要求最终检查保留 evidenceGaps，工具已调用不等于根因已确认。
- 每个入口仍为独立可加载的短文档，当前每个文件约 1,190–2,633 字符；没有增加运行时不会加载的外部流程依赖。

## 逐项改动

| Skill | 版本 | 场景行数 | 主要补充和修正 |
| --- | --- | --- | --- |
| button-execution-error | 1.2 → 1.3 | 6 | 无反应、一直转圈、网格参数、弹窗打开失败、重复提交、虚假成功提示；确认函数后再读取，避免虚构函数名。 |
| button-not-visible-or-disabled | 1.1 → 1.2 | 6 | 选择数量、新建/保存差异、用户差异、规则刷新、不同命令位置和发布后旧资源；按实际规则组合解释。 |
| custom-api-error | 1.2 → 1.3 | 6 | 参数、访问拒绝、路径/注册、包装异常、2xx 业务失败、超时；先确认操作与节点，再读取真实实现。 |
| custom-page-empty-list | 1.2 → 1.3 | 6 | 查询为空和前端未显示分开；增加二次筛选、竞态、查找器/子网格、分页及缓存处理。 |
| data-not-updated | 1.1 → 1.2 | 6 | 提交模式、赋值与事件联动、数据库/页面差异、成功提示、后续覆盖和关联记录；明确事务与异步边界。 |
| explain-current-logic | 1.1 → 1.2 | 6 | 按钮、加载、字段联动、保存影响、不同结果和未知错误的分流；按真实调用解释顺序。 |
| field-not-editable | 1.2 → 1.3 | 6 | 多控件、条件只读、整表只读、用户权限差异、派生内容和状态不一致；按问题需要读取实时值。 |
| field-not-visible | 1.3 → 1.4 | 6 | 父容器隐藏、不同窗体、加载闪现、表头控件、用户与应用差异；不混淆单控件和整字段状态。 |
| field-value-rules | 1.0 → 1.1 | 7 | 条件必填、清空、多来源赋值、金额精度、日期转换、查找/选项类型和事件触发；不凭常识补写算法。 |
| plugin-not-triggered | 1.1 → 1.2 | 6 | 包含禁用步骤、过滤字段语义、Target/映像、提前返回、间接调用、异步任务和旧版本。 |
| recorded-runtime-error | 1.1 → 1.2 | 7 | 首条相关异常、并行请求、二次提示、缺失脚本、无错误空列表、录制不完整及 iframe 来源。 |
| reverse-entity-writer | 1.0 → 1.1 | 7 | 只读命中与写入区分、通用仓储、批量请求、多入口、重复判断、外部依赖、代码库覆盖与分页停止。 |
| save-failed | 1.1 → 1.2 | 6 | 客户端拦截、保存模式、循环保存、平台错误、同步回滚和保存后失败；按证据决定是否查插件。 |

## 必查工具的调整

`required-when-available` 的实际含义是“工具一旦可用就必须调用”，不是“模型自行判断是否需要”。因此业务值读取和依赖明确节点的操作不能简单放进此列表。

| Skill | 调整 | 保留的取证要求 |
| --- | --- | --- |
| field-not-editable | 移除可用即强制执行的 read_current_form_values。 | 解释本次状态/条件且已有数据授权时仍读取最小字段；通用规则不强制读业务值。 |
| button-execution-error | 将 read_javascript_function 从无条件必查改为发现真实函数后的分支。 | 保留查找、追踪；发现绑定函数就读源码。 |
| custom-api-error | 基础必查改为 find_business_logic、trace_evidence；解析和实现读取按已发现操作/节点推进。 | resolve_custom_api 成功后，现有服务端机制仍强制 read_custom_api_implementation；新增测试验证此约束。 |
| data-not-updated / save-failed | 当前实体插件列表改为证据指向服务端写入后的分支。 | 不要求纯前端问题查询插件；真正进入服务端链仍必须按流程核对。 |
| custom-api-error / save-failed | 录制工具可用时要求读取录制；data-not-updated 增加可用失败响应必查。 | 保持录制/响应的可用性约束，不要求调用不存在的工具。 |

没有改动匹配算法、工具白名单或服务端校验代码。上述调整是元数据和流程优化；正文中的条件分支仍依赖模型遵循，不能由必查项测试证明每个分支都一定正确执行。

## 修正的关键判断

- 插件列表默认 `enabled_only: true`，现在诊断未触发时要求 `false`，以便看见禁用步骤。这由项目工具定义核对。
- `setValue` 不自动触发 OnChange；字段赋值、联动与保存分别取证。依据：[Microsoft setValue 文档](https://learn.microsoft.com/en-us/power-apps/developer/model-driven-apps/clientapi/reference/attributes/setvalue)。
- `submitMode=never` 可能导致保存后显示恢复服务器值，不应直接归因插件覆盖。依据：[Microsoft setSubmitMode 文档](https://learn.microsoft.com/en-us/power-apps/developer/model-driven-apps/clientapi/reference/attributes/setsubmitmode)。
- 过滤字段关注请求包含哪些列，而非其值是否真的变化。依据：[Microsoft 插件注册文档](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/register-plug-in)。
- 同步 PostOperation 可以处于事务中；异步任务不与原保存共用该事务。依据：[Microsoft 数据库事务文档](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/scalable-customization-design/database-transactions)。

在线 Dataverse 文档用于核对相关机制，不代表本地部署支持所有在线能力；Skill 已要求按实际版本与采集工件判断。

## 验证结果

在 `DiagnosticSkillCatalogTests.cs` 中增加 61 个执行用例：44 个常见问法匹配、6 个依赖信号的场景、5 个防止业务对象词/可用信号误导路由的案例、5 个工具门槛案例，以及 1 个 API 解析后的实现必查案例。

- Skill 专项测试：85/85 通过。
- `CrmLogicLens.Core.Tests` 完整回归：140/140 通过，无跳过。
- 13 个 Skill 经项目实际加载器检查名称、元数据、大小、工具白名单和信号；全部成功加载。
- 构建输出中 13 个 Skill 的 SHA-256 与源码一致。
- `git diff --check` 通过。

测试命令：

```powershell
dotnet test tests/CrmLogicLens.Core.Tests/CrmLogicLens.Core.Tests.csproj --filter FullyQualifiedName~DiagnosticSkillCatalogTests --no-restore
dotnet test tests/CrmLogicLens.Core.Tests/CrmLogicLens.Core.Tests.csproj --no-build --no-restore
```

没有运行通用 Codex `quick_validate.py`：它不接受此项目的 `version`、`triggers`、`required-tools` 等自定义元数据。使用项目自己的真实加载器和测试验证，避免为通过不适用的校验破坏格式。

构建报告 NU1900：NuGet 漏洞信息下载超时；编译和测试均通过，但本次没有获得完整的依赖漏洞检查结果。测试使用本地/模拟证据，不代表已经在真实 CRM、实际模型与所有用户权限组合下验收；本次未运行未改动的扩展测试，也未声称量化模型准确率或耗时提升。

## 备份、生效与后续验收

修改前的 13 个 Skill 保存在工作区 `artifacts/skill-audit-20260908-095621/DiagnosticSkills/`。同目录的 `before.json`、`after.json` 保存前后信息与哈希，`tests/` 保存专项和完整回归的 TRX 结果。此备份目录由现有 `.gitignore` 排除，不进入产品发布。

服务通过单例目录加载 Skill，不会自动热更新；下次重新启动分析服务后加载新流程。本次已验证构建复制，没有重启正在运行的服务。独立部署时需把更新后的 `DiagnosticSkills` 随服务发布；扩展未变更，不需要为本次 Skill 更新重新加载扩展。

真实环境验收优先采用“按钮一直转圈”“请求非空但列表不显示”“保存后值恢复”“禁用插件未触发”“录制中存在无关错误”“目标实体跨来源生成”这些区分性案例。核对选中的 Skill、实际工具 trace、引用证据和缺口；只在已获授权的范围进行业务值读取，不用自动写操作验证诊断。
