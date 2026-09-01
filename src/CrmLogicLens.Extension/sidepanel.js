"use strict";

const ui = {
  connectionBadge: document.querySelector("#connectionBadge"),
  refreshContextButton: document.querySelector("#refreshContextButton"),
  openToolsButton: document.querySelector("#openToolsButton"),
  scopeTitle: document.querySelector("#scopeTitle"),
  entityValue: document.querySelector("#entityValue"),
  formValue: document.querySelector("#formValue"),
  versionValue: document.querySelector("#versionValue"),
  apiValue: document.querySelector("#apiValue"),
  scopeNote: document.querySelector("#scopeNote"),
  artifactCount: document.querySelector("#artifactCount"),
  collectButton: document.querySelector("#collectButton"),
  traceSteps: [...document.querySelectorAll(".trace-step")],
  warningPanel: document.querySelector("#warningPanel"),
  warningCount: document.querySelector("#warningCount"),
  warningList: document.querySelector("#warningList"),
  evidencePanel: document.querySelector("#evidencePanel"),
  evidenceCount: document.querySelector("#evidenceCount"),
  analysisSummary: document.querySelector("#analysisSummary"),
  evidenceList: document.querySelector("#evidenceList"),
  chatReady: document.querySelector("#chatReady"),
  faultRecorder: document.querySelector("#faultRecorder"),
  recordButton: document.querySelector("#recordButton"),
  recordButtonLabel: document.querySelector("#recordButtonLabel"),
  recordStatus: document.querySelector("#recordStatus"),
  recordCount: document.querySelector("#recordCount"),
  chatLog: document.querySelector("#chatLog"),
  chatForm: document.querySelector("#chatForm"),
  questionInput: document.querySelector("#questionInput"),
  sendButton: document.querySelector("#sendButton"),
  toolsDialog: document.querySelector("#toolsDialog"),
  closeToolsButton: document.querySelector("#closeToolsButton"),
  toolCompatibility: document.querySelector("#toolCompatibility"),
  inspectToolGrid: document.querySelector("#inspectToolGrid"),
  diagnosticToolGrid: document.querySelector("#diagnosticToolGrid"),
  toolResultPanel: document.querySelector("#toolResultPanel"),
  toolResultTitle: document.querySelector("#toolResultTitle"),
  toolResult: document.querySelector("#toolResult"),
  copyToolResultButton: document.querySelector("#copyToolResultButton"),
  settingsDialog: document.querySelector("#settingsDialog"),
  openSettingsButton: document.querySelector("#openSettingsButton"),
  closeSettingsButton: document.querySelector("#closeSettingsButton"),
  settingsForm: document.querySelector("#settingsForm"),
  serverUrl: document.querySelector("#serverUrl"),
  apiVersion: document.querySelector("#apiVersion"),
  includeApplicationRibbon: document.querySelector("#includeApplicationRibbon"),
  includePluginAssemblies: document.querySelector("#includePluginAssemblies"),
  allowCrmDataAccess: document.querySelector("#allowCrmDataAccess"),
  testServerButton: document.querySelector("#testServerButton"),
  toast: document.querySelector("#toast")
};

const state = {
  context: null,
  run: null,
  warnings: [],
  collecting: false,
  chatting: false,
  recording: false,
  recordingStartedAt: null,
  recordingEventCount: 0,
  recordingTimer: 0,
  enhancedTools: [],
  enhancedToolResult: null,
  runningEnhancedTool: false,
  pollToken: 0,
  toastTimer: 0
};

document.addEventListener("DOMContentLoaded", initialize);
chrome.runtime.onMessage.addListener(handleRuntimeEvent);

async function initialize() {
  bindEvents();

  const [settingsResult, sessionResult] = await Promise.allSettled([
    request("GET_SETTINGS"),
    request("GET_SESSION")
  ]);

  if (settingsResult.status === "fulfilled") {
    fillSettings(settingsResult.value);
  }

  if (sessionResult.status === "fulfilled" && sessionResult.value) {
    restoreSession(sessionResult.value);
  }

  try {
    state.enhancedTools = await request("GET_ENHANCED_TOOLS");
    renderEnhancedTools();
  } catch {
    state.enhancedTools = [];
  }

  const context = await refreshContext(false);
  await restoreRecordingStatus();
  if (shouldAutoCollect(context)) {
    void collectAndUpload({ automatic: true });
  }
}

function bindEvents() {
  ui.refreshContextButton.addEventListener("click", refreshAndCollect);
  ui.openToolsButton.addEventListener("click", openEnhancedTools);
  ui.closeToolsButton.addEventListener("click", closeEnhancedTools);
  ui.toolsDialog.addEventListener("click", (event) => {
    if (event.target === ui.toolsDialog) closeEnhancedTools();
  });
  ui.copyToolResultButton.addEventListener("click", copyEnhancedToolResult);
  ui.collectButton.addEventListener("click", collectAndUpload);
  ui.openSettingsButton.addEventListener("click", openSettings);
  ui.closeSettingsButton.addEventListener("click", closeSettings);
  ui.settingsDialog.addEventListener("click", (event) => {
    if (event.target === ui.settingsDialog) closeSettings();
  });
  ui.settingsForm.addEventListener("submit", saveSettings);
  ui.testServerButton.addEventListener("click", testServer);
  ui.chatForm.addEventListener("submit", askQuestion);
  ui.recordButton.addEventListener("click", toggleRuntimeRecording);
  ui.allowCrmDataAccess.addEventListener("change", () => renderContext(state.context));
  ui.questionInput.addEventListener("keydown", (event) => {
    if (event.key === "Enter" && !event.shiftKey && !event.isComposing) {
      event.preventDefault();
      ui.chatForm.requestSubmit();
    }
  });
}

