"use strict";

function codeLibraryScope(serverUrl, organizationUrl) {
  return `${normalizeServerUrl(serverUrl).toLowerCase()}|${String(organizationUrl || "").replace(/\/+$/, "").toLowerCase()}`;
}

async function readLibraryCollection(located, url, maxItems, warnings) {
  const records = [], visited = new Set();
  let next = url;
  while (next && records.length < maxItems) {
    if (visited.has(next)) throw new Error("代码库目录分页发生循环。");
    visited.add(next);
    const response = await crmGetJson(located, next);
    if (!Array.isArray(response.value)) throw new Error("代码库目录查询未返回记录集合。");
    if (response.value.length > maxItems - records.length) warnings.push("部分目录超过本次同步范围。");
    records.push(...response.value.slice(0, maxItems - records.length));
    next = response["@odata.nextLink"] ? normalizeCrmCollectionLink(response["@odata.nextLink"], url) : null;
    if (visited.size >= 100) break;
  }
  if (next) warnings.push("部分目录超过本次同步范围，代码库覆盖不完整。");
  return records;
}

async function prepareCodeLibrary() {
  const settings = await getSettings();
  if (!settings.includePluginAssemblies) throw new Error("请先保存允许分析插件代码的设置。");
  const tab = await getActiveHttpTab();
  const located = await locateD365Context(tab.id);
  const warnings = [];
  const apiVersion = await chooseApiVersion(located, settings, warnings);
  const root = apiRoot(located.context.organizationUrl, apiVersion);
  const records = await readLibraryCollection(located,
    `${root}/pluginassemblies?$select=pluginassemblyid,name,version,sourcetype,ismanaged&$filter=not startswith(name,'Microsoft.') and not startswith(name,'msdyn')&$top=301`, 300, warnings);
  const assemblies = records.filter(isUnmanagedPluginComponent).map(a => ({
    id: normalizeGuid(a.pluginassemblyid), name: a.name, sourceType: a.sourcetype
  })).filter(a => a.id);
  if (!assemblies.length) throw new Error("当前登录用户没有可读取的自定义插件程序集。");
  const plan = { context: { ...located.context, apiVersion }, serverUrl: settings.serverUrl,
    assemblies, warnings, libraryId: null, discoveryComplete: warnings.length === 0 };
  await chrome.storage.session.set({ codeLibraryPlan: plan });
  return { count: assemblies.length, warnings };
}

