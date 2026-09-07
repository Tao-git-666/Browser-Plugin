"use strict";

if (typeof importScripts === "function") importScripts("code-library.js");

const DEFAULT_SETTINGS = Object.freeze({
  serverUrl: "http://localhost:5165",
  apiVersion: "auto",
  includeApplicationRibbon: false,
  includePluginAssemblies: true,
  allowCrmDataAccess: false,
  settingsSchemaVersion: 3
});

const ARTIFACT_KIND = Object.freeze({
  formXml: "formXml",
  javaScript: "javaScript",
  ribbonXml: "ribbonXml",
  pluginCatalog: "pluginCatalog",
  pluginAssembly: "pluginAssembly",
  entityMetadata: "entityMetadata",
  customPageCatalog: "customPageCatalog"
});

const COLLECTION_LIMITS = Object.freeze({
  maxArtifacts: 120,
  maxArtifactBytes: 24 * 1024 * 1024,
  maxSnapshotBytes: 60 * 1024 * 1024,
  maxPluginAssemblies: 12,
  maxPluginAssemblyBytes: 24 * 1024 * 1024,
  maxAllPluginAssemblyBytes: 30 * 1024 * 1024
});

const PLUGIN_QUERY_LIMITS = Object.freeze({
  maxEntityFilters: 500,
  maxEntitySteps: 1000,
  maxEventHandlers: 500,
  maxMessages: 250,
  filterIdsPerStepQuery: 12,
  exactReadConcurrency: 6
});

const RUNTIME_QUERY_SCRIPT_ID = "crm-logic-lens-runtime-query-hook";

chrome.runtime.onInstalled.addListener(() => {
  void initializeExtension();
});

chrome.runtime.onStartup.addListener(() => {
  void configureSidePanel();
});

chrome.webNavigation?.onDOMContentLoaded?.addListener((details) => {
  void attachRuntimeRecordingFrame(details);
});

void configureSidePanel();

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.type === "COLLECTION_PROGRESS" || message?.type === "CHAT_PROGRESS") {
    return false;
  }
  handleMessage(message)
    .then((data) => sendResponse({ ok: true, data }))
    .catch((error) => sendResponse({ ok: false, error: friendlyError(error) }));
  return true;
});

async function initializeExtension() {
  const stored = await chrome.storage.local.get("settings");
  const previous = stored.settings || {};
  const legacyUntouchedDefaults = previous.settingsSchemaVersion == null &&
    previous.includePluginAssemblies === false &&
    (!previous.serverUrl || previous.serverUrl === DEFAULT_SETTINGS.serverUrl) &&
    (!previous.apiVersion || previous.apiVersion === DEFAULT_SETTINGS.apiVersion) &&
    !previous.includeApplicationRibbon;
  const settings = {
    ...DEFAULT_SETTINGS,
    ...previous,
    includePluginAssemblies: legacyUntouchedDefaults ? true :
      (previous.includePluginAssemblies ?? DEFAULT_SETTINGS.includePluginAssemblies),
    settingsSchemaVersion: DEFAULT_SETTINGS.settingsSchemaVersion
  };
  await chrome.storage.local.set({ settings });
  await configureSidePanel();
}

async function configureSidePanel() {
  try {
    await chrome.sidePanel.setPanelBehavior({ openPanelOnActionClick: true });
  } catch {
    // Older Edge builds can still open the declared side panel from extension UI.
  }
}

async function attachRuntimeRecordingFrame(details) {
  try {
    const stored = await chrome.storage.session.get("runtimeRecordingState");
    const state = stored.runtimeRecordingState;
    if (!state?.active || Number(state.tabId) !== Number(details?.tabId)) return;
    const organizationUrl = state.organizationUrl || state.context?.organizationUrl;
    if (!organizationUrl || new URL(details.url).origin !== new URL(organizationUrl).origin) return;
    await chrome.scripting.executeScript({
      target: { tabId: details.tabId, frameIds: [details.frameId] },
      world: "MAIN",
      func: installRuntimeDiagnosticsInPage,
      args: [organizationUrl]
    });
    await chrome.scripting.executeScript({
      target: { tabId: details.tabId, frameIds: [details.frameId] },
      world: "MAIN",
      func: startRuntimeRecordingInPage,
      args: [organizationUrl, true, state.startedAt]
    });
  } catch {
    // Some CRM system frames are intentionally inaccessible; other frames remain recordable.
  }
}

async function handleMessage(message) {
  switch (message?.type) {
    case "GET_SETTINGS":
      return getSettings();
    case "SAVE_SETTINGS":
      return saveSettings(message.settings);
    case "GET_SESSION":
      return getSession();
    case "GET_CONTEXT":
      return getCurrentContext();
    case "START_RUNTIME_RECORDING":
      return startRuntimeRecording();
    case "GET_RUNTIME_RECORDING_STATUS":
      return getRuntimeRecordingStatus();
    case "STOP_RUNTIME_RECORDING":
      return stopRuntimeRecording();
    case "COLLECT_AND_UPLOAD":
      return collectAndUpload();
    case "PREPARE_CODE_LIBRARY":
      return prepareCodeLibrary();
    case "SYNC_CODE_LIBRARY_BATCH":
      return syncCodeLibraryBatch(message.index);
    case "TEST_SERVER":
      return testServer(message.serverUrl);
    case "CHECK_JOB":
      return checkJob(message.jobId);
    case "GET_EVIDENCE":
      return getEvidence(message.snapshotId);
    case "ASK_QUESTION":
      return askQuestion(message.snapshotId, message.question, message.requestId);
    default:
      throw new Error("不支持的扩展操作。请重新加载扩展后再试。");
  }
}

async function getSettings() {
  const stored = await chrome.storage.local.get("settings");
  return { ...DEFAULT_SETTINGS, ...(stored.settings || {}) };
}

async function saveSettings(candidate) {
  const settings = {
    serverUrl: normalizeServerUrl(candidate?.serverUrl),
    apiVersion: normalizeApiVersionSetting(candidate?.apiVersion),
    includeApplicationRibbon: Boolean(candidate?.includeApplicationRibbon),
    includePluginAssemblies: Boolean(candidate?.includePluginAssemblies),
    allowCrmDataAccess: Boolean(candidate?.allowCrmDataAccess),
    settingsSchemaVersion: DEFAULT_SETTINGS.settingsSchemaVersion
  };
  await chrome.storage.local.set({ settings });
  if (settings.allowCrmDataAccess) {
    try {
      const tab = await getActiveHttpTab();
      const located = await locateD365Context(tab.id);
      await installRuntimeDiagnostics(located);
    } catch {
      // Saving settings must still succeed when the active tab is not a CRM page.
    }
  }
  return settings;
}

async function getSession() {
  try {
    const stored = await chrome.storage.session.get("lastRun");
    return stored.lastRun || null;
  } catch {
    return null;
  }
}

async function saveSession(lastRun) {
  try {
    await chrome.storage.session.set({ lastRun });
  } catch {
    // A browser restart may end the session store; collection itself is unaffected.
  }
}

async function getCurrentContext() {
  const tab = await getActiveHttpTab();
  const located = await locateD365Context(tab.id);
  const settings = await getSettings();
  const context = {
    ...located.context,
    apiVersion: settings.apiVersion === "auto" ? null : settings.apiVersion
  };
  return { context, warnings: located.warnings };
}

async function collectAndUpload() {
  const warnings = [];
  const artifacts = [];
  const settings = await getSettings();
  const limits = await resolveCollectionLimits(settings.serverUrl, warnings);
  const tab = await getActiveHttpTab();

  notifyProgress("context", "active", "正在识别");
  const located = await locateD365Context(tab.id);
  warnings.push(...located.warnings);
  if (settings.allowCrmDataAccess) {
    try {
      await installRuntimeDiagnostics(located);
    } catch (error) {
      warnings.push(`运行时失败诊断未启用：${friendlyError(error)}`);
    }
  }
  const apiVersion = await chooseApiVersion(located, settings, warnings);
  const context = { ...located.context, apiVersion };
  notifyProgress("context", warnings.length ? "warning" : "done", context.entityName || "已定位", artifacts.length, warnings);

  let formXml = "";
  notifyProgress("form", "active", "读取中", artifacts.length, warnings);
  const beforeFormWarnings = warnings.length;
  const formResult = await collectForm(located, apiVersion, warnings);
  if (formResult) {
    artifacts.push(formResult.artifact);
    formXml = formResult.xml;
  }
  notifyProgress("form", warnings.length > beforeFormWarnings ? "warning" : "done", formResult ? "已取得" : "未取得", artifacts.length, warnings);

  notifyProgress("ribbon", "active", "读取中", artifacts.length, warnings);
  const beforeRibbonWarnings = warnings.length;
  const ribbons = await collectRibbons(located, apiVersion, settings.includeApplicationRibbon, warnings);
  notifyProgress("ribbon", warnings.length > beforeRibbonWarnings ? "warning" : "done", `${ribbons.length} 份待筛选`, artifacts.length, warnings);

  notifyProgress("scripts", "active", "解析引用", artifacts.length, warnings);
  const beforeScriptWarnings = warnings.length;
  const resourceNames = extractJavaScriptReferences([formXml, ...ribbons.map((item) => item.xml)]);
  const scripts = await collectWebResources(located, apiVersion, resourceNames, warnings);
  const customScriptNames = new Set(scripts.map((script) => normalizeWebResourceName(script.name)));
  let customRibbonCount = 0;
  for (const ribbon of ribbons) {
    const customXml = filterRibbonXmlForCustomJavaScript(ribbon.xml, customScriptNames);
    if (!customXml) continue;
    artifacts.push({
      ...ribbon.artifact,
      name: ribbon.artifact.name.replace(/\.ribbon\.xml$/i, ".custom.ribbon.xml"),
      contentBase64: utf8ToBase64(customXml)
    });
    customRibbonCount += 1;
  }
  if (ribbons.length && !customRibbonCount) {
    warnings.push("当前命令栏中没有找到绑定自定义 JavaScript 的按钮，已排除微软原生 Ribbon 定义。");
  }
  artifacts.push(...scripts);
  const pageResult = await collectOpenedCustomPages(located, apiVersion, scripts, warnings);
  artifacts.push(...pageResult.artifacts);
  const allScripts = [...scripts, ...pageResult.scripts];
  const relevantCustomApiCalls = extractRelevantCustomApiCalls(allScripts);
  const pageLabel = pageResult.pages.length ? `；${pageResult.pages.length} 个自定义页面` : "";
  notifyProgress("scripts", warnings.length > beforeScriptWarnings ? "warning" : "done", `${allScripts.length} 个自定义脚本${pageLabel}`, artifacts.length, warnings);

  notifyProgress("plugins", "active", "建立目录", artifacts.length, warnings);
  const beforePluginWarnings = warnings.length;
  const [entityCatalogResult, customApiCatalogResult, metadataResult] = await Promise.all([
    collectPluginCatalog(located, apiVersion, warnings),
    collectRelevantCustomApiCatalog(located, apiVersion, relevantCustomApiCalls, warnings),
    collectEntityMetadata(located, apiVersion, warnings)
  ]);
  const catalogResult = mergePluginCatalogResults(
    located,
    entityCatalogResult,
    customApiCatalogResult);
  if (catalogResult?.artifact) artifacts.push(catalogResult.artifact);
  if (metadataResult?.artifact) artifacts.push(metadataResult.artifact);

  let pluginAssemblyCount = 0;
  if (settings.includePluginAssemblies && catalogResult?.catalog) {
    const assemblyArtifacts = await collectPluginAssemblies(
      located,
      apiVersion,
      catalogResult.catalog,
      metadataResult?.objectTypeCode,
      {
        remainingArtifacts: Math.max(0, limits.maxArtifacts - artifacts.length),
        remainingBytes: Math.max(0, limits.maxSnapshotBytes - artifactBytes(artifacts)),
        maxArtifactBytes: limits.maxArtifactBytes
      },
      warnings);
    artifacts.push(...assemblyArtifacts);
    pluginAssemblyCount = assemblyArtifacts.length;
  }

  const pluginLabel = catalogResult?.artifact
    ? (settings.includePluginAssemblies ? `目录 + ${pluginAssemblyCount} 个 DLL` : "目录已建立")
    : "目录不完整";
  notifyProgress("plugins", warnings.length > beforePluginWarnings ? "warning" : "done", pluginLabel, artifacts.length, warnings);

  const snapshot = {
    context,
    artifacts: enforceSnapshotBudget(artifacts, warnings, limits),
    capturedAt: new Date().toISOString()
  };

  artifacts.splice(0, artifacts.length, ...snapshot.artifacts);

  notifyProgress("upload", "active", "正在上传", artifacts.length, warnings);
  const receipt = await uploadSnapshot(settings.serverUrl, context.organizationUrl, snapshot);
  const session = {
    snapshotId: receipt.snapshotId,
    jobId: receipt.jobId,
    status: receipt.status,
    artifactCount: receipt.artifactCount ?? artifacts.length,
    context,
    customApiNames: relevantCustomApiCalls.map((call) => call.name),
    warnings: uniqueStrings(warnings),
    serverUrl: settings.serverUrl,
    capturedAt: snapshot.capturedAt
  };
  await saveSession(session);
  notifyProgress("upload", "done", "已安全送达", session.artifactCount, warnings);

  return {
    context,
    receipt,
    artifactCount: artifacts.length,
    warnings: uniqueStrings(warnings)
  };
}

async function getActiveHttpTab() {
  const tabs = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
  const tab = tabs[0];
  if (!tab?.id || !/^https?:\/\//i.test(tab.url || "")) {
    throw new Error("当前标签页不是可访问的 CRM 网页。请先打开 Dynamics 365 记录窗体。");
  }
  return tab;
}

async function locateD365Context(tabId) {
  let results;
  try {
    results = await chrome.scripting.executeScript({
      target: { tabId, allFrames: true },
      world: "MAIN",
      func: readD365ContextInPage
    });
  } catch (error) {
    throw new Error(`无法读取当前页面上下文：${friendlyError(error)}`);
  }

  const candidates = results
    .filter((item) => item.result?.found && item.result?.context?.organizationUrl)
    .sort((left, right) => contextScore(right.result.context, right.frameId) - contextScore(left.result.context, left.frameId));

  if (!candidates.length) {
    throw new Error("当前页面没有检测到 Dynamics 365 的 Xrm 上下文。请打开记录主窗体后再试。");
  }

  const selected = candidates[0];
  return {
    tabId,
    frameId: selected.frameId,
    context: selected.result.context,
    warnings: selected.result.warnings || []
  };
}

async function installRuntimeDiagnostics(located) {
  await chrome.scripting.executeScript({
    target: { tabId: located.tabId, frameIds: [located.frameId] },
    world: "MAIN",
    func: installRuntimeDiagnosticsInPage,
    args: [located.context.organizationUrl]
  });
}

async function installRuntimeDiagnosticsForRecording(tabId, organizationUrl) {
  return chrome.scripting.executeScript({
    target: { tabId, allFrames: true },
    world: "MAIN",
    func: installRuntimeDiagnosticsInPage,
    args: [organizationUrl]
  });
}

async function enableEarlyRuntimeQueryHook(organizationUrl) {
  if (!chrome.scripting?.registerContentScripts) return false;
  try {
    await chrome.scripting.unregisterContentScripts({ ids: [RUNTIME_QUERY_SCRIPT_ID] });
  } catch { /* no previous registration */ }
  const organization = new URL(String(organizationUrl || ""));
  const matchOrigin = `${organization.protocol}//${organization.hostname}/*`;
  await chrome.scripting.registerContentScripts([{
    id: RUNTIME_QUERY_SCRIPT_ID,
    js: ["runtime-query-hook.js"],
    matches: [matchOrigin],
    allFrames: true,
    runAt: "document_start",
    world: "MAIN",
    persistAcrossSessions: false
  }]);
  return true;
}

function setEarlyRuntimeQueryHookStateInPage(active, clearBuffer) {
  window.__crmLogicLensEarlyQueryHookActiveV1 = Boolean(active);
  if (clearBuffer && Array.isArray(window.__crmLogicLensDataverseQueryBufferV1)) {
    window.__crmLogicLensDataverseQueryBufferV1.length = 0;
  }
}

async function disableEarlyRuntimeQueryHook(tabId = null) {
  if (Number.isInteger(tabId)) {
    try {
      await chrome.scripting.executeScript({
        target: { tabId, allFrames: true },
        world: "MAIN",
        func: setEarlyRuntimeQueryHookStateInPage,
        args: [false, false]
      });
    } catch { /* inaccessible or closed frames */ }
  }
  if (!chrome.scripting?.unregisterContentScripts) return;
  try {
    await chrome.scripting.unregisterContentScripts({ ids: [RUNTIME_QUERY_SCRIPT_ID] });
  } catch { /* already removed */ }
}