async function restoreRecordingStatus() {
  try {
    const status = await request("GET_RUNTIME_RECORDING_STATUS");
    state.recording = Boolean(status?.active);
    state.recordingStartedAt = status?.startedAt || null;
    state.recordingEventCount = Number(status?.eventCount) || 0;
    renderRecordingState();
    if (state.recording) startRecordingTicker();
  } catch {
    state.recording = false;
    renderRecordingState();
  }
}

async function toggleRuntimeRecording() {
  if (state.recording) {
    await stopRuntimeRecording();
  } else {
    await startRuntimeRecording();
  }
}

async function startRuntimeRecording() {
  if (!state.run?.snapshotId || state.chatting) return;
  ui.recordButton.disabled = true;
  try {
    const result = await request("START_RUNTIME_RECORDING");
    state.recording = true;
    state.recordingStartedAt = result.startedAt;
    state.recordingEventCount = Number(result.eventCount) || 0;
    renderRecordingState();
    startRecordingTicker();
    showToast("录制已开始。请在 CRM 中复现问题，然后点击“停止并分析”。");
  } catch (error) {
    showToast(error.message, "bad");
  } finally {
    ui.recordButton.disabled = !state.run?.snapshotId;
  }
}

async function stopRuntimeRecording() {
  ui.recordButton.disabled = true;
  stopRecordingTicker();
  try {
    const recording = await request("STOP_RUNTIME_RECORDING");
    state.recording = false;
    state.recordingEventCount = recording.events?.length || 0;
    state.recordingStartedAt = null;
    renderRecordingState(recording.errorCount || 0);
    showToast(`已冻结 ${state.recordingEventCount} 条事件，正在交给 AI 分析。`);
    ui.questionInput.value = "请分析我刚才录制并复现的报错，按实际操作时间线定位直接原因；如果涉及自定义 API 或插件，请继续读取对应实现代码。";
    ui.chatForm.requestSubmit();
  } catch (error) {
    state.recording = false;
    state.recordingStartedAt = null;
    renderRecordingState();
    showToast(error.message, "bad");
  } finally {
    ui.recordButton.disabled = !state.run?.snapshotId;
  }
}

function startRecordingTicker() {
  stopRecordingTicker();
  state.recordingTimer = window.setInterval(async () => {
    if (!state.recording) return;
    renderRecordingState();
    try {
      const status = await request("GET_RUNTIME_RECORDING_STATUS");
      if (status?.active) {
        state.recordingEventCount = Number(status.eventCount) || 0;
        renderRecordingState();
      }
    } catch { /* the stop action will surface a useful error */ }
  }, 1500);
}

function stopRecordingTicker() {
  if (state.recordingTimer) window.clearInterval(state.recordingTimer);
  state.recordingTimer = 0;
}

function renderRecordingState(errorCount = null) {
  ui.faultRecorder.dataset.state = state.recording ? "recording" : "idle";
  ui.recordButtonLabel.textContent = state.recording ? "停止并分析" : "录制报错";
  if (state.recording) {
    const elapsedSeconds = Math.max(0, Math.floor((Date.now() - (Date.parse(state.recordingStartedAt) || Date.now())) / 1000));
    const minutes = String(Math.floor(elapsedSeconds / 60)).padStart(2, "0");
    const seconds = String(elapsedSeconds % 60).padStart(2, "0");
    ui.recordStatus.textContent = `正在录制 · ${minutes}:${seconds}`;
    ui.recordCount.textContent = `${state.recordingEventCount} 个页面事件 · 复现后停止`;
  } else if (errorCount != null) {
    ui.recordStatus.textContent = "录制已冻结并交给 AI";
    ui.recordCount.textContent = `${state.recordingEventCount} 个事件，其中 ${errorCount} 个错误信号`;
  } else {
    ui.recordStatus.textContent = "遇到难复现的报错？录下这一次操作";
    ui.recordCount.textContent = "只记录点击、异常、错误提示和失败请求";
  }
}

async function refreshAndCollect() {
  const context = await refreshContext(true);
  if (context) await collectAndUpload();
}

function openSettings() {
  if (!ui.settingsDialog.open) ui.settingsDialog.showModal();
}

function closeSettings() {
  if (ui.settingsDialog.open) ui.settingsDialog.close();
}

function openEnhancedTools() {
  if (!state.context) {
    showToast("请先打开 Dynamics 365 记录窗体。", "bad");
    return;
  }
  renderToolCompatibility();
  if (!ui.toolsDialog.open) ui.toolsDialog.showModal();
}

function closeEnhancedTools() {
  if (ui.toolsDialog.open) ui.toolsDialog.close();
}

function renderEnhancedTools() {
  const renderGroup = (container, group) => {
    container.replaceChildren();
    for (const tool of state.enhancedTools.filter((item) => item.group === group)) {
      const button = document.createElement("button");
      button.type = "button";
      button.className = `tool-card tool-${group}`;
      button.dataset.toolId = tool.id;
      const title = document.createElement("strong");
      title.textContent = tool.label;
      const description = document.createElement("span");
      description.textContent = tool.description;
      button.append(title, description);
      button.addEventListener("click", () => runEnhancedTool(tool));
      container.append(button);
    }
  };
  renderGroup(ui.inspectToolGrid, "inspect");
  renderGroup(ui.diagnosticToolGrid, "diagnostics");
}