async function syncCodeLibraryBatch(index) {
  const plan = (await chrome.storage.session.get("codeLibraryPlan")).codeLibraryPlan;
  if (!plan || !Number.isInteger(index) || index < 0 || index >= plan.assemblies.length)
    throw new Error("同步计划已失效，请重新开始。");
  const settings = await getSettings();
  if (!settings.includePluginAssemblies) throw new Error("插件代码读取许可已关闭。");
  const tab = await getActiveHttpTab();
  const located = await locateD365Context(tab.id);
  if (codeLibraryScope(settings.serverUrl, located.context.organizationUrl) !== codeLibraryScope(plan.serverUrl, plan.context.organizationUrl))
    throw new Error("CRM 环境或分析服务器已变化，请在原环境重新同步。");
  const selected = plan.assemblies[index];
  if (Number(selected.sourceType) !== 0) throw new Error(`${selected.name} 不在 CRM 数据库中，无法通过浏览器下载 DLL。`);
  const root = apiRoot(plan.context.organizationUrl, plan.context.apiVersion);
  const warnings = [];
  const types = await readLibraryCollection(located,
    `${root}/plugintypes?$select=plugintypeid,name,typename,_pluginassemblyid_value&$filter=_pluginassemblyid_value eq ${selected.id}&$top=1001`, 1000, warnings);
  const steps = [];
  const typeIds = types.map(t => normalizeGuid(t.plugintypeid)).filter(Boolean);
  for (let offset = 0; offset < typeIds.length; offset += 12) {
    const relation = typeIds.slice(offset, offset + 12).map(id => `_eventhandler_value eq ${id}`).join(" or ");
    steps.push(...await readLibraryCollection(located,
      `${root}/sdkmessageprocessingsteps?$select=sdkmessageprocessingstepid,name,_sdkmessageid_value,_sdkmessagefilterid_value,_eventhandler_value,stage,mode,rank,filteringattributes,statecode&$filter=(${relation})&$top=1001`, 1000, warnings));
    if (steps.length >= 2000) { warnings.push("注册步骤过多，仅同步已读取部分。"); break; }
  }
  const filters = await fetchRecordsByIds(located, "步骤筛选器", `${root}/sdkmessagefilters`,
    "sdkmessagefilterid,primaryobjecttypecode,secondaryobjecttypecode", steps.map(s => s._sdkmessagefilterid_value), warnings);
  const messages = await fetchRecordsByIds(located, "步骤消息", `${root}/sdkmessages`,
    "sdkmessageid,name", steps.map(s => s._sdkmessageid_value), warnings);
  const assembly = await crmGetJson(located,
    `${root}/pluginassemblies(${selected.id})?$select=pluginassemblyid,name,version,sourcetype,ismanaged,content`);
  if (!isUnmanagedPluginComponent(assembly) || Number(assembly.sourcetype) !== 0 || !assembly.content)
    throw new Error("程序集当前不可读取或已不属于业务插件。");
  if (base64DecodedLength(assembly.content) > COLLECTION_LIMITS.maxPluginAssemblyBytes)
    throw new Error(`${assembly.name} 超过单个 DLL 读取上限。`);
  const catalog = {
    assemblies: [{ id: selected.id, name: assembly.name, version: assembly.version, sourceType: 0 }],
    types: types.map(t => ({ id: normalizeGuid(t.plugintypeid), name: t.name, typeName: t.typename, assemblyId: selected.id })),
    steps: steps.map(s => ({ id: normalizeGuid(s.sdkmessageprocessingstepid), name: s.name,
      messageId: normalizeGuid(s._sdkmessageid_value), filterId: normalizeGuid(s._sdkmessagefilterid_value),
      eventHandlerId: normalizeGuid(s._eventhandler_value), stage: s.stage, mode: s.mode, rank: s.rank,
      filteringAttributes: s.filteringattributes, stateCode: s.statecode })),
    filters: filters.map(f => ({ id: normalizeGuid(f.sdkmessagefilterid), primaryObjectTypeCode: f.primaryobjecttypecode, secondaryObjectTypeCode: f.secondaryobjecttypecode })),
    messages: messages.map(m => ({ id: normalizeGuid(m.sdkmessageid), name: m.name }))
  };
  const response = await serviceStreamRequest(plan.serverUrl, "/api/v1/code-library/batches/stream", {
    method: "POST", body: { libraryId: plan.libraryId, expectedAssemblies: plan.assemblies.length,
      discoveryComplete: plan.discoveryComplete && warnings.length === 0,
      upload: { context: plan.context, capturedAt: new Date().toISOString(), artifacts: [
        { kind: ARTIFACT_KIND.pluginCatalog, name: "plugin-catalog.json", mediaType: "application/json", contentBase64: utf8ToBase64(JSON.stringify(catalog)) },
        { kind: ARTIFACT_KIND.pluginAssembly, name: `${assembly.name}.dll`, componentId: selected.id, version: assembly.version,
          mediaType: "application/octet-stream", contentBase64: assembly.content }
      ] }
    }
  }, () => {});
  plan.libraryId = response.libraryId;
  if (warnings.length) plan.discoveryComplete = false;
  await chrome.storage.session.set({ codeLibraryPlan: plan });
  const mapping = (await chrome.storage.local.get("codeLibraries")).codeLibraries || {};
  mapping[codeLibraryScope(plan.serverUrl, plan.context.organizationUrl)] = { libraryId: response.libraryId, updatedAt: new Date().toISOString() };
  await chrome.storage.local.set({ codeLibraries: mapping });
  return { ...response, warnings: [...warnings, ...(response.warnings || [])] };
}