function readEarlyRuntimeQueriesInPage() {
  const entries = Array.isArray(window.__crmLogicLensDataverseQueryBufferV1)
    ? window.__crmLogicLensDataverseQueryBufferV1
    : [];
  return entries.slice(-60).map(item => ({
    kind: "dataverse-query",
    summary: String(item.summary || "Dataverse 查询").slice(0, 500),
    details: item.details == null ? null : String(item.details).slice(0, 8000),
    capturedAt: String(item.capturedAt || ""),
    method: "GET",
    path: String(item.path || "").slice(0, 1000),
    status: Number(item.status) || 0
  }));
}

function installRuntimeDiagnosticsInPage(organizationUrl) {
  const stateKey = "__crmLogicLensRuntimeDiagnosticsV1";
  const installedKey = "__crmLogicLensRuntimeDiagnosticsInstalledV1";
  if (window[installedKey]) return { installed: true };
  const entries = Array.isArray(window[stateKey]) ? window[stateKey] : [];
  window[stateKey] = entries;
  const organization = new URL(String(organizationUrl || ""), window.location.href);
  const organizationPath = organization.pathname.replace(/\/$/, "");
  const apiPrefix = `${organizationPath}/api/data/`.toLowerCase();

  const isDataverseUrl = (rawUrl) => {
    const url = new URL(String(rawUrl || ""), window.location.href);
    return url.origin === organization.origin && url.pathname.toLowerCase().startsWith(apiPrefix)
      ? url
      : null;
  };

  const sanitizeQueryValue = (name, value) => {
    let safe = String(value || "").slice(0, 12000);
    if (String(name).toLowerCase() === "fetchxml") {
      safe = safe
        .replace(/(\bvalue(?:of)?\s*=\s*)(["'])[^"']*\2/gi, "$1$2<value>$2")
        .replace(/\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/gi, "<guid>");
      return safe.slice(0, 8000);
    }
    safe = safe
      .replace(/\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/gi, "<guid>")
      .replace(/(\b(?:eq|ne|gt|ge|lt|le)\s+)(?:guid'[^']*'|'(?:''|[^'])*'|-?\d+(?:\.\d+)?)/gi, "$1<value>")
      .replace(/(['"])(?:https?:\/\/|mailto:)[^'"]+\1/gi, "$1<url>$1");
    return safe.slice(0, 8000);
  };

  const recordDataverseQueryForActiveRecording = (method, rawUrl, status, responseBody, durationMs) => {
    try {
      if (window.__crmLogicLensEarlyQueryHookInstalledV1) return;
      const recorder = window.__crmLogicLensOperationRecorderV1;
      if (!recorder?.active || !Array.isArray(recorder.events) || recorder.events.length >= 100) return;
      if (String(method || "GET").toUpperCase() !== "GET") return;
      if (recorder.events.filter(item => item?.kind === "dataverse-query").length >= 40) return;
      const url = isDataverseUrl(rawUrl);
      if (!url) return;
      const relativeApiPath = url.pathname.slice(apiPrefix.length);
      const entitySet = relativeApiPath.split(/[/(]/, 1)[0].slice(0, 256);
      const queryOptions = {};
      let remainingQueryCharacters = 6800;
      for (const [name, value] of url.searchParams.entries()) {
        const normalized = name.toLowerCase();
        if (!["$select", "$filter", "$expand", "$orderby", "$top", "fetchxml", "savedquery", "userquery"].includes(normalized)) continue;
        const safeValue = sanitizeQueryValue(normalized, value).slice(0, Math.max(0, remainingQueryCharacters));
        if (!safeValue) continue;
        queryOptions[normalized] = safeValue;
        remainingQueryCharacters -= safeValue.length;
        if (remainingQueryCharacters <= 0) break;
      }
      let resultCount = null;
      if (responseBody != null && String(responseBody).length <= 2_000_000) {
        try {
          const payload = typeof responseBody === "string" ? JSON.parse(responseBody) : responseBody;
          if (Array.isArray(payload?.value)) resultCount = payload.value.length;
          else if (payload && typeof payload === "object" && !payload.error) resultCount = 1;
        } catch { /* count is optional */ }
      }
      const numericStatus = Number(status) || 0;
      const countText = Number.isInteger(resultCount) ? `，返回 ${resultCount} 条` : "";
      recorder.events.push({
        kind: "dataverse-query",
        summary: `查询 ${entitySet || "Dataverse 数据"}${countText}`.slice(0, 500),
        details: JSON.stringify({
          entitySet: entitySet || null,
          queryOptions,
          resultCount,
          durationMs: Math.max(0, Math.round(Number(durationMs) || 0))
        }),
        capturedAt: new Date().toISOString(),
        method: "GET",
        path: url.pathname
          .replace(/\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/gi, "<guid>")
          .slice(0, 1000),
        status: numericStatus
      });
    } catch {
      // Query tracing must never affect CRM or expose raw response data.
    }
  };

  const recordFailure = (method, rawUrl, status, responseBody) => {
    try {
      const url = isDataverseUrl(rawUrl);
      if (!url) return;
      const numericStatus = Number(status);
      if (numericStatus < 400 || numericStatus > 599) return;
      const body = String(responseBody ?? "").slice(0, 8000);
      entries.push({
        method: String(method || "GET").toUpperCase().slice(0, 12),
        path: url.pathname.slice(0, 1000),
        status: numericStatus,
        responseBody: body,
        capturedAt: new Date().toISOString()
      });
      if (entries.length > 40) entries.splice(0, entries.length - 40);
    } catch {
      // Diagnostics must never affect the CRM request itself.
    }
  };

  const recordNetworkFailureForActiveRecording = (method, rawUrl, error) => {
    try {
      const recorder = window.__crmLogicLensOperationRecorderV1;
      if (!recorder?.active || !Array.isArray(recorder.events) || recorder.events.length >= 80) return;
      const url = new URL(String(rawUrl || ""), window.location.href);
      if (url.origin !== organization.origin || !url.pathname.toLowerCase().startsWith(apiPrefix)) return;
      const safeMethod = String(method || "GET").toUpperCase().slice(0, 12);
      const safePath = url.pathname.slice(0, 1000);
      const message = String(error?.message || error || "网络请求未取得响应").slice(0, 8000);
      recorder.events.push({
        kind: "http-error",
        summary: `${safeMethod} ${safePath} 未取得 HTTP 响应`.slice(0, 500),
        details: message,
        capturedAt: new Date().toISOString(),
        method: safeMethod,
        path: safePath,
        status: 0
      });
    } catch {
      // Recording must never alter the request behavior.
    }
  };

  const originalFetch = window.fetch;
  if (typeof originalFetch === "function") {
    window.fetch = async function (...args) {
      const startedAt = performance.now();
      let response;
      try {
        response = await Reflect.apply(originalFetch, this, args);
      } catch (error) {
        const input = args[0];
        const method = args[1]?.method || input?.method || "GET";
        const url = typeof input === "string" ? input : input?.url;
        recordNetworkFailureForActiveRecording(method, url, error);
        throw error;
      }
      const input = args[0];
      const method = args[1]?.method || input?.method || "GET";
      const url = typeof input === "string" ? input : input?.url;
      const shouldReadBody = Number(response.headers?.get?.("content-length") || 0) <= 2_000_000;
      if (!response.ok || (String(method).toUpperCase() === "GET" && isDataverseUrl(url))) {
        const consume = shouldReadBody ? response.clone().text() : Promise.resolve("");
        void consume
          .then((body) => {
            if (!response.ok) recordFailure(method, url, response.status, body);
            recordDataverseQueryForActiveRecording(method, url, response.status, body, performance.now() - startedAt);
          })
          .catch(() => {
            if (!response.ok) recordFailure(method, url, response.status, "");
            recordDataverseQueryForActiveRecording(method, url, response.status, null, performance.now() - startedAt);
          });
      }
      return response;
    };
  }

  const xhr = window.XMLHttpRequest?.prototype;
  if (xhr) {
    const originalOpen = xhr.open;
    const originalSend = xhr.send;
    const requests = new WeakMap();
    xhr.open = function (method, url, ...rest) {
      requests.set(this, { method, url, startedAt: performance.now() });
      return Reflect.apply(originalOpen, this, [method, url, ...rest]);
    };
    xhr.send = function (...args) {
      this.addEventListener("loadend", () => {
        const request = requests.get(this);
        if (!request) return;
        if (this.status === 0) {
          recordNetworkFailureForActiveRecording(request.method, request.url, "XHR 请求未取得 HTTP 响应");
          return;
        }
        let body = "";
        try {
          body = this.responseType === "json"
            ? JSON.stringify(this.response)
            : this.responseType === "" || this.responseType === "text"
              ? this.responseText
              : "";
        } catch {
          body = "";
        }
        if (this.status >= 400) recordFailure(request.method, request.url, this.status, body);
        recordDataverseQueryForActiveRecording(
          request.method,
          request.url,
          this.status,
          body,
          performance.now() - request.startedAt);
      }, { once: true });
      return Reflect.apply(originalSend, this, args);
    };
  }
  window[installedKey] = true;
  return { installed: true };
}

async function readRuntimeDiagnostics(session) {
  const tab = await getActiveHttpTab();
  const located = await locateD365Context(tab.id);
  if (session?.context?.organizationUrl &&
      String(session.context.organizationUrl).replace(/\/$/, "").toLowerCase() !==
        String(located.context.organizationUrl).replace(/\/$/, "").toLowerCase()) {
    return [];
  }
  const results = await chrome.scripting.executeScript({
    target: { tabId: located.tabId, frameIds: [located.frameId] },
    world: "MAIN",
    func: readRuntimeDiagnosticsInPage,
    args: [session?.customApiNames || []]
  });
  return Array.isArray(results?.[0]?.result) ? results[0].result : [];
}

function readRuntimeDiagnosticsInPage(customApiNames) {
  const entries = Array.isArray(window.__crmLogicLensRuntimeDiagnosticsV1)
    ? window.__crmLogicLensRuntimeDiagnosticsV1
    : [];
  const names = (customApiNames || [])
    .map((value) => String(value).trim().toLowerCase())
    .filter(Boolean);
  return entries
    .filter((item) => {
      if (!names.length) return true;
      const searchable = `${item.path || ""}\n${item.responseBody || ""}`.toLowerCase();
      return names.some((name) => searchable.includes(name));
    })
    .slice(-10)
    .map((item) => ({
      method: String(item.method || "GET").slice(0, 12),
      path: String(item.path || "").slice(0, 1000),
      status: Number(item.status),
      responseBody: String(item.responseBody || "").slice(0, 8000),
      capturedAt: String(item.capturedAt || "")
    }));
}

async function startRuntimeRecording() {
  const tab = await getActiveHttpTab();
  const located = await locateD365Context(tab.id);
  const session = await getSession();
  if (!session?.snapshotId || !sameRecordingScope(located.context, session.context)) {
    throw new Error("当前 CRM 窗体与已分析的窗体不一致，请先重新采集当前逻辑。 ");
  }
  try {
    await enableEarlyRuntimeQueryHook(located.context.organizationUrl);
    await chrome.scripting.executeScript({
      target: { tabId: located.tabId, allFrames: true },
      world: "MAIN",
      func: setEarlyRuntimeQueryHookStateInPage,
      args: [true, true]
    });
  } catch {
    // Existing frames and the navigation listener still provide best-effort tracing.
  }
  const startedAt = new Date().toISOString();
  let injected;
  try {
    await installRuntimeDiagnosticsForRecording(located.tabId, located.context.organizationUrl);
    injected = await chrome.scripting.executeScript({
      target: { tabId: located.tabId, allFrames: true },
      world: "MAIN",
      func: startRuntimeRecordingInPage,
      args: [located.context.organizationUrl, false, startedAt]
    });
  } catch (error) {
    await disableEarlyRuntimeQueryHook(located.tabId);
    throw error;
  }
  const frameResults = (injected || []).map(item => item?.result).filter(item => item?.active);
  const result = frameResults[0];
  if (!result?.active) {
    await disableEarlyRuntimeQueryHook(located.tabId);
    throw new Error(result?.error || "CRM 页面未能启动故障录制。 ");
  }
  await chrome.storage.session.set({
    runtimeRecordingState: {
      active: true,
      startedAt,
      tabId: located.tabId,
      organizationUrl: located.context.organizationUrl,
      snapshotId: session?.snapshotId || null,
      context: located.context
    },
    runtimeRecording: null
  });
  return {
    active: true,
    startedAt,
    eventCount: frameResults.reduce((total, item) => total + (Number(item.eventCount) || 0), 0),
    frameCount: frameResults.length
  };
}

async function getRuntimeRecordingStatus() {
  const stored = await chrome.storage.session.get(["runtimeRecordingState", "runtimeRecording"]);
  const state = stored.runtimeRecordingState;
  if (!state?.active) {
    return {
      active: false,
      eventCount: stored.runtimeRecording?.events?.length || 0,
      errorCount: stored.runtimeRecording?.errorCount || 0
    };
  }
  try {
    await installRuntimeDiagnosticsForRecording(state.tabId, state.organizationUrl || state.context?.organizationUrl);
    const injected = await chrome.scripting.executeScript({
      target: { tabId: state.tabId, allFrames: true },
      world: "MAIN",
      func: startRuntimeRecordingInPage,
      args: [state.organizationUrl || state.context?.organizationUrl, true, state.startedAt]
    });
    const frameResults = (injected || []).map(item => item?.result).filter(item => item?.active);
    return {
      active: true,
      startedAt: state.startedAt,
      eventCount: frameResults.reduce((total, item) => total + (Number(item.eventCount) || 0), 0),
      frameCount: frameResults.length
    };
  } catch {
    return { active: true, startedAt: state.startedAt, eventCount: 0, detached: true };
  }
}

async function stopRuntimeRecording() {
  const stored = await chrome.storage.session.get("runtimeRecordingState");
  const state = stored.runtimeRecordingState;
  if (!state?.active) {
    throw new Error("当前没有正在进行的故障录制。 ");
  }
  let result;
  try {
    const injected = await chrome.scripting.executeScript({
      target: { tabId: state.tabId, allFrames: true },
      world: "MAIN",
      func: stopRuntimeRecordingInPage
    });
    const frameResults = (injected || []).map(item => item?.result).filter(item => item?.stoppedAt && Array.isArray(item.events));
    result = frameResults.length
      ? {
          startedAt: state.startedAt,
          stoppedAt: frameResults.map(item => item.stoppedAt).sort().at(-1),
          events: frameResults.flatMap(item => item.events)
        }
      : null;
  } catch (error) {
    await disableEarlyRuntimeQueryHook(state.tabId);
    await chrome.storage.session.set({ runtimeRecordingState: { active: false } });
    throw new Error(`无法停止当前页面的故障录制：${friendlyError(error)}`);
  }
  if (!result?.stoppedAt || !Array.isArray(result.events)) {
    await disableEarlyRuntimeQueryHook(state.tabId);
    await chrome.storage.session.set({ runtimeRecordingState: { active: false } });
    throw new Error(result?.error || "CRM 页面没有返回录制结果。 ");
  }

  let diagnostics = [];
  let earlyQueries = [];
  try {
    diagnostics = await chrome.scripting.executeScript({
      target: { tabId: state.tabId, allFrames: true },
      world: "MAIN",
      func: readRuntimeDiagnosticsInPage,
      args: [[]]
    });
  } catch {
    // Page-level events are still useful if the auxiliary HTTP buffer cannot be read.
  }
  try {
    earlyQueries = await chrome.scripting.executeScript({
      target: { tabId: state.tabId, allFrames: true },
      world: "MAIN",
      func: readEarlyRuntimeQueriesInPage
    });
  } catch {
    // A newly closed dialog may no longer be readable; keep the remaining evidence.
  }
  await disableEarlyRuntimeQueryHook(state.tabId);
  const startedMs = Date.parse(result.startedAt || state.startedAt) || 0;
  const stoppedMs = Date.parse(result.stoppedAt) || Date.now();
  const httpEvents = (diagnostics || []).flatMap(item => Array.isArray(item?.result) ? item.result : [])
    .filter(item => {
      const captured = Date.parse(item.capturedAt) || 0;
      return captured >= startedMs && captured <= stoppedMs + 1000;
    })
    .map(item => ({
      kind: "http-error",
      summary: `${String(item.method || "GET").toUpperCase()} ${String(item.path || "")} 返回 ${Number(item.status)}`.slice(0, 500),
      details: String(item.responseBody || "").slice(0, 8000),
      capturedAt: item.capturedAt,
      method: String(item.method || "GET").slice(0, 12),
      path: String(item.path || "").slice(0, 1000),
      status: Number(item.status)
    }));
  const earlyQueryEvents = (earlyQueries || []).flatMap(item => Array.isArray(item?.result) ? item.result : [])
    .filter(item => {
      const captured = Date.parse(item.capturedAt) || 0;
      return captured >= startedMs && captured <= stoppedMs + 1000;
    });
  const uniqueEvents = new Map();
  for (const item of [...result.events, ...httpEvents, ...earlyQueryEvents]) {
    const signature = [item.kind, item.method || "", item.path || "", item.status ?? "", item.summary, item.details || ""].join("\n");
    if (!uniqueEvents.has(signature)) uniqueEvents.set(signature, item);
  }
  const events = [...uniqueEvents.values()]
    .sort((left, right) => (Date.parse(left.capturedAt) || 0) - (Date.parse(right.capturedAt) || 0))
    .slice(0, 100)
    .map((item, index) => ({ ...item, sequence: index + 1 }));
  const errorKinds = new Set(["javascript-error", "promise-rejection", "console-error", "ui-error", "http-error"]);
  const recording = {
    active: false,
    startedAt: result.startedAt || state.startedAt,
    stoppedAt: result.stoppedAt,
    snapshotId: state.snapshotId,
    context: state.context,
    events,
    errorCount: events.filter(item => errorKinds.has(item.kind)).length
  };
  await chrome.storage.session.set({ runtimeRecording: recording, runtimeRecordingState: { active: false } });
  return recording;
}

function startRuntimeRecordingInPage(organizationUrl, preserveExisting = false, sessionStartedAt = null) {
  const stateKey = "__crmLogicLensOperationRecorderV1";
  const installedKey = "__crmLogicLensOperationRecorderInstalledV1";
  try {
    const organization = new URL(String(organizationUrl || ""), window.location.href);
    if (organization.origin !== window.location.origin) return { skipped: true };
  } catch {
    return { skipped: true };
  }
  const existing = window[stateKey];
  if (preserveExisting && existing?.active) {
    return {
      active: true,
      startedAt: existing.startedAt || sessionStartedAt,
      eventCount: Array.isArray(existing.events) ? existing.events.length : 0
    };
  }
  const now = sessionStartedAt || new Date().toISOString();
  const state = {
    active: true,
    startedAt: now,
    events: [],
    lastUiSignature: ""
  };
  window[stateKey] = state;

  const bound = (value, max) => String(value ?? "").replace(/\s+/g, " ").trim().slice(0, max);
  const safeUrl = (value) => {
    try {
      const url = new URL(String(value || ""), window.location.href);
      return `${url.origin}${url.pathname}`.slice(0, 1000);
    } catch {
      return bound(value, 1000).split(/[?#]/, 1)[0];
    }
  };
  const record = (kind, summary, details = null, extra = {}) => {
    const current = window[stateKey];
    if (!current?.active || current.events.length >= 100) return;
    const safeSummary = bound(summary, 500);
    if (!safeSummary) return;
    current.events.push({
      kind,
      summary: safeSummary,
      details: details == null ? null : bound(details, 8000),
      capturedAt: new Date().toISOString(),
      ...extra
    });
  };
  record("recording", "开始录制故障复现");

  if (!window[installedKey]) {
    document.addEventListener("click", (event) => {
      const target = event.target instanceof Element
        ? event.target.closest("button,[role='button'],a,input[type='button'],input[type='submit']")
        : null;
      if (!target) return;
      const label = bound(
        target.getAttribute("aria-label") || target.getAttribute("title") || target.textContent || target.getAttribute("name") || target.id,
        160);
      const element = bound(target.tagName, 30).toLowerCase();
      record("user-action", label ? `点击“${label}”` : `点击 ${element || "页面操作项"}`, `元素：${element || "unknown"}`);
    }, true);

    window.addEventListener("error", (event) => {
      const message = bound(event.message || event.error?.message || "JavaScript 运行错误", 500);
      const source = safeUrl(event.filename || "");
      const location = source ? `${source}:${Number(event.lineno) || 0}:${Number(event.colno) || 0}` : "";
      record("javascript-error", message, [location, event.error?.stack].filter(Boolean).join("\n"));
    }, true);

    window.addEventListener("unhandledrejection", (event) => {
      const reason = event.reason;
      const message = bound(reason?.message || reason || "未处理的 Promise 拒绝", 500);
      record("promise-rejection", message, reason?.stack || null);
    });

    const originalConsoleError = console.error;
    console.error = function (...args) {
      try {
        const details = args.map(item => item instanceof Error ? `${item.message}\n${item.stack || ""}` : String(item)).join(" ");
        record("console-error", bound(details, 500) || "console.error", details);
      } catch { /* recording must not affect CRM */ }
      return Reflect.apply(originalConsoleError, this, args);
    };

    try {
      const navigation = globalThis.Xrm?.Navigation;
      if (navigation && typeof navigation.openErrorDialog === "function") {
        const originalOpenErrorDialog = navigation.openErrorDialog;
        navigation.openErrorDialog = function (options, ...rest) {
          try {
            const message = options?.message || options?.details || "D365 错误对话框";
            record("ui-error", `D365 提示：${bound(message, 420)}`, options?.details || options?.message || null);
          } catch { /* optional evidence */ }
          return Reflect.apply(originalOpenErrorDialog, this, [options, ...rest]);
        };
      }
    } catch { /* Xrm navigation is optional */ }

    const observer = new MutationObserver((mutations) => {
      for (const mutation of mutations) {
        for (const node of mutation.addedNodes) {
          if (!(node instanceof Element)) continue;
          const candidates = [];
          if (node.matches?.("[role='alert'],[role='dialog']")) candidates.push(node);
          candidates.push(...node.querySelectorAll?.("[role='alert'],[role='dialog']") || []);
          for (const candidate of candidates.slice(0, 4)) {
            const text = bound(candidate.textContent, 4000);
            if (!text || !/(错误|失败|异常|error|failed|exception)/i.test(text)) continue;
            const current = window[stateKey];
            const signature = text.slice(0, 240);
            if (current?.lastUiSignature === signature) continue;
            if (current) current.lastUiSignature = signature;
            record("ui-error", `页面提示：${bound(text, 420)}`, text);
          }
        }
      }
    });
    if (document.documentElement) observer.observe(document.documentElement, { childList: true, subtree: true });
    window[installedKey] = true;
  }
  return { active: true, startedAt: state.startedAt, eventCount: state.events.length };
}

function getRuntimeRecordingStatusInPage() {
  const state = window.__crmLogicLensOperationRecorderV1;
  return {
    active: Boolean(state?.active),
    startedAt: state?.startedAt || null,
    eventCount: Array.isArray(state?.events) ? state.events.length : 0
  };
}

function stopRuntimeRecordingInPage() {
  const state = window.__crmLogicLensOperationRecorderV1;
  if (!state?.active) return { error: "当前页面没有正在进行的故障录制。" };
  const stoppedAt = new Date().toISOString();
  if (state.events.length < 100) {
    state.events.push({
      kind: "recording",
      summary: "停止录制并冻结故障时间线",
      details: null,
      capturedAt: stoppedAt
    });
  }
  state.active = false;
  return {
    startedAt: state.startedAt,
    stoppedAt,
    events: state.events.slice(0, 100).map(item => ({
      kind: String(item.kind || "recording").slice(0, 40),
      summary: String(item.summary || "").slice(0, 500),
      details: item.details == null ? null : String(item.details).slice(0, 8000),
      capturedAt: String(item.capturedAt || stoppedAt),
      method: item.method == null ? null : String(item.method).slice(0, 12),
      path: item.path == null ? null : String(item.path).slice(0, 1000),
      status: Number.isFinite(item.status) ? Number(item.status) : null
    }))
  };
}

function contextScore(context, frameId) {
  let score = frameId === 0 ? 2 : 0;
  if (context.entityName) score += 6;
  if (context.formId) score += 5;
  if (context.entityId) score += 2;
  if (context.formLabel) score += 1;
  return score;
}

async function chooseApiVersion(located, settings, warnings) {
  if (settings.apiVersion !== "auto") {
    return settings.apiVersion;
  }

  const candidates = [];
  const versionMatch = String(located.context.version || "").match(/^(\d+)\.(\d+)/);
  if (versionMatch) {
    candidates.push(`v${versionMatch[1]}.${versionMatch[2]}`);
  }
  candidates.push("v9.2", "v9.1", "v9.0", "v8.2");

  for (const candidate of [...new Set(candidates)]) {
    const probeUrl = `${apiRoot(located.context.organizationUrl, candidate)}/WhoAmI`;
    try {
      await crmGetJson(located, probeUrl);
      return candidate;
    } catch {
      // Probe the next known on-premises Web API version.
    }
  }

  const fallback = candidates[0] || "v9.1";
  warnings.push(`Web API 版本探测未成功，将按 ${fallback} 继续尝试各项只读查询。`);
  return fallback;
}

function readD365ContextInPage() {
  return (async () => {
    const xrm = globalThis.Xrm;
    if (!xrm) {
      return { found: false };
    }

    const warnings = [];
    let globalContext = null;
    try {
      globalContext = xrm.Utility?.getGlobalContext?.() || xrm.Page?.context || null;
    } catch {
      warnings.push("已找到 Xrm，但无法读取全局上下文。");
    }

    let pageContext = null;
    try {
      pageContext = await Promise.resolve(xrm.Utility?.getPageContext?.() || null);
    } catch {
      warnings.push("当前版本没有返回 UCI 页面上下文，已尝试经典窗体兼容方式。");
    }

    const input = pageContext?.input || pageContext || {};
    const params = new URLSearchParams(location.search);
    let hashParams = new URLSearchParams();
    const hashQuestion = location.hash.indexOf("?");
    if (hashQuestion >= 0) {
      hashParams = new URLSearchParams(location.hash.slice(hashQuestion + 1));
    }
    const getParam = (name) => params.get(name) || hashParams.get(name) || null;
    const cleanGuid = (value) => {
      if (!value) return null;
      const cleaned = String(value).trim().replace(/[{}]/g, "").toLowerCase();
      return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(cleaned) ? cleaned : null;
    };

    let organizationUrl = null;
    let organizationId = null;
    let version = null;
    try { organizationUrl = globalContext?.getClientUrl?.() || null; } catch { /* optional */ }
    try { organizationId = cleanGuid(globalContext?.organizationSettings?.organizationId); } catch { /* optional */ }
    try { version = globalContext?.getVersion?.() || null; } catch { /* optional */ }

    let entityName = input.entityName || input.entityTypeName || getParam("etn") || getParam("typename") || null;
    let entityId = cleanGuid(input.entityId || input.id || getParam("id"));
    let formId = cleanGuid(input.formId || getParam("formid"));
    let formLabel = null;

    try {
      entityName ||= xrm.Page?.data?.entity?.getEntityName?.() || null;
      entityId ||= cleanGuid(xrm.Page?.data?.entity?.getId?.());
    } catch {
      warnings.push("经典窗体对象未能补充当前记录标识。");
    }

    try {
      const currentForm = xrm.Page?.ui?.formSelector?.getCurrentItem?.();
      formId ||= cleanGuid(currentForm?.getId?.());
      formLabel = currentForm?.getLabel?.() || null;
    } catch {
      warnings.push("未能从窗体选择器取得窗体名称。");
    }

    let appId = cleanGuid(input.appId || getParam("appid"));
    try {
      const appProperties = await Promise.resolve(xrm.Utility?.getGlobalContext?.()?.getCurrentAppProperties?.());
      appId ||= cleanGuid(appProperties?.appId);
    } catch {
      // App id is optional on classic and older on-premises clients.
    }

    const pageType = input.pageType || getParam("pagetype") || (entityName ? (entityId ? "entityrecord" : "entitylist") : "unknown");
    if (!organizationUrl) {
      organizationUrl = location.origin;
      warnings.push("CRM 客户端没有返回组织地址，暂以当前页面来源作为组织地址。");
    }

    return {
      found: true,
      warnings,
      context: {
        organizationUrl: String(organizationUrl).replace(/\/$/, ""),
        organizationId,
        version: version ? String(version) : null,
        apiVersion: null,
        pageType: String(pageType || "unknown"),
        entityName: entityName ? String(entityName).toLowerCase() : null,
        entityId,
        formId,
        appId,
        formLabel: formLabel ? String(formLabel) : null
      }
    };
  })();
}

function crmRequestInPage(request) {
  return (async () => {
    try {
      if (request.method !== "GET") {
        return { ok: false, status: 405, statusText: "Only read-only GET requests are allowed", body: "" };
      }

      const target = new URL(request.url);
      const organization = new URL(request.organizationUrl);
      const organizationPath = organization.pathname.replace(/\/$/, "").toLowerCase();
      const expectedPath = `${organizationPath}/api/data/`;
      if (target.origin !== organization.origin || !target.pathname.toLowerCase().startsWith(expectedPath)) {
        return { ok: false, status: 400, statusText: "Request is outside the detected CRM Web API", body: "" };
      }

      const response = await fetch(target.href, {
        method: "GET",
        credentials: "include",
        cache: "no-store",
        headers: {
          Accept: "application/json",
          "OData-MaxVersion": "4.0",
          "OData-Version": "4.0",
          Prefer: "odata.maxpagesize=5000"
        }
      });
      return {
        ok: response.ok,
        status: response.status,
        statusText: response.statusText,
        contentType: response.headers.get("content-type") || "",
        body: await response.text()
      };
    } catch (error) {
      return { ok: false, status: 0, statusText: String(error?.message || error), body: "" };
    }
  })();
}

async function crmGetJson(located, url) {
  const injected = await chrome.scripting.executeScript({
    target: { tabId: located.tabId, frameIds: [located.frameId] },
    world: "MAIN",
    func: crmRequestInPage,
    args: [{ method: "GET", url, organizationUrl: located.context.organizationUrl }]
  });
  const response = injected[0]?.result;
  if (!response?.ok) {
    const detail = parseServiceError(response?.body) || response?.statusText || "CRM 没有返回结果";
    throw new Error(`${response?.status || "网络"}：${detail}`);
  }
  if (!response.body) {
    return {};
  }
  try {
    return JSON.parse(response.body);
  } catch {
    throw new Error("CRM 返回了无法解析的数据。当前接口版本可能不匹配。");
  }
}

function apiRoot(organizationUrl, apiVersion) {
  return `${String(organizationUrl).replace(/\/$/, "")}/api/data/${apiVersion}`;
}

async function collectForm(located, apiVersion, warnings) {
  const root = apiRoot(located.context.organizationUrl, apiVersion);
  let record = null;
  let sourceUrl = null;

  if (located.context.formId) {
    const formId = normalizeGuid(located.context.formId);
    const urls = [
      `${root}/systemforms(${formId})?$select=formid,name,formxml,versionnumber,objecttypecode,type,ismanaged,customizationlevel`,
      `${root}/systemforms(${formId})?$select=formid,name,formxml,versionnumber,objecttypecode,type,ismanaged`
    ];
    let lastError = null;
    for (const url of urls) {
      try {
        record = await crmGetJson(located, url);
        sourceUrl = url;
        break;
      } catch (error) {
        lastError = error;
      }
    }
    if (!record && lastError) {
      warnings.push(`当前窗体 FormXML 读取失败：${friendlyError(lastError)}`);
    }
    // The actual form definition is required to locate customer event bindings,
    // including customer scripts attached to a managed form.
  }

  if (!record?.formxml && located.context.entityName) {
    const entity = odataString(located.context.entityName);
    const filter = `objecttypecode eq '${entity}' and type eq 2`;
    const urls = [
      `${root}/systemforms?$select=formid,name,formxml,versionnumber,objecttypecode,type,ismanaged,customizationlevel&$filter=${filter}&$top=25`,
      `${root}/systemforms?$select=formid,name,formxml,versionnumber,objecttypecode,type,ismanaged&$filter=${filter}&$top=25`
    ];
    let lastError = null;
    for (const url of urls) {
      try {
        const result = await crmGetJson(located, url);
        const forms = result.value || [];
        record = forms.find((item) => item.name === located.context.formLabel) || forms[0] || null;
        sourceUrl = url;
        if (forms.length > 1 && record && !located.context.formId) {
          warnings.push(`页面没有提供窗体 ID，已按窗体名称或实体主窗体推定“${record.name || "未命名窗体"}”。`);
        }
        break;
      } catch (error) {
        lastError = error;
      }
    }
    if (!record && lastError) {
      warnings.push(`自定义实体窗体读取失败：${friendlyError(lastError)}`);
    }
  }

  if (!record?.formxml) {
    if (!located.context.entityName && !located.context.formId) {
      warnings.push("当前页面没有实体或窗体标识，已跳过 FormXML。 ");
    } else if (record) {
      warnings.push("找到了当前窗体记录，但其中没有可读取的 FormXML。 ");
    }
    return null;
  }

  const formId = normalizeGuid(record.formid || located.context.formId);
  const name = record.name || located.context.formLabel || `${located.context.entityName || "entity"}.form`;
  return {
    xml: record.formxml,
    artifact: {
      kind: ARTIFACT_KIND.formXml,
      name: `${name}.form.xml`,
      componentId: formId,
      version: record.versionnumber == null ? null : String(record.versionnumber),
      mediaType: "application/xml; charset=utf-8",
      contentBase64: utf8ToBase64(record.formxml),
      sourceUrl
    }
  };
}

function isUnmanagedCustomComponent(record) {
  const name = String(record?.name || "").trim();
  if (/^(?:microsoft[.\/_]|mscrm[.\/_]|msdyn[_.\/]|msdynce[_.\/]|crm[._\/]|clientglobalcontext\.js|\$)/i.test(name)) return false;
  // Managed is a packaging flag, not an indication of Microsoft ownership.
  if (record?.ismanaged === true) return /^[a-z][a-z0-9]*[_.\/]/i.test(name);
  return record?.ismanaged === false || Number(record?.customizationlevel) === 1;
}

function isUnmanagedPluginComponent(record) {
  const identities = [record?.name, record?.typename]
    .filter(Boolean)
    .map((value) => String(value).trim().toLowerCase());
  if (identities.some((value) => /^(microsoft(?:\.|\s)|mscrm(?:\.|\s)|msdyn[_.]|msdynce[_.]|system(?:\.|\s))/.test(value))) return false;
  if (record?.ismanaged === true) return identities.length > 0;
  if (record?.customizationlevel !== null && record?.customizationlevel !== undefined) {
    return Number(record.customizationlevel) === 1;
  }
  return record?.ismanaged === false;
}

async function collectRibbons(located, apiVersion, includeApplicationRibbon, warnings) {
  const ribbons = [];
  const root = apiRoot(located.context.organizationUrl, apiVersion);

  if (located.context.entityName) {
    const entity = odataString(located.context.entityName);
    const entityUrl = `${root}/RetrieveEntityRibbon(EntityName='${entity}',RibbonLocationFilter=Microsoft.Dynamics.CRM.RibbonLocationFilters'All')`;
    const entityRibbon = await tryCollectRibbon(located, entityUrl, `${located.context.entityName}.ribbon.xml`, located.context.entityName, warnings);
    if (entityRibbon) ribbons.push(entityRibbon);
  } else {
    warnings.push("当前页面没有实体名称，已跳过实体命令栏。 ");
  }

  if (includeApplicationRibbon) {
    const applicationUrl = `${root}/RetrieveApplicationRibbon(RibbonLocationFilter=Microsoft.Dynamics.CRM.RibbonLocationFilters'All')`;
    const applicationRibbon = await tryCollectRibbon(located, applicationUrl, "application.ribbon.xml", located.context.appId, warnings);
    if (applicationRibbon) ribbons.push(applicationRibbon);
  }

  return ribbons;
}

async function tryCollectRibbon(located, url, name, componentId, warnings) {
  try {
    const response = await crmGetJson(located, url);
    const compressed = findRibbonPayload(response);
    if (!compressed) {
      warnings.push(`${name} 的查询成功，但响应中没有 Ribbon XML。`);
      return null;
    }
    const xml = await decodeRibbonPayload(compressed);
    if (!/^\s*(?:<\?xml\b|<RibbonDefinitions\b|<ImportExportXml\b|<)/i.test(xml)) {
      throw new Error("解压后的内容不是 XML");
    }
    return {
      xml,
      artifact: {
        kind: ARTIFACT_KIND.ribbonXml,
        name,
        componentId: normalizeGuid(componentId) || componentId || null,
        version: located.context.version,
        mediaType: "application/xml; charset=utf-8",
        contentBase64: utf8ToBase64(xml),
        sourceUrl: url
      }
    };
  } catch (error) {
    warnings.push(`${name} 读取失败：${friendlyError(error)}`);
    return null;
  }
}

function findRibbonPayload(value) {
  if (typeof value === "string" || Array.isArray(value)) {
    return value;
  }
  if (!value || typeof value !== "object") {
    return null;
  }

  const preferredNames = [
    "CompressedEntityXml",
    "CompressedApplicationRibbonXml",
    "CompressedApplicationXml",
    "RibbonXml",
    "ExportXml",
    "value"
  ];
  for (const preferred of preferredNames) {
    const match = Object.keys(value).find((key) => key.toLowerCase() === preferred.toLowerCase());
    if (match && (typeof value[match] === "string" || Array.isArray(value[match]))) {
      return value[match];
    }
  }
  for (const [key, nested] of Object.entries(value)) {
    if (/xml|ribbon/i.test(key)) {
      const found = findRibbonPayload(nested);
      if (found) return found;
    }
  }
  return null;
}

async function decodeRibbonPayload(payload) {
  if (Array.isArray(payload)) {
    return decodeRibbonBytes(Uint8Array.from(payload));
  }
  const text = String(payload || "").trim();
  if (text.startsWith("<")) {
    return text;
  }
  let bytes;
  try {
    bytes = base64ToBytes(text);
  } catch {
    throw new Error("Ribbon 响应不是有效的 Base64 数据");
  }
  return decodeRibbonBytes(bytes);
}

async function decodeRibbonBytes(bytes) {
  if (bytes.length > 32 * 1024 * 1024) {
    throw new Error("Ribbon 压缩包超过 32 MiB 安全上限");
  }
  if (bytes[0] === 0x50 && bytes[1] === 0x4b) {
    const xmlBytes = await extractZipPart(bytes, "RibbonXml.xml", COLLECTION_LIMITS.maxArtifactBytes);
    return new TextDecoder("utf-8", { fatal: true }).decode(xmlBytes).replace(/^\uFEFF/, "");
  }
  if (bytes[0] === 0x1f && bytes[1] === 0x8b) {
    const output = await decompressBounded(bytes, "gzip", COLLECTION_LIMITS.maxArtifactBytes);
    return new TextDecoder("utf-8", { fatal: true }).decode(output).replace(/^\uFEFF/, "");
  }
  if (bytes.length > COLLECTION_LIMITS.maxArtifactBytes) {
    throw new Error("Ribbon XML 超过 24 MiB 安全上限");
  }
  const plain = new TextDecoder("utf-8").decode(bytes).replace(/^\uFEFF/, "");
  if (plain.trimStart().startsWith("<")) {
    return plain;
  }
  throw new Error("Ribbon 使用了当前扩展不支持的压缩格式");
}

async function extractZipPart(zipBytes, expectedName, maxOutputBytes) {
  const view = new DataView(zipBytes.buffer, zipBytes.byteOffset, zipBytes.byteLength);
  const eocdOffset = findZipEndOfCentralDirectory(view);
  if (eocdOffset < 0) {
    throw new Error("Ribbon ZIP 缺少中央目录");
  }

  const entryCount = view.getUint16(eocdOffset + 10, true);
  const centralSize = view.getUint32(eocdOffset + 12, true);
  const centralOffset = view.getUint32(eocdOffset + 16, true);
  if (entryCount === 0xffff || centralSize === 0xffffffff || centralOffset === 0xffffffff) {
    throw new Error("Ribbon ZIP64 格式不在浏览器采集范围内");
  }
  if (entryCount > 256 || centralOffset + centralSize > zipBytes.length) {
    throw new Error("Ribbon ZIP 中央目录超出安全边界");
  }

  let cursor = centralOffset;
  for (let index = 0; index < entryCount; index += 1) {
    if (cursor + 46 > zipBytes.length || view.getUint32(cursor, true) !== 0x02014b50) {
      throw new Error("Ribbon ZIP 中央目录已损坏");
    }

    const flags = view.getUint16(cursor + 8, true);
    const method = view.getUint16(cursor + 10, true);
    const compressedSize = view.getUint32(cursor + 20, true);
    const uncompressedSize = view.getUint32(cursor + 24, true);
    const nameLength = view.getUint16(cursor + 28, true);
    const extraLength = view.getUint16(cursor + 30, true);
    const commentLength = view.getUint16(cursor + 32, true);
    const localHeaderOffset = view.getUint32(cursor + 42, true);
    const next = cursor + 46 + nameLength + extraLength + commentLength;
    if (next > zipBytes.length) {
      throw new Error("Ribbon ZIP 条目越界");
    }

    const nameBytes = zipBytes.subarray(cursor + 46, cursor + 46 + nameLength);
    const encoding = (flags & 0x0800) !== 0 ? "utf-8" : "windows-1252";
    const name = new TextDecoder(encoding).decode(nameBytes).replace(/^\/+/, "");
    if (name.toLowerCase() === expectedName.toLowerCase()) {
      if ((flags & 0x0001) !== 0) {
        throw new Error("Ribbon ZIP 条目不应加密");
      }
      if (uncompressedSize > maxOutputBytes || compressedSize > zipBytes.length) {
        throw new Error("RibbonXml.xml 超过安全上限");
      }
      if (localHeaderOffset + 30 > zipBytes.length || view.getUint32(localHeaderOffset, true) !== 0x04034b50) {
        throw new Error("Ribbon ZIP 本地条目已损坏");
      }

      const localNameLength = view.getUint16(localHeaderOffset + 26, true);
      const localExtraLength = view.getUint16(localHeaderOffset + 28, true);
      const dataOffset = localHeaderOffset + 30 + localNameLength + localExtraLength;
      if (dataOffset + compressedSize > zipBytes.length) {
        throw new Error("Ribbon ZIP 压缩数据越界");
      }

      const compressed = zipBytes.subarray(dataOffset, dataOffset + compressedSize);
      let output;
      if (method === 0) {
        output = compressed.slice();
      } else if (method === 8) {
        output = await inflateRawBounded(compressed, maxOutputBytes);
      } else {
        throw new Error(`Ribbon ZIP 使用不支持的压缩算法 ${method}`);
      }

      if (output.length !== uncompressedSize) {
        throw new Error("RibbonXml.xml 解压长度校验失败");
      }
      return output;
    }

    cursor = next;
  }

  throw new Error("Ribbon ZIP 中没有 RibbonXml.xml");
}

function findZipEndOfCentralDirectory(view) {
  const minimum = Math.max(0, view.byteLength - 22 - 0xffff);
  for (let offset = view.byteLength - 22; offset >= minimum; offset -= 1) {
    if (view.getUint32(offset, true) === 0x06054b50) {
      const commentLength = view.getUint16(offset + 20, true);
      if (offset + 22 + commentLength === view.byteLength) {
        return offset;
      }
    }
  }
  return -1;
}

async function inflateRawBounded(compressed, maxOutputBytes) {
  return decompressBounded(compressed, "deflate-raw", maxOutputBytes);
}

async function decompressBounded(compressed, format, maxOutputBytes) {
  if (typeof DecompressionStream !== "function") {
    throw new Error("当前 Edge 版本不支持解压 Ribbon ZIP");
  }

  let stream;
  try {
    stream = new Blob([compressed]).stream().pipeThrough(new DecompressionStream(format));
  } catch {
    throw new Error(`当前 Edge 版本不支持 ${format} 解压`);
  }

  const reader = stream.getReader();
  const chunks = [];
  let total = 0;
  while (true) {
    const { value, done } = await reader.read();
    if (done) break;
    total += value.byteLength;
    if (total > maxOutputBytes) {
      await reader.cancel("Ribbon output limit exceeded");
      throw new Error("RibbonXml.xml 解压后超过安全上限");
    }
    chunks.push(value);
  }

  const output = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    output.set(chunk, offset);
    offset += chunk.byteLength;
  }
  return output;
}

function extractJavaScriptReferences(xmlDocuments) {
  const found = new Map();
  const attributePatterns = [
    /<Library\b[^>]*\bname\s*=\s*["']([^"']+)["']/gi,
    /\bLibrary\s*=\s*["']([^"']+)["']/gi,
    /\$webresource:([^"'<>\s]+)/gi
  ];

  for (const xml of xmlDocuments.filter(Boolean)) {
    for (const pattern of attributePatterns) {
      pattern.lastIndex = 0;
      let match;
      while ((match = pattern.exec(xml)) !== null) {
        let name = decodeXmlEntities(match[1]).trim();
        name = name.replace(/^\/?\$webresource:/i, "").split(/[?#]/, 1)[0];
        try { name = decodeURIComponent(name); } catch { /* keep the original name */ }
        if (!/\.js$/i.test(name) || /^(?:https?:|\/\/)/i.test(name)) continue;
        const key = name.toLowerCase();
        if (!found.has(key)) found.set(key, name);
      }
    }
  }

  return [...found.values()];
}

function normalizeWebResourceName(value) {
  let name = decodeXmlEntities(String(value || "")).trim();
  name = name.replace(/^\/?\$webresource:/i, "").split(/[?#]/, 1)[0];
  try { name = decodeURIComponent(name); } catch { /* keep the original name */ }
  return name.toLowerCase();
}

function filterRibbonXmlForCustomJavaScript(xml, customScriptNames) {
  if (!xml || !customScriptNames?.size) return null;

  const referencesCustomScript = (fragment) => extractJavaScriptReferences([fragment])
    .some((name) => customScriptNames.has(normalizeWebResourceName(name)));
  const commandBlocks = [...xml.matchAll(/<CommandDefinition\b[^>]*>[\s\S]*?<\/CommandDefinition\s*>/gi)]
    .map((match) => match[0])
    .filter(referencesCustomScript);
  if (!commandBlocks.length) return null;

  const commandIds = new Set(commandBlocks
    .map((block) => block.match(/<CommandDefinition\b[^>]*\bId\s*=\s*["']([^"']+)["']/i)?.[1])
    .filter(Boolean));
  const referencedRuleIds = new Set();
  for (const block of commandBlocks) {
    for (const match of block.matchAll(/<(?:EnableRule|DisplayRule)\b[^>]*\bId\s*=\s*["']([^"']+)["'][^>]*\/?\s*>/gi)) {
      referencedRuleIds.add(match[1]);
    }
  }

  const ruleBlocks = [...xml.matchAll(/<(EnableRule|DisplayRule)\b(?![^>]*\/>)[^>]*>[\s\S]*?<\/\1\s*>/gi)]
    .map((match) => match[0])
    .filter((block) => {
      const id = block.match(/\bId\s*=\s*["']([^"']+)["']/i)?.[1];
      return (id && referencedRuleIds.has(id)) || referencesCustomScript(block);
    });

  const controls = [];
  for (const match of xml.matchAll(/<(Button|SplitButton|FlyoutAnchor)\b([^>]*)>/gi)) {
    const command = match[2].match(/\bCommand\s*=\s*["']([^"']+)["']/i)?.[1];
    if (!command || !commandIds.has(command)) continue;
    const attributes = match[2].replace(/\/\s*$/, "").trimEnd();
    controls.push(`<${match[1]}${attributes ? ` ${attributes}` : ""} />`);
  }

  return `<?xml version="1.0" encoding="utf-8"?>\n<RibbonDefinitions>\n` +
    `  <RuleDefinitions>${ruleBlocks.join("\n")}</RuleDefinitions>\n` +
    `  <CommandDefinitions>${commandBlocks.join("\n")}</CommandDefinitions>\n` +
    `  <Controls>${controls.join("\n")}</Controls>\n` +
    `</RibbonDefinitions>`;
}

async function collectWebResources(located, apiVersion, names, warnings) {
  if (!names.length) {
    warnings.push("FormXML 和 Ribbon 中没有发现 JavaScript Web Resource 引用。 ");
    return [];
  }

  const limitedNames = names.slice(0, 90);
  if (names.length > limitedNames.length) {
    warnings.push(`发现 ${names.length} 个脚本引用，本次为控制快照大小仅采集前 ${limitedNames.length} 个。`);
  }

  const root = apiRoot(located.context.organizationUrl, apiVersion);
  let excludedNonCustomCount = 0;
  const results = await mapLimit(limitedNames, 5, async (name) => {
    const safeName = odataString(name);
    const urls = [
      `${root}/webresourceset?$select=webresourceid,name,displayname,content,webresourcetype,modifiedon,ismanaged,customizationlevel&$filter=name eq '${safeName}'&$top=2`,
      `${root}/webresourceset?$select=webresourceid,name,displayname,content,webresourcetype,modifiedon,ismanaged&$filter=name eq '${safeName}'&$top=2`
    ];
    let lastError = null;
    for (const url of urls) {
      try {
        const response = await crmGetJson(located, url);
        const record = (response.value || [])
          .filter(isUnmanagedCustomComponent)
          .find((item) => Number(item.webresourcetype) === 3);
        if (!record) {
          excludedNonCustomCount += 1;
          return null;
        }
        if (!record.content) {
          warnings.push(`${name} 没有可读取的脚本内容。`);
          return null;
        }
        let normalizedContent;
        try {
          normalizedContent = normalizeTextWebResourceBase64(record.content);
        } catch (error) {
          warnings.push(`${name} 不是可安全转换的 UTF-8/UTF-16 脚本文本：${friendlyError(error)}`);
          return null;
        }
        return {
          kind: ARTIFACT_KIND.javaScript,
          name: record.name || name,
          componentId: normalizeGuid(record.webresourceid),
          version: record.modifiedon || null,
          mediaType: "application/javascript; charset=utf-8",
          contentBase64: normalizedContent,
          sourceUrl: url
        };
      } catch (error) {
        lastError = error;
      }
    }

    if (lastError) {
      warnings.push(`自定义脚本 ${name} 读取失败：${friendlyError(lastError)}`);
    }
    return null;
  });

  if (excludedNonCustomCount) {
    warnings.push(`已排除 ${excludedNonCustomCount} 个微软原生或未识别为业务脚本的 Web Resource 引用。`);
  }
  return results.filter(Boolean);
}

async function collectOpenedCustomPages(located, apiVersion, entryScripts, warnings) {
  const pages = new Map();
  const pageScripts = new Map();
  const existingScripts = new Set((entryScripts || [])
    .map(script => `${script.componentId || ""}:${script.name}`.toLowerCase()));
  const queue = extractOpenedPageReferences(entryScripts);
  const root = apiRoot(located.context.organizationUrl, apiVersion);

  for (let depth = 0; depth < 2 && queue.length && pages.size < 12; depth += 1) {
    const batch = queue.splice(0, 12 - pages.size)
      .filter(reference => !pages.has(`${reference.pageType}:${reference.name}`.toLowerCase()));
    for (const reference of batch) {
      const pageKey = `${reference.pageType}:${reference.name}`.toLowerCase();
      if (pages.has(pageKey)) continue;
      if (reference.pageType !== "webresource" || !/\.html?$/i.test(reference.name)) {
        pages.set(pageKey, { ...reference, scripts: [] });
        continue;
      }

      const safeName = odataString(reference.name);
      const urls = [
        `${root}/webresourceset?$select=webresourceid,name,displayname,content,webresourcetype,modifiedon,ismanaged,customizationlevel&$filter=name eq '${safeName}'&$top=2`,
        `${root}/webresourceset?$select=webresourceid,name,displayname,content,webresourcetype,modifiedon,ismanaged&$filter=name eq '${safeName}'&$top=2`
      ];
      let record = null;
      let sourceUrl = null;
      let lastError = null;
      for (const url of urls) {
        try {
          const response = await crmGetJson(located, url);
          record = (response.value || [])
            .filter(isUnmanagedCustomComponent)
            .find(item => Number(item.webresourcetype) === 1) || null;
          sourceUrl = url;
          if (record) break;
        } catch (error) {
          lastError = error;
        }
      }
      if (!record?.content) {
        warnings.push(lastError
          ? `自定义页面 ${reference.name} 读取失败：${friendlyError(lastError)}`
          : `自定义页面 ${reference.name} 没有可读取的业务 HTML 内容。`);
        pages.set(pageKey, { ...reference, scripts: [] });
        continue;
      }

      let html;
      try {
        html = new TextDecoder("utf-8", { fatal: true }).decode(
          base64ToBytes(normalizeTextWebResourceBase64(record.content)));
      } catch (error) {
        warnings.push(`自定义页面 ${reference.name} 不是可安全解析的文本：${friendlyError(error)}`);
        pages.set(pageKey, { ...reference, scripts: [] });
        continue;
      }

      const externalNames = extractHtmlScriptReferences(html, record.name || reference.name);
      const externalScripts = await collectWebResources(located, apiVersion, externalNames, warnings);
      const inlineScripts = extractInlineHtmlScripts(html, record, sourceUrl);
      const scriptsForPage = [];
      for (const script of [...externalScripts, ...inlineScripts]) {
        const key = `${script.componentId || ""}:${script.name}`.toLowerCase();
        if (!existingScripts.has(key) && !pageScripts.has(key)) pageScripts.set(key, script);
        scriptsForPage.push({ artifactName: script.name, componentId: script.componentId || null });
      }
      const page = {
        pageType: "webresource",
        name: record.name || reference.name,
        displayName: record.displayname || record.name || reference.name,
        componentId: normalizeGuid(record.webresourceid),
        scripts: scriptsForPage
      };
      pages.set(pageKey, page);
      queue.push(...extractOpenedPageReferences([...externalScripts, ...inlineScripts]));
    }
  }

  const pageValues = [...pages.values()];
  if (!pageValues.length) return { pages: [], scripts: [], artifacts: [] };
  const catalog = {
    kind: ARTIFACT_KIND.customPageCatalog,
    name: `${located.context.entityName || "page"}.custom-pages.json`.slice(0, 256),
    componentId: located.context.formId || located.context.entityName || "custom-pages",
    version: located.context.version,
    mediaType: "application/json; charset=utf-8",
    contentBase64: utf8ToBase64(JSON.stringify({ pages: pageValues })),
    sourceUrl: `${located.context.organizationUrl.replace(/\/$/, "")}/custom-page-discovery`
  };
  const collectedScripts = [...pageScripts.values()];
  return { pages: pageValues, scripts: collectedScripts, artifacts: [...collectedScripts, catalog] };
}

function extractOpenedPageReferences(scripts) {
  const found = new Map();
  const add = (pageType, rawName) => {
    const name = normalizeOpenedPageName(rawName);
    if (!name || name.length > 256) return;
    const normalizedType = String(pageType || "webresource").toLowerCase() === "custom" ? "custom" : "webresource";
    found.set(`${normalizedType}:${name}`.toLowerCase(), { pageType: normalizedType, name });
  };
  for (const script of scripts || []) {
    let source;
    try {
      source = new TextDecoder("utf-8", { fatal: true }).decode(base64ToBytes(script.contentBase64));
    } catch {
      continue;
    }
    for (const match of source.matchAll(/openWebResource\s*\(\s*["']([^"']+)["']/gi)) add("webresource", match[1]);
    for (const match of source.matchAll(/["']?webresourceName["']?\s*:\s*["']([^"']+)["']/gi)) add("webresource", match[1]);
    for (const match of source.matchAll(/\/WebResources\/([^"'?#\s)]+)/gi)) add("webresource", match[1]);
    for (const block of source.matchAll(/\{[^{}]{0,800}["']?pageType["']?\s*:\s*["']custom["'][^{}]{0,800}\}/gi)) {
      const name = block[0].match(/["']?name["']?\s*:\s*["']([^"']+)["']/i)?.[1];
      if (name) add("custom", name);
    }
  }
  return [...found.values()];
}

function normalizeOpenedPageName(value) {
  let name = decodeXmlEntities(String(value || "")).trim();
  try { name = decodeURIComponent(name); } catch { /* keep source name */ }
  name = name.replace(/^\/?\$webresource:/i, "");
  const webResourcesIndex = name.toLowerCase().indexOf("/webresources/");
  if (webResourcesIndex >= 0) name = name.slice(webResourcesIndex + "/webresources/".length);
  return name.split(/[?#]/, 1)[0].replace(/^\/+/, "").trim();
}

function extractHtmlScriptReferences(html, pageName) {
  const found = new Map();
  for (const match of String(html || "").matchAll(/<script\b[^>]*\bsrc\s*=\s*["']([^"']+)["'][^>]*>/gi)) {
    let name = normalizeOpenedPageName(match[1]);
    if (!name || /^(?:data:|javascript:|https?:\/\/)/i.test(match[1])) continue;
    if (!name.includes("/") && pageName.includes("/")) {
      name = `${pageName.slice(0, pageName.lastIndexOf("/") + 1)}${name}`;
    }
    if (!/\.js$/i.test(name) || name.includes("..")) continue;
    found.set(name.toLowerCase(), name);
  }
  return [...found.values()];
}

function extractInlineHtmlScripts(html, pageRecord, sourceUrl) {
  const scripts = [];
  let index = 0;
  for (const match of String(html || "").matchAll(/<script\b(?![^>]*\bsrc\s*=)[^>]*>([\s\S]*?)<\/script\s*>/gi)) {
    const source = String(match[1] || "").trim();
    if (!source || source.length > 2 * 1024 * 1024) continue;
    index += 1;
    const pageName = String(pageRecord.name || "custom-page");
    scripts.push({
      kind: ARTIFACT_KIND.javaScript,
      name: `${pageName.slice(0, 220)}#inline-${index}.js`,
      componentId: `${normalizeGuid(pageRecord.webresourceid) || pageName}:inline:${index}`.slice(0, 256),
      version: pageRecord.modifiedon || null,
      mediaType: "application/javascript; charset=utf-8",
      contentBase64: utf8ToBase64(source),
      sourceUrl
    });
  }
  return scripts;
}

function extractRelevantCustomApiCalls(scripts) {
  const calls = [];
  const seen = new Set();
  const pattern = /(?:invokeHiddenApiAsync|invokeCustomApiAsync|invokeActionAsync)\s*\(\s*["']([a-zA-Z_][a-zA-Z0-9_.]{0,127})["']\s*,\s*["']([^"'\r\n]{1,240})["']/g;
  for (const script of scripts || []) {
    let source;
    try {
      source = new TextDecoder("utf-8", { fatal: true }).decode(base64ToBytes(script.contentBase64));
    } catch {
      continue;
    }
    for (const match of source.matchAll(pattern)) {
      const name = String(match[1]).trim();
      const route = String(match[2]).trim();
      const key = `${name.toLowerCase()}\n${route.toLowerCase()}`;
      if (seen.has(key)) continue;
      seen.add(key);
      calls.push({ name, route, library: script.name || "" });
      if (calls.length >= 12) return calls;
    }
  }
  return calls;
}

async function collectEntityMetadata(located, apiVersion, warnings) {
  if (!located.context.entityName) {
    warnings.push("当前页面没有实体名称，已跳过字段元数据。 ");
    return null;
  }
  const root = apiRoot(located.context.organizationUrl, apiVersion);
  const entity = odataString(located.context.entityName);
  const url = `${root}/EntityDefinitions(LogicalName='${entity}')?$select=MetadataId,LogicalName,ObjectTypeCode,PrimaryIdAttribute,PrimaryNameAttribute,DisplayName&$expand=Attributes($select=MetadataId,LogicalName,AttributeType,DisplayName)`;
  try {
    const metadata = await crmGetJson(located, url);
    return {
      objectTypeCode: nullableNumber(metadata.ObjectTypeCode),
      artifact: {
        kind: ARTIFACT_KIND.entityMetadata,
        name: `${located.context.entityName}.metadata.json`,
        componentId: normalizeGuid(metadata.MetadataId),
        version: located.context.version,
        mediaType: "application/json; charset=utf-8",
        contentBase64: utf8ToBase64(JSON.stringify(metadata)),
        sourceUrl: url
      }
    };
  } catch (error) {
    warnings.push(`实体字段元数据读取失败：${friendlyError(error)}`);
    return null;
  }
}

async function collectPluginCatalog(located, apiVersion, warnings) {
  const entityName = String(located.context.entityName || "").trim().toLowerCase();
  if (!entityName) {
    warnings.push("当前页面没有实体名称，已跳过插件步骤；不会扫描整个组织的插件目录。");
    return null;
  }

  const root = apiRoot(located.context.organizationUrl, apiVersion);
  const filterUrls = entityMessageFilterUrls(root, entityName);
  const rawFilters = await fetchFirstPageCompatible(
    located,
    "当前实体消息筛选器",
    filterUrls,
    PLUGIN_QUERY_LIMITS.maxEntityFilters,
    warnings);
  const relevantFilters = rawFilters
    .map((item) => ({
      id: normalizeGuid(item.sdkmessagefilterid),
      primaryObjectTypeCode: item.primaryobjecttypecode ?? null,
      secondaryObjectTypeCode: item.secondaryobjecttypecode ?? null
    }))
    .filter((item) => item.id && messageFilterMatchesEntity(item, entityName));
  const filterIds = relevantFilters.map((item) => item.id);

  if (!filterIds.length) {
    warnings.push(`当前实体 ${entityName} 没有可读取的消息筛选器，因此未查询任何插件步骤。`);
    return null;
  }

  const rawSteps = await fetchCustomStepsByFilterIds(located, root, filterIds, warnings);
  const eventHandlerIds = rawSteps
    .map((item) => normalizeGuid(item._eventhandler_value))
    .filter(Boolean);
  const rawTypes = await fetchCustomRecordsByIds(
    located,
    "插件类型",
    `${root}/plugintypes`,
    [
      "plugintypeid,typename,name,_pluginassemblyid_value",
      "plugintypeid,typename,_pluginassemblyid_value"
    ],
    eventHandlerIds,
    PLUGIN_QUERY_LIMITS.maxEventHandlers,
    warnings);
  const assemblyIdsFromTypes = rawTypes
    .map((item) => normalizeGuid(item._pluginassemblyid_value))
    .filter(Boolean);
  const rawAssemblies = await fetchCustomRecordsByIds(
    located,
    "插件程序集",
    `${root}/pluginassemblies`,
    [
      "pluginassemblyid,name,version,sourcetype,isolationmode,path,sourcehash",
      "pluginassemblyid,name,version,sourcetype,isolationmode"
    ],
    assemblyIdsFromTypes,
    COLLECTION_LIMITS.maxPluginAssemblies,
    warnings);

  const customAssemblyIds = new Set(rawAssemblies
    .filter(isUnmanagedPluginComponent)
    .map((item) => normalizeGuid(item.pluginassemblyid))
    .filter(Boolean));
  const customTypes = rawTypes
    .filter(isUnmanagedPluginComponent)
    .filter((item) => customAssemblyIds.has(normalizeGuid(item._pluginassemblyid_value)));
  const customTypeIds = new Set(customTypes
    .map((item) => normalizeGuid(item.plugintypeid))
    .filter(Boolean));
  const customSteps = rawSteps
    .filter(isUnmanagedPluginComponent)
    .filter((item) => customTypeIds.has(normalizeGuid(item._eventhandler_value)));

  if (!customSteps.length && !customTypes.length && !customAssemblyIds.size) {
    warnings.push(`当前实体 ${entityName} 没有找到可读取的自定义插件步骤；已排除已识别的微软原生及无法确认的组件。`);
    return null;
  }

  const excludedStepCount = rawSteps.length - customSteps.length;
  if (excludedStepCount > 0) {
    warnings.push(`当前实体有 ${excludedStepCount} 个步骤未能追溯到自定义插件程序集，已保守排除。`);
  }

  const messageIds = [...new Set(customSteps
    .map((item) => normalizeGuid(item._sdkmessageid_value))
    .filter(Boolean))];
  const rawMessages = await fetchRecordsByIds(
    located,
    "插件消息",
    `${root}/sdkmessages`,
    "sdkmessageid,name",
    messageIds.slice(0, PLUGIN_QUERY_LIMITS.maxMessages),
    warnings);
  if (new Set(messageIds).size > PLUGIN_QUERY_LIMITS.maxMessages) {
    warnings.push(`当前实体相关插件消息超过 ${PLUGIN_QUERY_LIMITS.maxMessages} 个，目录仅保留安全上限内的消息名称。`);
  }

  const customFilterIds = new Set(customSteps
    .map((item) => normalizeGuid(item._sdkmessagefilterid_value))
    .filter(Boolean));

  const catalog = {
    steps: customSteps
      .map((item) => ({
        id: normalizeGuid(item.sdkmessageprocessingstepid),
        name: item.name || "",
        messageId: normalizeGuid(item._sdkmessageid_value),
        filterId: normalizeGuid(item._sdkmessagefilterid_value),
        eventHandlerId: normalizeGuid(item._eventhandler_value),
        stage: nullableNumber(item.stage),
        mode: nullableNumber(item.mode),
        rank: nullableNumber(item.rank),
        filteringAttributes: item.filteringattributes || null,
        stateCode: nullableNumber(item.statecode)
      }))
      .filter((item) => item.id),
    messages: rawMessages
      .map((item) => ({ id: normalizeGuid(item.sdkmessageid), name: item.name || "" }))
      .filter((item) => item.id),
    filters: relevantFilters.filter((item) => customFilterIds.has(item.id)),
    types: customTypes
      .map((item) => ({
        id: normalizeGuid(item.plugintypeid),
        typeName: item.typename || "",
        name: item.name || item.typename || "",
        assemblyId: normalizeGuid(item._pluginassemblyid_value)
      }))
      .filter((item) => item.id),
    assemblies: rawAssemblies
      .filter(isUnmanagedPluginComponent)
      .map((item) => ({
        id: normalizeGuid(item.pluginassemblyid),
        name: item.name || "",
        version: item.version || null,
        sourceType: nullableNumber(item.sourcetype),
        isolationMode: nullableNumber(item.isolationmode),
        path: item.path || null,
        sourceHash: item.sourcehash == null ? null : String(item.sourcehash)
      }))
      .filter((item) => item.id)
  };

  for (const key of Object.keys(catalog)) {
    catalog[key].sort((left, right) => String(left.name || left.typeName || "").localeCompare(String(right.name || right.typeName || ""), "zh-CN"));
  }

  return {
    catalog,
    artifact: {
      kind: ARTIFACT_KIND.pluginCatalog,
      name: "plugin-catalog.json",
      componentId: located.context.organizationId,
      version: located.context.version,
      mediaType: "application/json; charset=utf-8",
      contentBase64: utf8ToBase64(JSON.stringify(catalog)),
      sourceUrl: filterUrls[0]
    }
  };
}

async function collectRelevantCustomApiCatalog(located, apiVersion, calls, warnings) {
  const selectedCalls = (calls || []).slice(0, 12);
  if (!selectedCalls.length) return null;

  const root = apiRoot(located.context.organizationUrl, apiVersion);
  const modernApis = [];
  const legacyMessages = [];
  for (const call of selectedCalls) {
    const safeName = odataString(call.name);
    const modern = await tryReadCollection(located, [
      `${root}/customapis?$select=customapiid,name,uniquename,bindingtype,boundentitylogicalname,isfunction,isprivate,_plugintypeid_value,ismanaged,customizationlevel&$filter=uniquename eq '${safeName}'&$top=3`,
      `${root}/customapis?$select=customapiid,name,uniquename,bindingtype,boundentitylogicalname,isfunction,isprivate,_plugintypeid_value,ismanaged&$filter=uniquename eq '${safeName}'&$top=3`
    ]);
    modernApis.push(...modern.filter(isUnmanagedPluginComponent));

    const legacy = await tryReadCollection(located, [
      `${root}/sdkmessages?$select=sdkmessageid,name&$filter=name eq '${safeName}'&$top=3`
    ]);
    legacyMessages.push(...legacy);
  }

  const uniqueModernApis = deduplicateRecords(modernApis, "customapiid");
  const uniqueLegacyMessages = deduplicateRecords(legacyMessages, "sdkmessageid");
  const legacyMessageIds = uniqueLegacyMessages.map((item) => normalizeGuid(item.sdkmessageid)).filter(Boolean);
  const rawLegacySteps = legacyMessageIds.length
    ? await fetchFirstPageCompatible(
        located,
        "脚本引用的自定义 API/Action 步骤",
        customApiStepCollectionUrls(root, legacyMessageIds),
        60,
        warnings)
    : [];

  const eventHandlerIds = [
    ...uniqueModernApis.map((item) => normalizeGuid(item._plugintypeid_value)),
    ...rawLegacySteps.map((item) => normalizeGuid(item._eventhandler_value))
  ].filter(Boolean);
  const rawTypes = await fetchCustomRecordsByIds(
    located,
    "自定义 API 实现类型",
    `${root}/plugintypes`,
    ["plugintypeid,typename,name,_pluginassemblyid_value", "plugintypeid,typename,_pluginassemblyid_value"],
    eventHandlerIds,
    60,
    warnings);
  const rawAssemblies = await fetchCustomRecordsByIds(
    located,
    "自定义 API 实现程序集",
    `${root}/pluginassemblies`,
    [
      "pluginassemblyid,name,version,sourcetype,isolationmode,path,sourcehash",
      "pluginassemblyid,name,version,sourcetype,isolationmode"
    ],
    rawTypes.map((item) => normalizeGuid(item._pluginassemblyid_value)).filter(Boolean),
    COLLECTION_LIMITS.maxPluginAssemblies,
    warnings);

  const customAssemblyIds = new Set(rawAssemblies
    .filter(isUnmanagedPluginComponent)
    .map((item) => normalizeGuid(item.pluginassemblyid))
    .filter(Boolean));
  const customTypes = rawTypes
    .filter(isUnmanagedPluginComponent)
    .filter((item) => customAssemblyIds.has(normalizeGuid(item._pluginassemblyid_value)));
  const customTypeIds = new Set(customTypes.map((item) => normalizeGuid(item.plugintypeid)).filter(Boolean));
  const customLegacySteps = rawLegacySteps
    .filter(isUnmanagedPluginComponent)
    .filter((item) => customTypeIds.has(normalizeGuid(item._eventhandler_value)));

  const customApis = [];
  for (const call of selectedCalls) {
    const modern = uniqueModernApis.find((item) =>
      String(item.uniquename || "").toLowerCase() === call.name.toLowerCase());
    const legacy = uniqueLegacyMessages.find((item) =>
      String(item.name || "").toLowerCase() === call.name.toLowerCase());
    const legacyStep = legacy && customLegacySteps.find((item) =>
      normalizeGuid(item._sdkmessageid_value) === normalizeGuid(legacy.sdkmessageid));
    const pluginTypeId = normalizeGuid(modern?._plugintypeid_value) || normalizeGuid(legacyStep?._eventhandler_value);
    if (!modern && !legacy) continue;
    customApis.push({
      id: `${normalizeGuid(modern?.customapiid) || normalizeGuid(legacy?.sdkmessageid)}:${call.route}`,
      definitionId: normalizeGuid(modern?.customapiid) || null,
      messageId: normalizeGuid(legacy?.sdkmessageid) || null,
      uniqueName: modern?.uniquename || legacy?.name || call.name,
      name: modern?.name || legacy?.name || call.name,
      route: call.route,
      library: call.library,
      bindingType: nullableNumber(modern?.bindingtype),
      boundEntityLogicalName: modern?.boundentitylogicalname || null,
      isFunction: modern?.isfunction ?? null,
      isPrivate: modern?.isprivate ?? null,
      pluginTypeId,
      source: modern ? "CustomApi" : "SdkMessage"
    });
  }

  if (!customApis.length) {
    warnings.push(`脚本引用了 ${selectedCalls.length} 个自定义 API/Action，但当前用户无法读取其定义或实现类型。`);
    return null;
  }

  const catalog = {
    customApis,
    steps: customLegacySteps.map((item) => ({
      id: normalizeGuid(item.sdkmessageprocessingstepid),
      name: item.name || "",
      messageId: normalizeGuid(item._sdkmessageid_value),
      filterId: normalizeGuid(item._sdkmessagefilterid_value),
      eventHandlerId: normalizeGuid(item._eventhandler_value),
      stage: nullableNumber(item.stage),
      mode: nullableNumber(item.mode),
      rank: nullableNumber(item.rank),
      filteringAttributes: item.filteringattributes || null,
      stateCode: nullableNumber(item.statecode)
    })).filter((item) => item.id),
    messages: uniqueLegacyMessages.map((item) => ({
      id: normalizeGuid(item.sdkmessageid),
      name: item.name || ""
    })).filter((item) => item.id),
    filters: [],
    types: customTypes.map((item) => ({
      id: normalizeGuid(item.plugintypeid),
      typeName: item.typename || "",
      name: item.name || item.typename || "",
      assemblyId: normalizeGuid(item._pluginassemblyid_value)
    })).filter((item) => item.id),
    assemblies: rawAssemblies.filter(isUnmanagedPluginComponent).map((item) => ({
      id: normalizeGuid(item.pluginassemblyid),
      name: item.name || "",
      version: item.version || null,
      sourceType: nullableNumber(item.sourcetype),
      isolationMode: nullableNumber(item.isolationmode),
      path: item.path || null,
      sourceHash: item.sourcehash == null ? null : String(item.sourcehash)
    })).filter((item) => item.id)
  };
  return { catalog, sourceUrl: `${root}/customapis` };
}

function customApiStepCollectionUrls(root, messageIds) {
  const ids = [...new Set((messageIds || []).map(normalizeGuid).filter(Boolean))];
  if (!ids.length) return [];
  const relation = `(${ids.map((id) => `_sdkmessageid_value eq ${id}`).join(" or ")})`;
  const fields = "sdkmessageprocessingstepid,name,_sdkmessageid_value,_sdkmessagefilterid_value,_eventhandler_value,stage,mode,rank,filteringattributes,statecode";
  return [
    `${root}/sdkmessageprocessingsteps?$select=${fields},ismanaged,customizationlevel&$filter=${relation}&$top=60`,
    `${root}/sdkmessageprocessingsteps?$select=${fields},ismanaged&$filter=${relation}&$top=60`,
    `${root}/sdkmessageprocessingsteps?$select=${fields},customizationlevel&$filter=${relation}&$top=60`
  ];
}

async function tryReadCollection(located, urls) {
  for (const url of urls || []) {
    try {
      const response = await crmGetJson(located, url);
      if (Array.isArray(response?.value)) return response.value;
    } catch {
      // Exact-name compatibility probes are intentionally silent; the caller reports one bounded warning.
    }
  }
  return [];
}

function mergePluginCatalogResults(located, ...results) {
  const available = results.filter((result) => result?.catalog);
  if (!available.length) return null;
  const catalog = {};
  const keyFields = {
    customApis: "id",
    steps: "id",
    messages: "id",
    filters: "id",
    types: "id",
    assemblies: "id"
  };
  for (const [key, idField] of Object.entries(keyFields)) {
    const seen = new Set();
    catalog[key] = available.flatMap((result) => result.catalog[key] || []).filter((item) => {
      const id = String(item?.[idField] || "").toLowerCase();
      if (!id || seen.has(id)) return false;
      seen.add(id);
      return true;
    });
  }
  return {
    catalog,
    artifact: {
      kind: ARTIFACT_KIND.pluginCatalog,
      name: "plugin-catalog.json",
      componentId: located.context.organizationId,
      version: located.context.version,
      mediaType: "application/json; charset=utf-8",
      contentBase64: utf8ToBase64(JSON.stringify(catalog)),
      sourceUrl: available[0].artifact?.sourceUrl || available[0].sourceUrl || null
    }
  };
}

function entityMessageFilterUrls(root, entityName) {
  const entity = odataString(String(entityName || "").trim().toLowerCase());
  if (!entity) return [];
  const relation = `(primaryobjecttypecode eq '${entity}' or secondaryobjecttypecode eq '${entity}')`;
  const select = "sdkmessagefilterid,primaryobjecttypecode,secondaryobjecttypecode";
  return [
    `${root}/sdkmessagefilters?$select=${select}&$filter=${relation}&$top=${PLUGIN_QUERY_LIMITS.maxEntityFilters}`
  ];
}

function messageFilterMatchesEntity(filter, entityName) {
  const entity = String(entityName || "").trim().toLowerCase();
  return Boolean(entity) && [filter?.primaryObjectTypeCode, filter?.secondaryObjectTypeCode]
    .some((value) => String(value ?? "").trim().toLowerCase() === entity);
}

function customStepCollectionUrls(root, filterIds, remainingLimit = PLUGIN_QUERY_LIMITS.maxEntitySteps) {
  const ids = [...new Set((filterIds || []).map(normalizeGuid).filter(Boolean))];
  if (!ids.length) return [];
  const relation = `(${ids.map((id) => `_sdkmessagefilterid_value eq ${id}`).join(" or ")})`;
  const fields = "sdkmessageprocessingstepid,name,_sdkmessageid_value,_sdkmessagefilterid_value,_eventhandler_value,stage,mode,rank,filteringattributes,statecode";
  const top = Math.max(1, Math.min(PLUGIN_QUERY_LIMITS.maxEntitySteps, Number(remainingLimit) || 1));
  return [
    `${root}/sdkmessageprocessingsteps?$select=${fields},ismanaged,customizationlevel&$filter=${relation}&$top=${top}`,
    `${root}/sdkmessageprocessingsteps?$select=${fields},ismanaged&$filter=${relation}&$top=${top}`,
    `${root}/sdkmessageprocessingsteps?$select=${fields},customizationlevel&$filter=${relation}&$top=${top}`
  ];
}

async function fetchCustomStepsByFilterIds(located, root, filterIds, warnings) {
  const ids = [...new Set((filterIds || []).map(normalizeGuid).filter(Boolean))];
  const steps = [];
  for (let offset = 0; offset < ids.length && steps.length < PLUGIN_QUERY_LIMITS.maxEntitySteps; offset += PLUGIN_QUERY_LIMITS.filterIdsPerStepQuery) {
    const chunk = ids.slice(offset, offset + PLUGIN_QUERY_LIMITS.filterIdsPerStepQuery);
    const remaining = PLUGIN_QUERY_LIMITS.maxEntitySteps - steps.length;
    const records = await fetchFirstPageCompatible(
      located,
      "当前实体的自定义插件步骤",
      customStepCollectionUrls(root, chunk, remaining),
      remaining,
      warnings);
    steps.push(...records.filter(isUnmanagedPluginComponent));
  }

  if (steps.length >= PLUGIN_QUERY_LIMITS.maxEntitySteps) {
    warnings.push(`当前实体相关的自定义插件步骤达到安全上限（${PLUGIN_QUERY_LIMITS.maxEntitySteps} 项），其余步骤未读取。`);
  }
  return deduplicateRecords(steps, "sdkmessageprocessingstepid");
}

async function fetchFirstPageCompatible(located, label, urls, maxItems, warnings) {
  let lastError = null;
  for (let index = 0; index < urls.length; index += 1) {
    try {
      const response = await crmGetJson(located, urls[index]);
      if (!Array.isArray(response.value)) {
        throw new Error("集合查询没有返回 value 数组");
      }
      const records = response.value.slice(0, maxItems);
      if (response["@odata.nextLink"] || response.value.length > maxItems) {
        warnings.push(`${label}达到安全上限（${maxItems} 项），为避免扫描组织全量数据，其余记录未读取。`);
      }
      if (index > 0) {
        warnings.push(`${label}的部分扩展属性在当前 CRM 版本不可用，已使用兼容字段并应用微软组件名称前缀保守排除。`);
      }
      return records;
    } catch (error) {
      lastError = error;
    }
  }
  warnings.push(`${label}读取失败：${friendlyError(lastError)}`);
  return [];
}

async function fetchCustomRecordsByIds(located, label, collectionUrl, selectVariants, ids, maxIds, warnings) {
  const uniqueIds = [...new Set((ids || []).map(normalizeGuid).filter(Boolean))];
  const selectedIds = uniqueIds.slice(0, maxIds);
  if (uniqueIds.length > selectedIds.length) {
    warnings.push(`${label}引用超过安全上限（${maxIds} 项），其余记录未读取。`);
  }

  let failureCount = 0;
  let nonCustomCount = 0;
  let compatibilityFallbackCount = 0;
  const records = await mapLimit(selectedIds, PLUGIN_QUERY_LIMITS.exactReadConcurrency, async (id) => {
    let record = null;
    for (const fields of selectVariants) {
      const urls = [
        `${collectionUrl}(${id})?$select=${fields},ismanaged,customizationlevel`,
        `${collectionUrl}(${id})?$select=${fields},ismanaged`,
        `${collectionUrl}(${id})?$select=${fields},customizationlevel`
      ];
      for (let urlIndex = 0; urlIndex < urls.length; urlIndex += 1) {
        try {
          record = await crmGetJson(located, urls[urlIndex]);
          if (urlIndex > 0) compatibilityFallbackCount += 1;
          break;
        } catch {
          // Try the v8.2/v9.x compatible field set before treating the id as unreadable.
        }
      }
      if (record) break;
    }
    if (!record) {
      failureCount += 1;
      return null;
    }
    if (!isUnmanagedPluginComponent(record)) {
      nonCustomCount += 1;
      return null;
    }
    return record;
  });

  if (failureCount) {
    warnings.push(`${label}中有 ${failureCount} 个当前实体步骤引用的记录无法读取，已保守排除。`);
  }
  if (nonCustomCount) {
    warnings.push(`${label}中有 ${nonCustomCount} 个微软原生或无法确认的记录，已排除。`);
  }
  if (compatibilityFallbackCount) {
    warnings.push(`${label}中有 ${compatibilityFallbackCount} 个记录只能用旧版兼容字段判断自定义层，已同时应用微软组件名称前缀保守排除。`);
  }
  return records.filter(Boolean);
}

function deduplicateRecords(records, idField) {
  const seen = new Set();
  return (records || []).filter((record) => {
    const id = normalizeGuid(record?.[idField]);
    if (!id || seen.has(id)) return false;
    seen.add(id);
    return true;
  });
}

async function fetchRecordsByIds(located, label, collectionUrl, select, ids, warnings) {
  const uniqueIds = [...new Set(ids.map(normalizeGuid).filter(Boolean))];
  if (!uniqueIds.length) return [];

  let failureCount = 0;
  const records = await mapLimit(uniqueIds, 8, async (id) => {
    const url = `${collectionUrl}(${id})?$select=${select}`;
    try {
      return await crmGetJson(located, url);
    } catch {
      failureCount += 1;
      return null;
    }
  });
  if (failureCount) {
    warnings.push(`${label}中有 ${failureCount} 个被自定义插件步骤引用的记录无法读取。`);
  }
  return records.filter(Boolean);
}

async function collectPluginAssemblies(located, apiVersion, catalog, objectTypeCode, budget, warnings) {
  const entityName = String(located.context.entityName || "").toLowerCase();
  if (!entityName) {
    warnings.push("当前页面没有实体名称，无法限定要读取的插件 DLL。");
    return [];
  }

  const relevantFilterIds = new Set(
    (catalog.filters || [])
      .filter((filter) => {
        const primary = String(filter.primaryObjectTypeCode ?? "").toLowerCase();
        const secondary = String(filter.secondaryObjectTypeCode ?? "").toLowerCase();
        const matchesObjectType = (value) => value === entityName ||
          (objectTypeCode !== null && objectTypeCode !== undefined && Number(value) === Number(objectTypeCode));
        return matchesObjectType(primary) || matchesObjectType(secondary);
      })
      .map((filter) => filter.id)
      .filter(Boolean));

  const relevantTypeIds = new Set(
    (catalog.steps || [])
      .filter((step) => step.stateCode === null || step.stateCode === 0)
      .filter((step) => step.filterId && relevantFilterIds.has(step.filterId))
      .map((step) => step.eventHandlerId)
      .filter(Boolean));
  for (const api of catalog.customApis || []) {
    if (api.pluginTypeId) relevantTypeIds.add(api.pluginTypeId);
  }

  const assemblyIds = [...new Set(
    (catalog.types || [])
      .filter((type) => relevantTypeIds.has(type.id))
      .map((type) => type.assemblyId)
      .filter(Boolean))];

  if (!assemblyIds.length) {
    warnings.push("插件目录中没有找到明确绑定当前实体或脚本引用自定义 API 的 DLL；为避免批量导出，未下载其他程序集。");
    return [];
  }

  const maxAssemblies = Math.min(
    COLLECTION_LIMITS.maxPluginAssemblies,
    Math.max(0, Number(budget?.remainingArtifacts) || 0));
  const selectedIds = assemblyIds.slice(0, maxAssemblies);
  if (assemblyIds.length > selectedIds.length) {
    warnings.push(`当前实体关联 ${assemblyIds.length} 个插件程序集，本次仅读取前 ${maxAssemblies} 个。`);
  }

  if (!selectedIds.length) {
    warnings.push("当前快照已达到证据数量上限，未再读取插件 DLL。");
    return [];
  }

  const assembliesById = new Map((catalog.assemblies || []).map((assembly) => [assembly.id, assembly]));
  const root = apiRoot(located.context.organizationUrl, apiVersion);
  const results = [];
  let remainingBytes = Math.min(
    COLLECTION_LIMITS.maxAllPluginAssemblyBytes,
    Math.max(0, Number(budget?.remainingBytes) || 0));

  // Read sequentially: once the total DLL budget is full, the extension stops before
  // requesting another potentially large Base64 payload from CRM.
  for (const assemblyId of selectedIds) {
    if (remainingBytes <= 0) break;
    const catalogEntry = assembliesById.get(assemblyId);
    if (catalogEntry?.sourceType !== 0) {
      const sourceDescription = catalogEntry?.sourceType === 1
        ? "磁盘部署 DLL，浏览器无法访问 CRM 服务器文件系统"
        : `非数据库程序集（SourceType=${catalogEntry?.sourceType ?? "未知"}）`;
      warnings.push(`${catalogEntry?.name || assemblyId} 是${sourceDescription}，已跳过。`);
      continue;
    }

    const url = `${root}/pluginassemblies(${assemblyId})?$select=pluginassemblyid,name,version,sourcetype,content`;
    try {
      const record = await crmGetJson(located, url);
      const contentBase64 = String(record.content || "").replace(/\s/g, "");
      if (!contentBase64) {
        warnings.push(`${record.name || catalogEntry?.name || assemblyId} 没有可通过 Web API 读取的 DLL 内容。`);
        continue;
      }

      const estimatedBytes = base64DecodedLength(contentBase64);
      const perArtifactLimit = Math.min(
        COLLECTION_LIMITS.maxPluginAssemblyBytes,
        Math.max(1, Number(budget?.maxArtifactBytes) || COLLECTION_LIMITS.maxArtifactBytes));
      if (estimatedBytes > perArtifactLimit) {
        warnings.push(`${record.name || assemblyId} 超过当前服务器的单项采集上限，已跳过。`);
        continue;
      }
      if (estimatedBytes > remainingBytes) {
        warnings.push(`${record.name || assemblyId} 会使快照超过 60 MiB，总量预算已用尽，后续 DLL 未再读取。`);
        break;
      }

      const name = String(record.name || catalogEntry?.name || assemblyId);
      results.push({
        kind: ARTIFACT_KIND.pluginAssembly,
        name: /\.dll$/i.test(name) ? name : `${name}.dll`,
        componentId: normalizeGuid(record.pluginassemblyid) || assemblyId,
        version: record.version || catalogEntry?.version || null,
        mediaType: "application/vnd.microsoft.portable-executable",
        contentBase64,
        sourceUrl: url
      });
      remainingBytes -= estimatedBytes;
    } catch (error) {
      warnings.push(`插件 DLL ${catalogEntry?.name || assemblyId} 读取失败：${friendlyError(error)}`);
    }
  }

  return results;
}

function normalizeCrmCollectionLink(nextLink, firstUrl) {
  if (!nextLink) return null;

  const first = new URL(firstUrl);
  const candidate = new URL(String(nextLink), first);
  const apiRootMatch = first.pathname.match(/^(.*\/api\/data\/v\d+\.\d+)(?:\/|$)/i);
  if (!apiRootMatch) {
    throw new Error("CRM 初始查询不是可识别的 Web API 地址");
  }

  const expectedPath = apiRootMatch[1].toLowerCase();
  const candidatePath = candidate.pathname.toLowerCase();
  if (candidate.hash || (candidatePath !== expectedPath && !candidatePath.startsWith(`${expectedPath}/`))) {
    throw new Error("CRM 返回的下一页链接超出当前组织 Web API 路径");
  }

  // On-premises deployments can emit a paging link with an internal FQDN or
  // reverse-proxy host. Keep only its validated API path/query and send it via
  // the already detected organization origin so CRM credentials never cross origins.
  return `${first.origin}${candidate.pathname}${candidate.search}`;
}

async function resolveCollectionLimits(serverUrl, warnings) {
  try {
    const capabilities = await serviceRequest(serverUrl, "/api/v1/capabilities", { method: "GET" });
    const advertised = capabilities?.limits || {};
    const maxRequestBodyBytes = positiveNumber(advertised.maxRequestBodyBytes);
    const requestBodyBudget = maxRequestBodyBytes === null
      ? COLLECTION_LIMITS.maxSnapshotBytes
      : Math.max(1, Math.floor(maxRequestBodyBytes * 0.70));
    return {
      ...COLLECTION_LIMITS,
      maxArtifacts: Math.min(
        COLLECTION_LIMITS.maxArtifacts,
        Math.floor(positiveNumber(advertised.maxArtifactsPerSnapshot) ?? COLLECTION_LIMITS.maxArtifacts)),
      maxArtifactBytes: Math.min(
        COLLECTION_LIMITS.maxArtifactBytes,
        Math.floor(positiveNumber(advertised.maxArtifactBytes) ?? COLLECTION_LIMITS.maxArtifactBytes)),
      maxSnapshotBytes: Math.min(
        COLLECTION_LIMITS.maxSnapshotBytes,
        Math.floor(positiveNumber(advertised.maxSnapshotBytes) ?? COLLECTION_LIMITS.maxSnapshotBytes),
        requestBodyBudget)
    };
  } catch (error) {
    warnings.push(`未能读取分析服务器容量，先按保守默认值采集：${friendlyError(error)}`);
    return { ...COLLECTION_LIMITS };
  }
}

function positiveNumber(value) {
  const number = Number(value);
  return Number.isFinite(number) && number > 0 ? number : null;
}

async function uploadSnapshot(serverUrl, organizationUrl, snapshot) {
  const baseUrl = normalizeServerUrl(serverUrl);
  if (new URL(baseUrl).origin === new URL(organizationUrl).origin) {
    throw new Error("分析服务器不能使用 CRM 的同一来源地址。请配置独立 Windows 分析服务器。 ");
  }
  return serviceRequest(baseUrl, "/api/v1/snapshots", {
    method: "POST",
    body: snapshot
  });
}

async function checkJob(jobId) {
  const id = requireIdentifier(jobId, "分析任务 ID");
  const session = await getSession();
  const settings = await getSettings();
  const serverUrl = session?.jobId === jobId && session.serverUrl ? session.serverUrl : settings.serverUrl;
  const job = await serviceRequest(serverUrl, `/api/v1/jobs/${encodeURIComponent(id)}`, { method: "GET" });
  if (session?.jobId === jobId) {
    await saveSession({ ...session, status: job.status || job.state || session.status });
  }
  return job;
}

async function getEvidence(snapshotId) {
  const id = requireIdentifier(snapshotId, "快照 ID");
  const session = await getSession();
  const settings = await getSettings();
  const serverUrl = session?.snapshotId === snapshotId && session.serverUrl ? session.serverUrl : settings.serverUrl;
  return serviceRequest(serverUrl, `/api/v1/snapshots/${encodeURIComponent(id)}/evidence`, { method: "GET" });
}

async function askQuestion(snapshotId, question, requestId) {
  const id = requireIdentifier(snapshotId, "快照 ID");
  const normalizedQuestion = String(question || "").trim();
  if (!normalizedQuestion) {
    throw new Error("请输入要了解的业务逻辑问题。 ");
  }
  if (normalizedQuestion.length > 2000) {
    throw new Error("问题不能超过 2000 个字符。 ");
  }
  const session = await getSession();
  const settings = await getSettings();
  const serverUrl = session?.snapshotId === snapshotId && session.serverUrl ? session.serverUrl : settings.serverUrl;
  const libraryMap = (await chrome.storage.local.get("codeLibraries")).codeLibraries || {};
  const environmentLibraryId = settings.includePluginAssemblies && typeof codeLibraryScope === "function"
    ? libraryMap[codeLibraryScope(serverUrl, session?.context?.organizationUrl)]?.libraryId || null : null;
  const recordingStore = await chrome.storage.session.get("runtimeRecording");
  const runtimeRecording = recordingMatchesSession(recordingStore.runtimeRecording, session)
    ? recordingStore.runtimeRecording.events.slice(0, 100)
    : [];
  const dataResults = new Map();
  const formValueResults = new Map();
  let continuationId = null;
  let runtimeDiagnostics = [];
  if (settings.allowCrmDataAccess) {
    try {
      const tab = await getActiveHttpTab();
      const located = await locateD365Context(tab.id);
      await installRuntimeDiagnostics(located);
      runtimeDiagnostics = await readRuntimeDiagnostics(session);
    } catch {
      runtimeDiagnostics = [];
    }
  }
  let accumulatedTrace = [];
  for (let round = 0; round < 4; round += 1) {
    const response = await serviceStreamRequest(serverUrl, "/api/v1/chat/stream", {
      method: "POST",
      body: {
        snapshotId: id,
        question: normalizedQuestion,
        environmentLibraryId,
        dataAccessConsent: settings.allowCrmDataAccess,
        dataResults: [...dataResults.values()],
        runtimeDiagnostics,
        formValueResults: [...formValueResults.values()],
        continuationId,
        runtimeRecordingConsent: runtimeRecording.length > 0,
        runtimeRecording
      }
    }, (event) => {
      broadcastChatProgress(requestId, event);
    });
    continuationId = response.continuationId || null;
    accumulatedTrace = mergeAnalysisTrace(accumulatedTrace, response.trace);
    const requests = Array.isArray(response.dataRequests) ? response.dataRequests : [];
    const formValueRequests = Array.isArray(response.formValueRequests) ? response.formValueRequests : [];
    if (requests.length === 0 && formValueRequests.length === 0) {
      return { ...response, trace: accumulatedTrace };
    }
    if (!settings.allowCrmDataAccess) {
      throw new Error("AI 请求读取 CRM 数据，但用户尚未在连接设置中授权。");
    }
    broadcastChatProgress(requestId, {
      type: "progress",
      step: {
        title: "读取当前 CRM 数据",
        summary: "正在通过当前登录用户执行模型选定的只读查询。",
        toolName: requests.length ? "query_crm_data" : "read_current_form_values",
        status: "active"
      }
    });
    const fetched = await executeCrmDataRequests(requests, settings, session);
    for (const result of fetched) {
      dataResults.set(result.requestId, result);
    }
    const formValues = await executeFormValueRequests(formValueRequests, session);
    for (const result of formValues) {
      formValueResults.set(result.requestId, result);
    }
  }
  throw new Error("AI 在一次问答中请求了过多批次的 CRM 数据，请缩小问题范围后重试。");
}

function broadcastChatProgress(requestId, event) {
  if (!requestId || !event || event.type !== "progress") return;
  try {
    const delivery = chrome.runtime.sendMessage({
      type: "CHAT_PROGRESS",
      requestId,
      event
    });
    if (delivery && typeof delivery.catch === "function") {
      delivery.catch(() => {});
    }
  } catch {
    // The side panel may have been closed while the analysis keeps running.
  }
}

function recordingMatchesSession(recording, session) {
  if (!recording || !Array.isArray(recording.events) || !recording.events.length || !session) return false;
  if (recording.snapshotId && session.snapshotId && recording.snapshotId !== session.snapshotId) return false;
  return sameRecordingScope(recording.context, session.context);
}

function sameRecordingScope(left, right) {
  const leftOrganization = String(left?.organizationUrl || "").replace(/\/$/, "").toLowerCase();
  const rightOrganization = String(right?.organizationUrl || "").replace(/\/$/, "").toLowerCase();
  if (!leftOrganization || !rightOrganization || leftOrganization !== rightOrganization) return false;
  if (!left?.entityName || !right?.entityName ||
      String(left.entityName).toLowerCase() !== String(right.entityName).toLowerCase()) return false;
  if (left.formId && right.formId && String(left.formId).toLowerCase() !== String(right.formId).toLowerCase()) return false;
  return true;
}

async function executeFormValueRequests(requests, session) {
  if (requests.length > 3) {
    throw new Error("单轮当前窗体值查询不能超过 3 项。");
  }
  if (requests.length === 0) return [];
  const tab = await getActiveHttpTab();
  const located = await locateD365Context(tab.id);
  const expectedOrganization = String(session?.context?.organizationUrl || "").replace(/\/$/, "").toLowerCase();
  const actualOrganization = String(located.context.organizationUrl || "").replace(/\/$/, "").toLowerCase();
  if (expectedOrganization && expectedOrganization !== actualOrganization) {
    throw new Error("当前 CRM 组织与生成快照的组织不一致，已阻止读取窗体值。");
  }
  if (String(session?.context?.entityName || "").toLowerCase() !== String(located.context.entityName || "").toLowerCase()) {
    throw new Error("当前窗体实体与生成快照的实体不一致，已阻止读取窗体值。");
  }
  if (session?.context?.formId && located.context.formId &&
      String(session.context.formId).toLowerCase() !== String(located.context.formId).toLowerCase()) {
    throw new Error("当前窗体与生成快照时的窗体不一致，请重新采集后再提问。");
  }
  const results = [];
  for (const request of requests) {
    const requestId = String(request?.requestId || "");
    try {
      if (!/^[a-zA-Z0-9_-]{1,64}$/.test(requestId)) throw new Error("窗体值请求 ID 无效");
      const fields = [...new Set((request?.fields || []).map(value => String(value).toLowerCase()))];
      if (fields.length < 1 || fields.length > 20 || fields.some(value => !/^[a-z][a-z0-9_]{0,127}$/.test(value))) {
        throw new Error("窗体字段列表无效");
      }
      const injected = await chrome.scripting.executeScript({
        target: { tabId: located.tabId, frameIds: [located.frameId] },
        world: "MAIN",
        func: readCurrentFormValuesInPage,
        args: [fields]
      });
      const payload = injected?.[0]?.result;
      if (!payload?.ok) throw new Error(payload?.error || "CRM 页面没有返回窗体值");
      const json = JSON.stringify(payload);
      if (json.length > 100000) throw new Error("窗体值结果超过 100000 字符上限");
      results.push({ requestId, success: true, json });
    } catch (error) {
      results.push({ requestId, success: false, error: friendlyError(error).slice(0, 1000) });
    }
  }
  return results;
}

function readCurrentFormValuesInPage(fields) {
  try {
    const page = globalThis.Xrm?.Page;
    if (!page?.data?.entity) {
      return { ok: false, error: "当前 CRM 页面没有可访问的记录窗体上下文。" };
    }
    const normalizeValue = (value, depth = 0) => {
      if (value == null || typeof value === "string" || typeof value === "number" || typeof value === "boolean") return value;
      if (value instanceof Date) return value.toISOString();
      if (depth >= 3) return String(value);
      if (Array.isArray(value)) return value.slice(0, 20).map(item => normalizeValue(item, depth + 1));
      if (typeof value === "object") {
        const safe = {};
        for (const key of ["id", "name", "entityType", "typename", "type"]) {
          if (value[key] != null) safe[key] = normalizeValue(value[key], depth + 1);
        }
        return Object.keys(safe).length ? safe : String(value);
      }
      return String(value);
    };
    const readControls = (attribute, field) => {
      let controls = [];
      try { controls = attribute?.controls?.get?.() || []; } catch { controls = []; }
      if (!Array.isArray(controls)) controls = [];
      if (!controls.length) {
        try {
          const direct = page.getControl?.(field);
          if (direct) controls = [direct];
        } catch { /* optional */ }
      }
      return controls.slice(0, 10).map(control => {
        let name = null;
        let label = null;
        let visible = null;
        let disabled = null;
        try { name = control.getName?.() ?? null; } catch { /* optional */ }
        try { label = control.getLabel?.() ?? null; } catch { /* optional */ }
        try { visible = control.getVisible?.() ?? null; } catch { /* optional */ }
        try { disabled = control.getDisabled?.() ?? null; } catch { /* optional */ }
        return { name, label, visible, disabled };
      });
    };
    const values = fields.map(field => {
      let attribute = null;
      try { attribute = page.getAttribute?.(field) || null; } catch { attribute = null; }
      if (!attribute) return { field, existsOnForm: false };
      let value = null;
      let text = null;
      let attributeType = null;
      let isDirty = false;
      let submitMode = null;
      let requiredLevel = null;
      try { value = normalizeValue(attribute.getValue?.()); } catch { /* value remains null */ }
      try { text = normalizeValue(attribute.getText?.()); } catch { /* not supported by every type */ }
      try { attributeType = attribute.getAttributeType?.() ?? null; } catch { /* optional */ }
      try { isDirty = Boolean(attribute.getIsDirty?.()); } catch { /* optional */ }
      try { submitMode = attribute.getSubmitMode?.() ?? null; } catch { /* optional */ }
      try { requiredLevel = attribute.getRequiredLevel?.() ?? null; } catch { /* optional */ }
      return {
        field,
        existsOnForm: true,
        value,
        text,
        attributeType,
        isDirty,
        submitMode,
        requiredLevel,
        controls: readControls(attribute, field)
      };
    });
    let entityName = null;
    let entityId = null;
    let formDirty = false;
    try { entityName = page.data.entity.getEntityName?.() ?? null; } catch { /* optional */ }
    try { entityId = page.data.entity.getId?.() ?? null; } catch { /* unsaved record */ }
    try { formDirty = Boolean(page.data.entity.getIsDirty?.()); } catch { /* optional */ }
    return {
      ok: true,
      capturedAt: new Date().toISOString(),
      entityName,
      entityId,
      formDirty,
      values
    };
  } catch (error) {
    return { ok: false, error: String(error?.message || error) };
  }
}

function mergeAnalysisTrace(existing, incoming) {
  const merged = [...(existing || [])];
  for (const step of Array.isArray(incoming) ? incoming : []) {
    if (step?.title === "限定分析范围" && merged.some(item => item?.title === step.title)) continue;
    if (step?.title === "等待浏览器读取数据") continue;
    merged.push(step);
  }
  return merged.map((step, index) => ({ ...step, sequence: index + 1 }));
}

async function executeCrmDataRequests(requests, settings, session) {
  if (requests.length > 3) {
    throw new Error("单轮 CRM 数据查询不能超过 3 项。");
  }
  if (requests.length === 0) return [];
  const tab = await getActiveHttpTab();
  const located = await locateD365Context(tab.id);
  if (session?.context?.organizationUrl &&
      String(session.context.organizationUrl).replace(/\/$/, "").toLowerCase() !==
        String(located.context.organizationUrl).replace(/\/$/, "").toLowerCase()) {
    throw new Error("当前 CRM 组织与生成快照的组织不一致，已阻止数据查询。");
  }
  const warnings = [];
  const apiVersion = session?.context?.apiVersion || await chooseApiVersion(located, settings, warnings);
  const root = `${located.context.organizationUrl}/api/data/${apiVersion}`;
  const entityMetadata = new Map();
  const results = [];

  for (const request of requests) {
    const requestId = String(request?.requestId || "");
    try {
      if (!/^[a-zA-Z0-9_-]{1,64}$/.test(requestId)) throw new Error("数据请求 ID 无效");
      const entity = String(request?.entity || "").toLowerCase();
      if (!/^[a-z][a-z0-9_]{0,127}$/.test(entity)) throw new Error("实体逻辑名无效");
      const select = [...new Set((request?.select || []).map(value => String(value).toLowerCase()))];
      if (select.length < 1 || select.length > 20 || select.some(value => !/^[a-z][a-z0-9_]{0,127}$/.test(value))) {
        throw new Error("字段选择无效");
      }
      const filter = request?.filter == null ? "" : String(request.filter).trim();
      if (filter.length > 500 || /[$;\u0000-\u001f]/.test(filter) || /https?:/i.test(filter)) {
        throw new Error("过滤条件包含不允许的内容");
      }
      const orderBy = request?.orderBy == null ? "" : String(request.orderBy).trim();
      if (orderBy && !/^[a-z][a-z0-9_]*(?:\s+(?:asc|desc))?(?:\s*,\s*[a-z][a-z0-9_]*(?:\s+(?:asc|desc))?)*$/i.test(orderBy)) {
        throw new Error("排序条件无效");
      }
      const currentRecord = request?.currentRecord === true;
      const currentEntity = String(session?.context?.entityName || "").toLowerCase();
      const currentRecordId = normalizeGuid(session?.context?.entityId);
      if (currentRecord && (entity !== currentEntity || !currentRecordId)) {
        throw new Error("当前记录查询与快照实体不一致，或当前记录尚未保存");
      }
      const top = currentRecord ? 1 : Math.min(50, Math.max(1, Number(request?.top) || 10));

      let metadata = entityMetadata.get(entity);
      if (!metadata) {
        const metadataUrl = `${root}/EntityDefinitions(LogicalName='${odataString(entity)}')?$select=EntitySetName,PrimaryIdAttribute`;
        const response = await crmGetJson(located, metadataUrl);
        const entitySet = String(response?.EntitySetName || "");
        const primaryId = String(response?.PrimaryIdAttribute || "").toLowerCase();
        if (!/^[a-zA-Z][a-zA-Z0-9_]{0,127}$/.test(entitySet)) {
          throw new Error(`无法解析实体 ${entity} 的 EntitySetName`);
        }
        if (!/^[a-z][a-z0-9_]{0,127}$/.test(primaryId)) {
          throw new Error(`无法解析实体 ${entity} 的主键字段`);
        }
        metadata = { entitySet, primaryId };
        entityMetadata.set(entity, metadata);
      }

      const url = new URL(`${root}/${metadata.entitySet}`);
      url.searchParams.set("$select", select.join(","));
      url.searchParams.set("$top", String(top));
      const enforcedFilter = currentRecord ? `${metadata.primaryId} eq ${currentRecordId}` : "";
      const finalFilter = enforcedFilter && filter
        ? `(${enforcedFilter}) and (${filter})`
        : enforcedFilter || filter;
      if (finalFilter) url.searchParams.set("$filter", finalFilter);
      if (orderBy) url.searchParams.set("$orderby", orderBy);
      const data = await crmGetJson(located, url.href);
      const json = JSON.stringify(data);
      if (json.length > 150000) throw new Error("查询结果超过 150000 字符上限");
      results.push({ requestId, success: true, json });
    } catch (error) {
      results.push({ requestId, success: false, error: friendlyError(error).slice(0, 1000) });
    }
  }
  return results;
}

async function testServer(candidateUrl) {
  const serverUrl = normalizeServerUrl(candidateUrl);
  let response;
  try {
    response = await fetch(`${serverUrl}/health`, {
      method: "GET",
      credentials: "include",
      cache: "no-store",
      redirect: "error",
      headers: { Accept: "application/json, text/plain" }
    });
  } catch (error) {
    throw new Error(`无法连接分析服务器：${friendlyError(error)}。请检查地址、HTTPS 证书和服务器 CORS 设置。`);
  }

  if (response.status === 401 || response.status === 403) {
    return { message: "服务器可以访问，但当前 Windows 身份尚未获授权。" };
  }
  if (!response.ok) {
    throw new Error(`分析服务器健康检查返回 ${response.status}。`);
  }
  return { message: "分析服务器连接正常。" };
}

async function serviceRequest(candidateUrl, path, options) {
  const serverUrl = normalizeServerUrl(candidateUrl);
  const method = options?.method || "GET";
  const headers = { Accept: "application/json" };
  const request = {
    method,
    credentials: "include",
    cache: "no-store",
    redirect: "error",
    referrerPolicy: "no-referrer",
    headers
  };
  if (options?.body !== undefined) {
    headers["Content-Type"] = "application/json";
    request.body = JSON.stringify(options.body);
  }

  let response;
  try {
    response = await fetch(`${serverUrl}${path}`, request);
  } catch (error) {
    throw new Error(`无法访问分析服务器：${friendlyError(error)}。请检查服务器地址、证书和 CORS 设置。`);
  }

  const text = await response.text();
  let payload = null;
  if (text) {
    try { payload = JSON.parse(text); } catch { payload = text; }
  }
  if (!response.ok) {
    const detail = parseServiceError(payload) || response.statusText || "请求未完成";
    throw new Error(`分析服务器返回 ${response.status}：${detail}`);
  }
  return payload || {};
}

async function serviceStreamRequest(candidateUrl, path, options, onEvent) {
  const serverUrl = normalizeServerUrl(candidateUrl);
  const method = options?.method || "POST";
  const headers = { Accept: "application/x-ndjson" };
  const request = {
    method,
    credentials: "include",
    cache: "no-store",
    redirect: "error",
    referrerPolicy: "no-referrer",
    headers
  };
  if (options?.body !== undefined) {
    headers["Content-Type"] = "application/json";
    request.body = JSON.stringify(options.body);
  }

  let response;
  try {
    response = await fetch(`${serverUrl}${path}`, request);
  } catch (error) {
    throw new Error(`无法访问分析服务器：${friendlyError(error)}。请检查服务器地址、证书和 CORS 设置。`);
  }
  if (!response.ok) {
    const text = await response.text();
    let payload = text;
    try { payload = text ? JSON.parse(text) : null; } catch { /* keep text */ }
    const detail = parseServiceError(payload) || response.statusText || "请求未完成";
    throw new Error(`分析服务器返回 ${response.status}：${detail}`);
  }
  if (!response.body || typeof response.body.getReader !== "function") {
    throw new Error("分析服务器未返回可读取的状态流，请更新 Edge 浏览器或分析服务器。 ");
  }

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";
  let result = null;

  const consumeLine = (line) => {
    if (!line.trim()) return;
    let event;
    try {
      event = JSON.parse(line);
    } catch {
      throw new Error("分析服务器返回了无效的状态流。 ");
    }
    if (event.type === "progress") {
      onEvent?.(event);
    } else if (event.type === "result") {
      result = event.response || {};
    } else if (event.type === "error") {
      throw new Error(event.error || "分析服务器未能完成本次问题。 ");
    }
  };

  while (true) {
    const { value, done } = await reader.read();
    buffer += decoder.decode(value || new Uint8Array(), { stream: !done });
    let newline;
    while ((newline = buffer.indexOf("\n")) >= 0) {
      const line = buffer.slice(0, newline).replace(/\r$/, "");
      buffer = buffer.slice(newline + 1);
      consumeLine(line);
    }
    if (done) break;
  }
  consumeLine(buffer);
  if (result == null) {
    throw new Error("分析状态流已结束，但没有返回最终业务答案。 ");
  }
  return result;
}

function normalizeServerUrl(value) {
  let url;
  try {
    url = new URL(String(value || "").trim());
  } catch {
    throw new Error("请输入完整的分析服务器地址，例如 https://logiclens.contoso.local。 ");
  }
  if (!/^https?:$/.test(url.protocol)) {
    throw new Error("分析服务器地址只能使用 HTTP 或 HTTPS。 ");
  }
  if (url.username || url.password || url.search || url.hash) {
    throw new Error("分析服务器地址不能包含账号、密码、查询参数或片段。 ");
  }
  const path = url.pathname === "/" ? "" : url.pathname.replace(/\/$/, "");
  return `${url.origin}${path}`;
}

function normalizeApiVersionSetting(value) {
  const version = String(value || "auto").toLowerCase();
  if (version === "auto") return version;
  if (!/^v\d+\.\d+$/.test(version)) {
    throw new Error("D365 Web API 版本格式无效。 ");
  }
  return version;
}

function requireIdentifier(value, label) {
  const normalized = String(value || "").trim();
  if (!/^[a-zA-Z0-9_-]{1,128}$/.test(normalized)) {
    throw new Error(`${label}无效。`);
  }
  return normalized;
}

function notifyProgress(step, state, label, artifactCount, warnings) {
  chrome.runtime.sendMessage({
    type: "COLLECTION_PROGRESS",
    step,
    state,
    label,
    artifactCount,
    warnings: warnings ? uniqueStrings(warnings) : undefined
  }).catch(() => {
    // The side panel can be closed while background collection continues.
  });
}

async function mapLimit(items, limit, worker) {
  const results = new Array(items.length);
  let cursor = 0;
  async function run() {
    while (cursor < items.length) {
      const index = cursor;
      cursor += 1;
      results[index] = await worker(items[index], index);
    }
  }
  await Promise.all(Array.from({ length: Math.min(limit, items.length) }, run));
  return results;
}

function enforceSnapshotBudget(artifacts, warnings, limits = COLLECTION_LIMITS) {
  const accepted = [];
  let totalBytes = 0;
  for (const artifact of artifacts) {
    const size = base64DecodedLength(artifact?.contentBase64);
    if (!Number.isFinite(size)) {
      warnings.push(`${artifact?.name || "未命名证据"} 的 Base64 长度无效，已跳过。`);
      continue;
    }
    if (size > limits.maxArtifactBytes) {
      warnings.push(`${artifact.name || "未命名证据"} 超过服务器单项证据上限，已跳过。`);
      continue;
    }
    if (accepted.length >= limits.maxArtifacts ||
        totalBytes + size > limits.maxSnapshotBytes) {
      warnings.push(`${artifact.name || "未命名证据"} 超出快照总量预算，已跳过。`);
      continue;
    }
    accepted.push(artifact);
    totalBytes += size;
  }
  return accepted;
}

function artifactBytes(artifacts) {
  return artifacts.reduce((total, artifact) => {
    const size = base64DecodedLength(artifact?.contentBase64);
    return Number.isFinite(size) ? total + size : total;
  }, 0);
}

function base64DecodedLength(value) {
  const encoded = String(value || "").replace(/\s/g, "");
  if (!encoded || encoded.length % 4 !== 0) return Number.POSITIVE_INFINITY;
  const padding = encoded.endsWith("==") ? 2 : encoded.endsWith("=") ? 1 : 0;
  return (encoded.length / 4) * 3 - padding;
}

function utf8ToBase64(value) {
  const bytes = new TextEncoder().encode(String(value));
  let binary = "";
  const chunkSize = 0x8000;
  for (let offset = 0; offset < bytes.length; offset += chunkSize) {
    binary += String.fromCharCode(...bytes.subarray(offset, offset + chunkSize));
  }
  return btoa(binary);
}

function normalizeTextWebResourceBase64(value) {
  const bytes = base64ToBytes(value);
  let encoding = "utf-8";
  if (bytes.length >= 2 && bytes[0] === 0xff && bytes[1] === 0xfe) {
    encoding = "utf-16le";
  } else if (bytes.length >= 2 && bytes[0] === 0xfe && bytes[1] === 0xff) {
    encoding = "utf-16be";
  }

  const text = new TextDecoder(encoding, { fatal: true }).decode(bytes);
  if (text.includes("\0")) {
    throw new Error("脚本文本包含 NUL 字符");
  }
  return utf8ToBase64(text.replace(/^\uFEFF/, ""));
}

function base64ToBytes(value) {
  let encoded = String(value).replace(/\s/g, "").replace(/-/g, "+").replace(/_/g, "/");
  encoded += "=".repeat((4 - encoded.length % 4) % 4);
  const binary = atob(encoded);
  const bytes = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index += 1) {
    bytes[index] = binary.charCodeAt(index);
  }
  return bytes;
}

function decodeXmlEntities(value) {
  return String(value)
    .replace(/&quot;/gi, "\"")
    .replace(/&apos;/gi, "'")
    .replace(/&lt;/gi, "<")
    .replace(/&gt;/gi, ">")
    .replace(/&amp;/gi, "&")
    .replace(/&#x([0-9a-f]+);/gi, (_match, hex) => String.fromCodePoint(Number.parseInt(hex, 16)))
    .replace(/&#(\d+);/g, (_match, decimal) => String.fromCodePoint(Number.parseInt(decimal, 10)));
}

function odataString(value) {
  return String(value || "").replace(/'/g, "''");
}

function normalizeGuid(value) {
  if (!value) return null;
  const normalized = String(value).trim().replace(/[{}]/g, "").toLowerCase();
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(normalized) ? normalized : null;
}

function nullableNumber(value) {
  if (value === null || value === undefined || value === "") return null;
  const number = Number(value);
  return Number.isFinite(number) ? number : null;
}

function uniqueStrings(values) {
  return [...new Set((values || []).filter(Boolean).map((value) => String(value).trim()).filter(Boolean))];
}

function parseServiceError(value) {
  if (!value) return "";
  if (typeof value === "string") {
    try { return parseServiceError(JSON.parse(value)); } catch { return value.slice(0, 500); }
  }
  return value.error?.message || value.detail || value.message || value.title || "";
}

function friendlyError(error) {
  if (!error) return "未知错误";
  return String(error.message || error).replace(/^Error:\s*/i, "").slice(0, 600);
}