async function runEnhancedTool(tool) {
  if (!tool || state.runningEnhancedTool) return;
  const confirmed = tool.group !== "diagnostics" || window.confirm(
    `${tool.label} 会重新载入当前 CRM 页面，未保存的更改可能丢失。确定继续吗？`);
  if (!confirmed) return;
  state.runningEnhancedTool = true;
  setEnhancedToolButtonsDisabled(true);
  ui.toolResultPanel.hidden = false;
  ui.toolResultTitle.textContent = `正在运行 ${tool.label}…`;
  ui.toolResult.textContent = "正在从当前 CRM 窗体读取信息。";
  try {
    const result = await request("RUN_ENHANCED_TOOL", { toolId: tool.id, confirmed });
    if (result.navigated) {
      closeEnhancedTools();
      showToast(result.message || `${tool.label} 正在打开。`);
      return;
    }
    state.enhancedToolResult = result;
    ui.toolResultTitle.textContent = result.title || tool.label;
    ui.toolResult.textContent = formatEnhancedToolResult(result);
    showToast(`${tool.label} 已完成；结果已加入本次 AI 问答证据。`);
  } catch (error) {
    state.enhancedToolResult = null;
    ui.toolResultTitle.textContent = `${tool.label} 未完成`;
    ui.toolResult.textContent = error.message;
    showToast(error.message, "bad");
  } finally {
    state.runningEnhancedTool = false;
    setEnhancedToolButtonsDisabled(false);
  }
}

function formatEnhancedToolResult(result) {
  const data = result?.data;
  if (result?.toolId === "form-overview" && data && !Array.isArray(data)) {
    return [
      `实体：${data.entityName || "—"}`,
      `窗体类型：${data.formType ?? "—"}；未保存：${data.formDirty ? "是" : "否"}`,
      `字段：${data.attributeCount ?? 0}；控件：${data.controlCount ?? 0}`,
      `已修改字段：${data.changedFieldCount ?? 0}`,
      `隐藏控件：${data.hiddenControlCount ?? 0}；只读控件：${data.disabledControlCount ?? 0}`,
      `页签：${data.tabCount ?? 0}；分区：${data.sectionCount ?? 0}`,
      `客户端：${data.client || "未知"}；CRM ${data.crmVersion || "未知"}`
    ].join("\n");
  }
  if (Array.isArray(data) && result?.toolId === "changed-fields") {
    if (!data.length) return "当前窗体没有尚未保存的字段。";
    return data.map((field) => {
      const value = result.valuesIncluded && Object.hasOwn(field, "value")
        ? `；当前值：${JSON.stringify(field.value)}`
        : "";
      return `${field.label || field.name} (${field.name})${value}`;
    }).join("\n");
  }
  if (Array.isArray(data) && result?.toolId === "field-states") {
    return data.map((field) => {
      const controls = Array.isArray(field.controls) ? field.controls : [];
      const hidden = controls.length > 0 && controls.every((control) => control.visible === false);
      const disabled = controls.some((control) => control.disabled === true);
      return `${field.label || field.name} (${field.name})：${hidden ? "隐藏" : "可见"}，${disabled ? "只读" : "可编辑"}，${field.requiredLevel || "none"}${field.dirty ? "，已修改" : ""}`;
    }).join("\n");
  }
  if (Array.isArray(data) && result?.toolId === "option-sets") {
    if (!data.length) return "当前窗体没有选项字段。";
    return data.map((field) => {
      const options = (field.options || []).map((option) => `${option.text}=${option.value}`).join("，");
      return `${field.label || field.name} (${field.name})\n  ${options}`;
    }).join("\n");
  }
  if (result?.toolId === "table-processes" && data && !Array.isArray(data)) {
    const processLines = (data.processes || []).map((item) => {
      const triggers = item.triggers?.length ? `；触发：${item.triggers.join("、")}` : "";
      return `${item.kind}：${item.name}${triggers}`;
    });
    const apiLines = (data.customApis || []).map((item) => `绑定自定义 API：${item.name} (${item.uniqueName})`);
    const warningLines = (data.warnings || []).map((item) => `提示：${item}`);
    return [...processLines, ...apiLines, ...warningLines].join("\n") || `实体 ${data.entityName} 没有返回流程或绑定自定义 API。`;
  }
  return JSON.stringify(data ?? result, null, 2);
}

function setEnhancedToolButtonsDisabled(disabled) {
  for (const button of document.querySelectorAll(".tool-card")) {
    button.disabled = disabled;
  }
}

async function copyEnhancedToolResult() {
  const text = ui.toolResult.textContent || "";
  if (!text) return;
  try {
    await navigator.clipboard.writeText(text);
    showToast("工具结果已复制。 ");
  } catch {
    showToast("浏览器未允许复制，请手动选择结果文本。", "bad");
  }
}

function renderToolCompatibility() {
  if (!state.context) {
    ui.toolCompatibility.textContent = "识别 CRM 后可使用。";
    return;
  }
  const deployment = state.context.deploymentType === "online" ? "在线版" :
    state.context.deploymentType === "on-premises" ? "本地版" : "未知部署";
  const transport = String(state.context.transport || "").toUpperCase();
  const client = state.context.clientType || "Web";
  ui.toolCompatibility.textContent = `已识别 ${deployment} CRM · ${transport || "HTTP(S)"} · ${client}。窗体检查只读；字段值仍受连接设置中的授权控制。`;
}

function request(type, payload = {}) {
  return new Promise((resolve, reject) => {
    chrome.runtime.sendMessage({ type, ...payload }, (response) => {
      if (chrome.runtime.lastError) {
        reject(new Error(chrome.runtime.lastError.message));
        return;
      }

      if (!response) {
        reject(new Error("扩展后台没有返回结果。"));
        return;
      }

      if (!response.ok) {
        reject(new Error(response.error || "操作未完成。"));
        return;
      }

      resolve(response.data);
    });
  });
}

function handleRuntimeEvent(message) {
  if (message?.type !== "COLLECTION_PROGRESS") {
    return;
  }

  if (message.step) {
    updateTraceStep(message.step, message.state || "active", message.label || message.detail);
  }

  if (Number.isFinite(message.artifactCount)) {
    setArtifactCount(message.artifactCount);
  }

  if (Array.isArray(message.warnings)) {
    setWarnings(message.warnings);
  }
}

async function refreshContext(showFeedback) {
  setConnection("正在识别", "quiet");
  ui.refreshContextButton.disabled = true;

  try {
    const result = await request("GET_CONTEXT");
    if (state.run?.context && !sameLogicScope(state.run.context, result.context)) {
      state.pollToken += 1;
      state.run = null;
      setChatReady(false);
      ui.evidencePanel.hidden = true;
    }
    state.context = result.context;
    renderContext(result.context);
    setConnection("已连接 CRM", "connected");
    if (showFeedback) {
      showToast("已重新识别当前窗体。");
    }
    if (result.warnings?.length) {
      setWarnings(unique([...state.warnings, ...result.warnings]));
    }
    return result.context;
  } catch (error) {
    state.context = null;
    renderContext(null, error.message);
    setConnection("未识别 CRM", "bad");
    if (showFeedback) {
      showToast(error.message, "bad");
    }
    return null;
  } finally {
    ui.refreshContextButton.disabled = false;
  }
}

function renderContext(context, errorMessage) {
  if (!context) {
    ui.openToolsButton.disabled = true;
    ui.scopeTitle.textContent = "等待识别 CRM 窗体";
    ui.entityValue.textContent = "—";
    ui.formValue.textContent = "—";
    ui.versionValue.textContent = "—";
    ui.apiValue.textContent = ui.apiVersion.value === "auto" ? "自动" : ui.apiVersion.value;
    ui.scopeNote.textContent = errorMessage || "打开一条 Dynamics 365 记录，再开始采集。";
    renderToolCompatibility();
    return;
  }

  ui.openToolsButton.disabled = false;
  const entity = context.entityName || "未识别实体";
  const form = context.formLabel || shortId(context.formId) || "未识别窗体";
  ui.scopeTitle.textContent = context.formLabel ? context.formLabel : `${entity} 记录窗体`;
  ui.entityValue.textContent = entity;
  ui.entityValue.title = entity;
  ui.formValue.textContent = form;
  ui.formValue.title = context.formId || form;
  ui.versionValue.textContent = context.version || "未知";
  ui.apiValue.textContent = context.apiVersion || (ui.apiVersion.value === "auto" ? "待检测" : ui.apiVersion.value);
  ui.scopeNote.textContent = context.entityId
    ? ui.allowCrmDataAccess.checked
      ? `已定位记录 ${shortId(context.entityId)}；AI 可按需读取当前窗体实时值和只读查询 CRM 数据。`
      : `已定位记录 ${shortId(context.entityId)}；不会读取这条记录的字段值。`
    : ui.allowCrmDataAccess.checked
      ? "已定位页面；AI 可按需读取当前窗体实时值和只读查询 CRM 数据。"
      : "已定位页面；不会读取页面中的业务数据值。";
  renderToolCompatibility();
}

async function collectAndUpload(options = {}) {
  if (state.collecting) {
    return;
  }

  state.collecting = true;
  setConnection("正在采集", "collecting");
  state.pollToken += 1;
  state.run = null;
  state.warnings = [];
  resetTrace();
  setWarnings([]);
  setArtifactCount(0);
  setChatReady(false);
  ui.evidencePanel.hidden = true;
  ui.collectButton.disabled = true;
  ui.collectButton.querySelector("span").textContent = options.automatic
    ? "正在自动采集…"
    : "正在沿证据线路采集…";

  try {
    const result = await request("COLLECT_AND_UPLOAD");
    state.context = result.context;
    state.run = {
      snapshotId: result.receipt?.snapshotId,
      jobId: result.receipt?.jobId,
      status: result.receipt?.status || "Queued",
      context: result.context
    };
    renderContext(result.context);
    setArtifactCount(result.receipt?.artifactCount ?? result.artifactCount ?? 0);
    setWarnings(result.warnings || []);
    updateTraceStep("upload", "done", "已安全送达");
    setConnection("分析处理中", "working");

    if (isCompleteStatus(state.run.status)) {
      await analysisCompleted();
    } else if (state.run.jobId) {
      void pollJob(state.run.jobId, ++state.pollToken);
    } else {
      setChatReady(true);
      setConnection("证据已上传", "connected");
    }
  } catch (error) {
    const active = ui.traceSteps.find((step) => step.dataset.state === "active");
    if (active) {
      updateTraceStep(active.dataset.step, "warning", "未完成");
    }
    setConnection("采集未完成", "bad");
    setWarnings(unique([...state.warnings, error.message]));
    showToast(error.message, "bad");
  } finally {
    state.collecting = false;
    ui.collectButton.disabled = false;
    ui.collectButton.querySelector("span").textContent = "重新采集当前逻辑";
  }
}

async function pollJob(jobId, token) {
  const started = Date.now();
  const timeoutMs = 6 * 60 * 1000;

  while (token === state.pollToken && Date.now() - started < timeoutMs) {
    await delay(1800);

    try {
      const job = await request("CHECK_JOB", { jobId });
      const status = job.status || job.state || job.jobStatus || "Running";
      state.run.status = status;

      if (isCompleteStatus(status)) {
        await analysisCompleted();
        return;
      }

      if (isFailureStatus(status)) {
        const detail = job.error || job.errorMessage || "分析服务未能完成这个任务。";
        setConnection("分析失败", "bad");
        setWarnings(unique([...state.warnings, detail]));
        return;
      }

      setConnection(statusLabel(status), "working");
    } catch (error) {
      setWarnings(unique([...state.warnings, `查询分析进度失败：${error.message}`]));
      return;
    }
  }

  if (token === state.pollToken) {
    setConnection("分析仍在后台进行", "warning");
    setWarnings(unique([...state.warnings, "等待分析超时。可以稍后重新打开侧栏继续查看。"]));
  }
}

async function analysisCompleted() {
  setConnection("分析已完成", "complete");
  setChatReady(true);

  if (!state.run?.snapshotId) {
    return;
  }

  try {
    const evidence = await request("GET_EVIDENCE", { snapshotId: state.run.snapshotId });
    renderEvidence(evidence);
  } catch (error) {
    setWarnings(unique([...state.warnings, `证据链暂时无法显示：${error.message}`]));
  }
}

function renderEvidence(result) {
  const graph = result?.graph || result || {};
  const nodes = Array.isArray(graph.nodes) ? graph.nodes : [];
  const warnings = Array.isArray(graph.warnings) ? graph.warnings : [];
  const currentEntity = findCurrentEntity(nodes);
  const businessNodes = nodes
    .filter((node) => isBusinessEvidenceNode(node, currentEntity))
    .sort((left, right) => businessEvidencePriority(right) - businessEvidencePriority(left));
  const summary = buildBusinessEvidenceSummary(nodes, currentEntity);

  ui.evidencePanel.hidden = false;
  ui.evidenceCount.textContent = `${businessNodes.length} 条业务证据`;
  ui.analysisSummary.textContent = summary;
  ui.evidenceList.replaceChildren();

  for (const node of businessNodes.slice(0, 6)) {
    const item = document.createElement("li");
    const kind = document.createElement("span");
    const copy = document.createElement("span");
    const label = document.createElement("strong");
    const detail = document.createElement("span");

    kind.className = "evidence-kind";
    kind.textContent = node.kind || "证据";
    copy.className = "evidence-copy";
    label.textContent = node.label || node.id || "未命名证据";
    detail.textContent = node.summary || node.location || node.artifactName || "";
    copy.append(label, detail);
    item.append(kind, copy);
    ui.evidenceList.append(item);
  }

  if (businessNodes.length > 6) {
    const more = document.createElement("li");
    more.className = "evidence-copy";
    more.textContent = `还有 ${businessNodes.length - 6} 条业务证据，可在问答中继续追问。`;
    ui.evidenceList.append(more);
  }

  if (warnings.length) {
    setWarnings(unique([...state.warnings, ...warnings]));
  }
}

const BUSINESS_EVIDENCE_KINDS = new Set([
  "PageContext",
  "EntityMetadata",
  "FieldMetadata",
  "Form",
  "Field",
  "FormEvent",
  "FormHandler",
  "RibbonButton",
  "RibbonCommand",
  "RibbonRule",
  "RibbonJavaScriptAction",
  "JavaScriptBehavior",
  "PluginStep",
  "CSharpPluginBehavior",
  "DecompiledPluginExcerpt"
]);

function findCurrentEntity(nodes) {
  const page = nodes.find((node) => node?.kind === "PageContext");
  return readEvidenceProperty(page, "Entity") || null;
}

function isBusinessEvidenceNode(node, currentEntity) {
  if (!node || !BUSINESS_EVIDENCE_KINDS.has(node.kind)) {
    return false;
  }
  if (node.kind === "PluginStep" && currentEntity) {
    return readEvidenceProperty(node, "Entity") === currentEntity;
  }
  return true;
}

function readEvidenceProperty(node, name) {
  const properties = node?.properties;
  if (!properties || typeof properties !== "object") {
    return null;
  }
  const key = Object.keys(properties).find((candidate) => candidate.toLowerCase() === name.toLowerCase());
  const value = key ? properties[key] : null;
  return value == null ? null : String(value);
}

function businessEvidencePriority(node) {
  return {
    PageContext: 100,
    EntityMetadata: 95,
    RibbonButton: 90,
    FormEvent: 85,
    JavaScriptBehavior: 80,
    PluginStep: 75,
    CSharpPluginBehavior: 70,
    DecompiledPluginExcerpt: 68,
    FormHandler: 65,
    RibbonCommand: 60,
    RibbonRule: 55,
    RibbonJavaScriptAction: 50,
    FieldMetadata: 30,
    Field: 25,
    Form: 20
  }[node?.kind] || 0;
}

function buildBusinessEvidenceSummary(nodes, currentEntity) {
  const count = (kind, predicate = () => true) =>
    nodes.filter((node) => node?.kind === kind && predicate(node)).length;
  const formEvents = count("FormEvent");
  const commands = count("RibbonCommand");
  const frontEndBehaviors = count("JavaScriptBehavior");
  const pluginSteps = count("PluginStep", (node) =>
    !currentEntity || readEvidenceProperty(node, "Entity") === currentEntity);
  const relatedAssemblies = count("PluginAssembly", (node) =>
    readEvidenceProperty(node, "ContentAvailable")?.toLowerCase() === "true");
  const parts = [];
  if (formEvents) parts.push(`${formEvents} 个窗体事件`);
  if (commands) parts.push(`${commands} 个命令栏动作`);
  if (frontEndBehaviors) parts.push(`${frontEndBehaviors} 个前端业务行为`);
  if (pluginSteps) parts.push(`${pluginSteps} 个当前实体插件步骤`);
  if (relatedAssemblies) parts.push(`${relatedAssemblies} 个相关插件程序集`);
  if (!parts.length) {
    return "业务证据索引已经建立，可以开始提问。";
  }
  return `已识别 ${parts.join("、")}。DLL 内部公共库和逐方法目录不计入业务证据；插件源码由 AI 按相关步骤需要读取。`;
}

async function askQuestion(event) {
  event.preventDefault();
  const question = ui.questionInput.value.trim();

  if (!question || !state.run?.snapshotId || state.chatting) {
    return;
  }

  state.chatting = true;
  ui.questionInput.value = "";
  ui.questionInput.disabled = true;
  ui.sendButton.disabled = true;
  appendMessage("user", question);
  const thinking = appendMessage("assistant", "正在沿证据链查找…", null, null, { compact: true });

  try {
    const response = await request("ASK_QUESTION", {
      snapshotId: state.run.snapshotId,
      question
    });
    thinking.remove();
    appendMessage(
      "assistant",
      response.answer || "分析服务没有返回文字说明。",
      response.citations,
      response.trace
    );
  } catch (error) {
    thinking.classList.add("assistant-error");
    thinking.textContent = `这次提问未完成：${error.message}`;
  } finally {
    state.chatting = false;
    ui.questionInput.disabled = false;
    ui.sendButton.disabled = false;
    ui.questionInput.focus();
  }
}

function appendMessage(role, text, citations, trace, options = {}) {
  const message = document.createElement("div");
  message.className = `message ${role}-message`;

  if (role === "assistant" && !options.compact) {
    message.classList.add("business-answer-message");
    renderBusinessAnswer(message, text, citations, trace);
  } else {
    message.textContent = text;
  }

  ui.chatLog.append(message);
  ui.chatLog.scrollTop = ui.chatLog.scrollHeight;
  return message;
}

function renderBusinessAnswer(container, answer, citations, trace) {
  const source = String(answer || "").replace(/\r\n?/g, "\n");
  const evidenceIds = extractEvidenceIds(source);
  const article = document.createElement("article");
  article.className = "business-answer";

  const content = document.createElement("div");
  content.className = "natural-answer";
  if (!renderAnswerMarkdown(content, removeCitationMarkers(source))) {
    const empty = document.createElement("p");
    empty.className = "answer-empty";
    empty.textContent = "这次没有生成可显示的回答。";
    content.append(empty);
  }
  article.append(content);

  const analysisTrace = buildAnalysisTrace(trace);
  if (analysisTrace) {
    article.append(analysisTrace);
  }

  const technicalDetails = buildTechnicalDetails(citations, evidenceIds);
  if (technicalDetails) {
    article.append(technicalDetails);
  }

  container.append(article);
}

function buildAnalysisTrace(trace) {
  const steps = Array.isArray(trace) ? trace.filter(step => step?.title) : [];
  if (!steps.length) {
    return null;
  }

  const details = document.createElement("details");
  details.className = "analysis-trace-details";
  const summary = document.createElement("summary");
  const label = document.createElement("span");
  label.textContent = "查看 AI 分析过程";
  const count = document.createElement("span");
  count.className = "analysis-trace-count";
  count.textContent = `${steps.length} 步`;
  summary.append(label, count);
  details.append(summary);

  const list = document.createElement("ol");
  list.className = "analysis-trace-list";
  for (const step of steps) {
    const item = document.createElement("li");
    const status = ["completed", "failed", "fallback", "requested"].includes(step.status)
      ? step.status
      : "completed";
    item.className = `analysis-trace-step trace-${status}`;

    const marker = document.createElement("span");
    marker.className = "analysis-trace-marker";
    marker.textContent = String(step.sequence || list.children.length + 1);

    const body = document.createElement("div");
    body.className = "analysis-trace-body";
    const heading = document.createElement("div");
    heading.className = "analysis-trace-heading";
    const title = document.createElement("strong");
    title.textContent = step.title;
    heading.append(title);
    if (step.toolName) {
      const tool = document.createElement("code");
      tool.textContent = step.toolName;
      heading.append(tool);
    }

    const description = document.createElement("p");
    description.textContent = step.summary || "已完成。";
    body.append(heading, description);
    if (Number.isFinite(step.durationMs) && step.durationMs > 0) {
      const duration = document.createElement("span");
      duration.className = "analysis-trace-duration";
      duration.textContent = step.durationMs >= 1000
        ? `${(step.durationMs / 1000).toFixed(1)} 秒`
        : `${step.durationMs} 毫秒`;
      body.append(duration);
    }

    item.append(marker, body);
    list.append(item);
  }
  details.append(list);
  return details;
}

function renderAnswerMarkdown(container, value) {
  const lines = String(value || "").split("\n");
  let paragraphLines = [];
  let list = null;
  let rendered = false;

  const flushParagraph = () => {
    const text = paragraphLines.join(" ").trim();
    paragraphLines = [];
    if (!text) {
      return;
    }
    const paragraph = document.createElement("p");
    appendSafeInlineMarkdown(paragraph, text);
    container.append(paragraph);
    rendered = true;
  };

  for (const rawLine of lines) {
    const line = rawLine.trim();
    if (!line) {
      flushParagraph();
      list = null;
      continue;
    }

    const heading = line.match(/^#{1,6}\s+(.+)$/);
    if (heading) {
      flushParagraph();
      list = null;
      const element = document.createElement("h3");
      appendSafeInlineMarkdown(element, heading[1].trim());
      container.append(element);
      rendered = true;
      continue;
    }

    const unordered = line.match(/^[-+*]\s+(.+)$/);
    const ordered = line.match(/^\d+[.)、]\s+(.+)$/);
    if (unordered || ordered) {
      flushParagraph();
      const listTag = ordered ? "ol" : "ul";
      if (!list || list.tagName.toLowerCase() !== listTag) {
        list = document.createElement(listTag);
        container.append(list);
      }
      const item = document.createElement("li");
      appendSafeInlineMarkdown(item, cleanBlockMarkdown((unordered || ordered)[1]));
      list.append(item);
      rendered = true;
      continue;
    }

    list = null;
    paragraphLines.push(cleanBlockMarkdown(line));
  }

  flushParagraph();
  return rendered;
}

function cleanBlockMarkdown(value) {
  return String(value || "")
    .replace(/^\s*>\s?/, "")
    .replace(/^#{1,6}\s+/, "")
    .trim();
}

function removeCitationMarkers(value) {
  return String(value || "")
    .replace(/【证据\s*[:：]\s*[^\u3011\]]+[】\]]/g, "")
    .replace(/\[\s*证据\s*[:：]\s*[^\]]+\]/g, "")
    .replace(/\s+([，。；：！？,.!?:;])/g, "$1")
    .trim();
}

function extractEvidenceIds(value) {
  const ids = [];
  const pattern = /(?:【证据\s*[:：]\s*([^\u3011\]]+)[】\]]|\[\s*证据\s*[:：]\s*([^\]]+)\])/g;
  for (const match of String(value || "").matchAll(pattern)) {
    const id = (match[1] || match[2] || "").trim();
    if (id && !ids.includes(id)) {
      ids.push(id);
    }
  }
  return ids;
}

function appendSafeInlineMarkdown(container, value) {
  const source = String(value || "");
  const tokenPattern = /(\*\*[^*\n]+\*\*|__[^_\n]+__|`[^`\n]+`|\[[^\]\n]+\]\([^)\n]+\))/g;
  let position = 0;

  for (const match of source.matchAll(tokenPattern)) {
    if (match.index > position) {
      container.append(document.createTextNode(source.slice(position, match.index)));
    }

    const token = match[0];
    if (token.startsWith("**") || token.startsWith("__")) {
      const strong = document.createElement("strong");
      strong.textContent = token.slice(2, -2);
      container.append(strong);
    } else if (token.startsWith("`")) {
      const code = document.createElement("code");
      code.textContent = token.slice(1, -1);
      container.append(code);
    } else {
      const link = token.match(/^\[([^\]]+)\]\(([^)]+)\)$/);
      const label = document.createElement("span");
      label.className = "answer-link-label";
      label.textContent = link ? link[1] : token;
      if (link?.[2]) {
        label.title = link[2];
      }
      container.append(label);
    }
    position = match.index + token.length;
  }

  if (position < source.length) {
    container.append(document.createTextNode(source.slice(position)));
  }
}

function buildTechnicalDetails(citations, inlineEvidenceIds) {
  const normalizedCitations = Array.isArray(citations) ? citations.filter(Boolean) : [];
  const structuredIds = new Set(normalizedCitations.map((citation) => citation.nodeId).filter(Boolean));
  const remainingIds = (inlineEvidenceIds || []).filter((id) => !structuredIds.has(id));
  const total = normalizedCitations.length + remainingIds.length;
  if (total === 0) {
    return null;
  }

  const details = document.createElement("details");
  details.className = "technical-details";
  const summary = document.createElement("summary");
  const summaryLabel = document.createElement("span");
  summaryLabel.textContent = "查看技术依据";
  const count = document.createElement("span");
  count.className = "technical-count";
  count.textContent = `${total} 条`;
  summary.append(summaryLabel, count);
  details.append(summary);

  const citationList = document.createElement("ol");
  citationList.className = "citation-list";
  for (const citation of normalizedCitations) {
    citationList.append(buildCitationItem(citation));
  }
  for (const nodeId of remainingIds) {
    citationList.append(buildCitationItem({ nodeId, label: "回答中引用的证据" }));
  }
  details.append(citationList);
  return details;
}

function buildCitationItem(citation) {
  const item = document.createElement("li");
  const source = document.createElement("strong");
  source.textContent = citation.label || citation.artifactName || "技术证据";
  item.append(source);

  if (citation.location) {
    const location = document.createElement("span");
    location.textContent = citation.location;
    item.append(location);
  }

  if (citation.nodeId) {
    const identifier = document.createElement("code");
    identifier.textContent = citation.nodeId;
    item.append(identifier);
  }

  return item;
}

async function saveSettings(event) {
  event.preventDefault();
  const settings = readSettingsForm();

  try {
    const saved = await request("SAVE_SETTINGS", { settings });
    fillSettings(saved);
    closeSettings();
    showToast("连接设置已保存。");
    renderContext(state.context);
  } catch (error) {
    showToast(error.message, "bad");
  }
}

async function testServer() {
  ui.testServerButton.disabled = true;
  ui.testServerButton.textContent = "检测中…";

  try {
    const result = await request("TEST_SERVER", { serverUrl: ui.serverUrl.value });
    showToast(result.message || "分析服务器可以访问。");
  } catch (error) {
    showToast(error.message, "bad");
  } finally {
    ui.testServerButton.disabled = false;
    ui.testServerButton.textContent = "测试连接";
  }
}

function readSettingsForm() {
  return {
    serverUrl: ui.serverUrl.value.trim(),
    apiVersion: ui.apiVersion.value,
    includeApplicationRibbon: ui.includeApplicationRibbon.checked,
    includePluginAssemblies: ui.includePluginAssemblies.checked,
    allowCrmDataAccess: ui.allowCrmDataAccess.checked
  };
}

function fillSettings(settings) {
  ui.serverUrl.value = settings?.serverUrl || "http://localhost:5165";
  ui.apiVersion.value = settings?.apiVersion || "auto";
  ui.includeApplicationRibbon.checked = Boolean(settings?.includeApplicationRibbon);
  ui.includePluginAssemblies.checked = Boolean(settings?.includePluginAssemblies);
  ui.allowCrmDataAccess.checked = Boolean(settings?.allowCrmDataAccess);
  renderContext(state.context);
}

function restoreSession(session) {
  state.context = session.context || null;
  state.run = session.snapshotId ? {
    snapshotId: session.snapshotId,
    jobId: session.jobId,
    status: session.status,
    context: session.context
  } : null;
  state.warnings = session.warnings || [];

  if (state.context) {
    renderContext(state.context);
  }
  setArtifactCount(session.artifactCount || 0);
  setWarnings(state.warnings);

  if (!state.run) {
    return;
  }

  ui.collectButton.querySelector("span").textContent = "重新采集当前逻辑";
  for (const step of ui.traceSteps) {
    updateTraceStep(step.dataset.step, "done", "已完成");
  }

  if (isCompleteStatus(state.run.status)) {
    void analysisCompleted();
  } else if (state.run.jobId && !isFailureStatus(state.run.status)) {
    setConnection("继续查询分析进度", "working");
    void pollJob(state.run.jobId, ++state.pollToken);
  }
}

function setChatReady(ready) {
  ui.chatReady.dataset.ready = String(ready);
  ui.chatReady.setAttribute("aria-label", ready ? "问答已准备好" : "问答尚未准备好");
  ui.questionInput.disabled = !ready;
  ui.sendButton.disabled = !ready;
  ui.recordButton.disabled = !ready && !state.recording;
  ui.questionInput.placeholder = ready ? "例如：点击批准后会发生什么？" : "先完成上方分析…";
}

function setConnection(label, tone) {
  ui.connectionBadge.textContent = label;
  ui.connectionBadge.dataset.tone = tone;
}

function resetTrace() {
  for (const step of ui.traceSteps) {
    step.dataset.state = "idle";
    step.querySelector(".step-state").textContent = "待开始";
  }
}

function updateTraceStep(stepName, stepState, label) {
  const step = ui.traceSteps.find((item) => item.dataset.step === stepName);
  if (!step) {
    return;
  }
  step.dataset.state = stepState;
  step.querySelector(".step-state").textContent = label || stateLabel(stepState);
}

function setArtifactCount(count) {
  const safeCount = Number.isFinite(Number(count)) ? Number(count) : 0;
  ui.artifactCount.textContent = `${safeCount} 份证据`;
}

function setWarnings(warnings) {
  state.warnings = unique((warnings || []).filter(Boolean).map(String));
  ui.warningPanel.hidden = state.warnings.length === 0;
  ui.warningCount.textContent = String(state.warnings.length);
  ui.warningList.replaceChildren();

  for (const warning of state.warnings) {
    const item = document.createElement("li");
    item.textContent = warning;
    ui.warningList.append(item);
  }
}

function showToast(message, tone = "good") {
  window.clearTimeout(state.toastTimer);
  ui.toast.textContent = message;
  ui.toast.dataset.tone = tone;
  ui.toast.hidden = false;
  state.toastTimer = window.setTimeout(() => {
    ui.toast.hidden = true;
  }, 3600);
}

function shortId(value) {
  if (!value) {
    return "";
  }
  const normalized = String(value).replace(/[{}]/g, "");
  return normalized.length > 12 ? `${normalized.slice(0, 8)}…${normalized.slice(-4)}` : normalized;
}

function stateLabel(value) {
  return ({ idle: "待开始", active: "读取中", done: "已完成", warning: "有缺口" })[value] || value;
}

function statusLabel(value) {
  const normalized = String(value || "").toLowerCase();
  if (normalized.includes("queue")) return "等待分析";
  if (normalized.includes("run") || normalized.includes("process")) return "分析处理中";
  return `分析：${value}`;
}

function isCompleteStatus(value) {
  return ["complete", "completed", "succeeded", "success", "done"].includes(String(value || "").toLowerCase());
}

function isFailureStatus(value) {
  return ["failed", "failure", "error", "cancelled", "canceled"].includes(String(value || "").toLowerCase());
}

function unique(items) {
  return [...new Set(items)];
}

function sameLogicScope(left, right) {
  if (!left || !right) return false;
  const normalize = (value) => String(value || "").toLowerCase().replace(/[{}]/g, "");
  return normalize(left.organizationUrl) === normalize(right.organizationUrl)
    && normalize(left.entityName) === normalize(right.entityName)
    && normalize(left.formId) === normalize(right.formId);
}

function shouldAutoCollect(context) {
  if (!context || state.collecting) {
    return false;
  }
  if (!context.entityName && !context.formId) {
    return false;
  }
  if (!state.run || !sameLogicScope(state.run.context, context)) {
    return true;
  }
  if (isFailureStatus(state.run.status)) {
    return true;
  }
  return !state.run.snapshotId && !state.run.jobId;
}

function delay(milliseconds) {
  return new Promise((resolve) => window.setTimeout(resolve, milliseconds));
}
