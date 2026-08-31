"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const zlib = require("node:zlib");
const { performance } = require("node:perf_hooks");

function createZipPart(name, text, method = 8) {
  const nameBytes = Buffer.from(name, "utf8");
  const source = Buffer.from(text, "utf8");
  const compressed = method === 0 ? source : zlib.deflateRawSync(source);

  const local = Buffer.alloc(30);
  local.writeUInt32LE(0x04034b50, 0);
  local.writeUInt16LE(20, 4);
  local.writeUInt16LE(0x0800, 6);
  local.writeUInt16LE(method, 8);
  local.writeUInt32LE(0, 14);
  local.writeUInt32LE(compressed.length, 18);
  local.writeUInt32LE(source.length, 22);
  local.writeUInt16LE(nameBytes.length, 26);

  const central = Buffer.alloc(46);
  central.writeUInt32LE(0x02014b50, 0);
  central.writeUInt16LE(20, 4);
  central.writeUInt16LE(20, 6);
  central.writeUInt16LE(0x0800, 8);
  central.writeUInt16LE(method, 10);
  central.writeUInt32LE(0, 16);
  central.writeUInt32LE(compressed.length, 20);
  central.writeUInt32LE(source.length, 24);
  central.writeUInt16LE(nameBytes.length, 28);
  central.writeUInt32LE(0, 42);

  const localEntry = Buffer.concat([local, nameBytes, compressed]);
  const centralEntry = Buffer.concat([central, nameBytes]);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(1, 8);
  end.writeUInt16LE(1, 10);
  end.writeUInt32LE(centralEntry.length, 12);
  end.writeUInt32LE(localEntry.length, 16);
  return Buffer.concat([localEntry, centralEntry, end]);
}

function createChromeStub() {
  const event = { addListener() {} };
  return {
    runtime: {
      onInstalled: event,
      onStartup: event,
      onMessage: event,
      sendMessage: async () => undefined
    },
    sidePanel: { setPanelBehavior: async () => undefined },
    storage: {
      local: { get: async () => ({}), set: async () => undefined },
      session: { get: async () => ({}), set: async () => undefined }
    },
    tabs: { query: async () => [] },
    scripting: { executeScript: async () => [] }
  };
}

async function main() {
  const backgroundPath = path.resolve(__dirname, "../../src/CrmLogicLens.Extension/background.js");
  const source = fs.readFileSync(backgroundPath, "utf8");
  const context = vm.createContext({
    chrome: createChromeStub(),
    console,
    URL,
    URLSearchParams,
    TextEncoder,
    TextDecoder,
    ArrayBuffer,
    Uint8Array,
    DataView,
    Blob,
    Response,
    DecompressionStream,
    atob,
    btoa,
    fetch,
    performance,
    setTimeout,
    clearTimeout
  });
  vm.runInContext(source, context, { filename: backgroundPath });

  const xml = "<?xml version=\"1.0\"?><RibbonDefinitions><CommandDefinition Id=\"demo\" /></RibbonDefinitions>";
  const zip = createZipPart("RibbonXml.xml", xml);
  context.__zip = Uint8Array.from(zip);
  const unpacked = await vm.runInContext("decodeRibbonBytes(__zip)", context);
  assert.equal(unpacked, xml, "OPC ZIP RibbonXml.xml should be extracted");

  context.__storedZip = Uint8Array.from(createZipPart("/RibbonXml.xml", xml, 0));
  const unpackedStored = await vm.runInContext("decodeRibbonBytes(__storedZip)", context);
  assert.equal(unpackedStored, xml, "stored OPC ZIP parts should be extracted");

  context.__payload = zip.toString("base64url");
  const unpackedUrlSafe = await vm.runInContext("decodeRibbonPayload(__payload)", context);
  assert.equal(unpackedUrlSafe, xml, "Edm.Binary base64url should be accepted");

  assert.equal(vm.runInContext("ARTIFACT_KIND.pluginAssembly", context), "pluginAssembly");
  assert.equal(vm.runInContext("DEFAULT_SETTINGS.allowCrmDataAccess", context), false);
  assert.equal(vm.runInContext("DEFAULT_SETTINGS.settingsSchemaVersion", context), 3);
  assert.equal(vm.runInContext("base64DecodedLength('YWJj')", context), 3);
  context.__utf16Script = Buffer.from("\uFEFFfunction demo() {}", "utf16le").toString("base64");
  const normalizedScript = vm.runInContext("normalizeTextWebResourceBase64(__utf16Script)", context);
  assert.equal(Buffer.from(normalizedScript, "base64").toString("utf8"), "function demo() {}");

  context.__apiScripts = [{
    name: "new_/logic.js",
    contentBase64: Buffer.from(
      'rtcrm.invokeHiddenApiAsync("new_service", "CSSparePartInQuiry/CalculatePrice", { id: "1" });',
      "utf8").toString("base64")
  }];
  const apiCalls = vm.runInContext("extractRelevantCustomApiCalls(__apiScripts)", context);
  assert.equal(apiCalls.length, 1);
  assert.equal(apiCalls[0].name, "new_service");
  assert.equal(apiCalls[0].route, "CSSparePartInQuiry/CalculatePrice");

  context.__pageScripts = [{
    name: "new_/dispatch.js",
    contentBase64: Buffer.from(`
      Xrm.Navigation.openWebResource("new_/dispatch/index.html");
      Xrm.Navigation.navigateTo({ pageType: "custom", name: "new_dispatchpage" });
    `, "utf8").toString("base64")
  }];
  const openedPages = vm.runInContext("extractOpenedPageReferences(__pageScripts)", context);
  assert.equal(openedPages.length, 2);
  assert.ok(openedPages.some(item => item.pageType === "webresource" && item.name === "new_/dispatch/index.html"));
  assert.ok(openedPages.some(item => item.pageType === "custom" && item.name === "new_dispatchpage"));
  context.__pageHtml = '<script src="page.js"></script><script>function loadPeople(){ return Xrm.WebApi.retrieveMultipleRecords("systemuser"); }</script>';
  const pageLibraries = vm.runInContext("extractHtmlScriptReferences(__pageHtml, 'new_/dispatch/index.html')", context);
  assert.equal(pageLibraries[0], "new_/dispatch/page.js");
  context.__pageRecord = { name: "new_/dispatch/index.html", webresourceid: "page-1" };
  const inlineLibraries = vm.runInContext("extractInlineHtmlScripts(__pageHtml, __pageRecord, 'source')", context);
  assert.equal(inlineLibraries.length, 1);
  assert.match(Buffer.from(inlineLibraries[0].contentBase64, "base64").toString("utf8"), /loadPeople/);

  context.__mergeLocated = { context: { organizationId: "org", version: "9.1" } };
  context.__entityCatalog = { catalog: {
    steps: [], messages: [], filters: [], types: [], assemblies: []
  }, artifact: { sourceUrl: "entity-catalog" } };
  context.__apiCatalog = { catalog: {
    customApis: [{ id: "api:route", uniqueName: "new_service" }],
    steps: [], messages: [], filters: [], types: [], assemblies: []
  }, sourceUrl: "customapis" };
  const mergedCatalog = vm.runInContext(
    "mergePluginCatalogResults(__mergeLocated, __entityCatalog, __apiCatalog)", context);
  assert.equal(mergedCatalog.catalog.customApis.length, 1);
  assert.equal(mergedCatalog.catalog.customApis[0].uniqueName, "new_service");

  await vm.runInContext(`
    crmGetJson = async (_located, url) => {
      if (url.includes("/customapis?")) return { value: [{
        customapiid: "11111111-1111-1111-1111-111111111111",
        name: "Service API", uniquename: "new_service",
        _plugintypeid_value: "22222222-2222-2222-2222-222222222222",
        ismanaged: false
      }] };
      if (url.includes("/sdkmessages?")) return { value: [] };
      if (url.includes("/plugintypes(")) return {
        plugintypeid: "22222222-2222-2222-2222-222222222222",
        typename: "Custom.ServiceApiPlugin",
        _pluginassemblyid_value: "33333333-3333-3333-3333-333333333333",
        ismanaged: false
      };
      if (url.includes("/pluginassemblies(")) return {
        pluginassemblyid: "33333333-3333-3333-3333-333333333333",
        name: "Custom.Service.dll", sourcetype: 0, ismanaged: false
      };
      throw new Error("unexpected URL " + url);
    };
  `, context);
  context.__customApiLocated = { context: {
    organizationUrl: "https://crm.example/org",
    organizationId: "org",
    version: "9.1"
  } };
  context.__customApiCalls = [{
    name: "new_service",
    route: "CSSparePartInQuiry/CalculatePrice",
    library: "new_/logic.js"
  }];
  context.__customApiWarnings = [];
  const collectedApi = await vm.runInContext(
    "collectRelevantCustomApiCatalog(__customApiLocated, 'v9.1', __customApiCalls, __customApiWarnings)",
    context);
  assert.equal(collectedApi.catalog.customApis.length, 1);
  assert.equal(collectedApi.catalog.customApis[0].pluginTypeId, "22222222-2222-2222-2222-222222222222");
  assert.equal(collectedApi.catalog.assemblies[0].name, "Custom.Service.dll");

  context.window = {
    __crmLogicLensRuntimeDiagnosticsV1: [{
      method: "POST",
      path: "/demo/api/data/v9.0/new_service",
      status: 400,
      responseBody: '{"error":{"message":"价目表未配置"}}',
      capturedAt: "2026-08-24T00:00:00Z"
    }]
  };
  context.__apiNames = ["new_service"];
  const runtimeErrors = vm.runInContext("readRuntimeDiagnosticsInPage(__apiNames)", context);
  assert.equal(runtimeErrors.length, 1);
  assert.equal(runtimeErrors[0].status, 400);
  assert.match(runtimeErrors[0].responseBody, /价目表未配置/);

  context.window = {
    location: { href: "https://crm.example/org/main.aspx", origin: "https://crm.example" },
    fetch: async () => new Response(JSON.stringify({ value: [] }), {
      status: 200,
      headers: { "Content-Type": "application/json" }
    }),
    __crmLogicLensOperationRecorderV1: { active: true, startedAt: "2026-08-24T00:00:00Z", events: [] }
  };
  vm.runInContext("installRuntimeDiagnosticsInPage('https://crm.example/org')", context);
  await vm.runInContext(`window.fetch(
    "https://crm.example/org/api/data/v9.1/systemusers?$select=fullname&$filter=statecode%20eq%200%20and%20territoryid%20eq%2011111111-2222-4333-8444-555555555555")`, context);
  await new Promise(resolve => setTimeout(resolve, 0));
  const queryEvent = context.window.__crmLogicLensOperationRecorderV1.events.find(item => item.kind === "dataverse-query");
  assert.ok(queryEvent, "successful Dataverse GET should be recorded");
  assert.equal(queryEvent.status, 200);
  assert.match(queryEvent.summary, /返回 0 条/);
  assert.doesNotMatch(queryEvent.details, /11111111-2222-4333-8444-555555555555/);
  assert.match(queryEvent.details, /<guid>/);

  context.window.__crmLogicLensOperationRecorderV1 = {
    active: true,
    startedAt: "2026-08-24T00:00:00Z",
    events: [{
      kind: "user-action",
      summary: "点击“计算价格”",
      details: "元素：button",
      capturedAt: "2026-08-24T00:00:01Z"
    }]
  };
  const stoppedRecording = vm.runInContext("stopRuntimeRecordingInPage()", context);
  assert.equal(stoppedRecording.events.length, 2);
  assert.equal(stoppedRecording.events[0].kind, "user-action");
  assert.equal(context.window.__crmLogicLensOperationRecorderV1.active, false);
  context.__recording = {
    snapshotId: "snapshot-1",
    context: { organizationUrl: "https://crm.example/org", entityName: "account", formId: "form-1" },
    events: [{ kind: "http-error" }]
  };
  context.__recordingSession = {
    snapshotId: "snapshot-1",
    context: { organizationUrl: "https://crm.example/org/", entityName: "account", formId: "form-1" }
  };
  assert.equal(vm.runInContext("recordingMatchesSession(__recording, __recordingSession)", context), true);

  context.fetch = async () => new Response(JSON.stringify({
    limits: {
      maxArtifactBytes: 1024,
      maxSnapshotBytes: 4096,
      maxRequestBodyBytes: 8192,
      maxArtifactsPerSnapshot: 3
    }
  }), { status: 200, headers: { "Content-Type": "application/json" } });
  context.__limitWarnings = [];
  const negotiated = await vm.runInContext(
    "resolveCollectionLimits('https://logiclens.example.local', __limitWarnings)",
    context);
  assert.equal(negotiated.maxArtifactBytes, 1024);
  assert.equal(negotiated.maxSnapshotBytes, 4096);
  assert.equal(negotiated.maxArtifacts, 3);
  context.__shortZip = new Uint8Array(4);
  assert.equal(vm.runInContext("findZipEndOfCentralDirectory(new DataView(__shortZip.buffer))", context), -1);

  await vm.runInContext(`
    getActiveHttpTab = async () => ({ id: 7, url: "https://crm.example/org/main.aspx" });
    locateD365Context = async () => ({
      tabId: 7,
      frameId: 0,
      warnings: [],
      context: { organizationUrl: "https://crm.example/org", apiVersion: "v9.1" }
    });
    __crmUrls = [];
    crmGetJson = async (_located, url) => {
      __crmUrls.push(url);
      return url.includes("EntityDefinitions")
        ? { EntitySetName: "accounts", PrimaryIdAttribute: "accountid" }
        : { value: [{ name: "Contoso" }] };
    };
  `, context);
  context.__dataRequests = [{
    requestId: "abc123",
    entity: "account",
    select: ["name"],
    filter: "statecode eq 0",
    orderBy: "name asc",
    top: 5,
    purpose: "查找有效客户"
  }];
  context.__dataSettings = { apiVersion: "v9.1" };
  context.__dataSession = { context: { organizationUrl: "https://crm.example/org", apiVersion: "v9.1" } };
  const dataResults = await vm.runInContext(
    "executeCrmDataRequests(__dataRequests, __dataSettings, __dataSession)",
    context);
  assert.equal(dataResults.length, 1);
  assert.equal(dataResults[0].success, true);
  assert.match(dataResults[0].json, /Contoso/);
  assert.equal(context.__crmUrls.length, 2);
  assert.match(context.__crmUrls[1], /accounts/);
  assert.match(context.__crmUrls[1], /%24select=name/);

  context.__crmUrls = [];
  context.__dataRequests = [{
    requestId: "current123",
    entity: "account",
    select: ["name"],
    filter: "statecode eq 0",
    top: 50,
    currentRecord: true,
    purpose: "读取当前记录"
  }];
  context.__dataSession = { context: {
    organizationUrl: "https://crm.example/org",
    apiVersion: "v9.1",
    entityName: "account",
    entityId: "11111111-2222-3333-4444-555555555555"
  } };
  const currentResults = await vm.runInContext(
    "executeCrmDataRequests(__dataRequests, __dataSettings, __dataSession)",
    context);
  assert.equal(currentResults[0].success, true);
  assert.match(context.__crmUrls[1], /accountid\+eq\+11111111-2222-3333-4444-555555555555/);
  assert.match(context.__crmUrls[1], /%24top=1/);
  assert.match(context.__crmUrls[1], /statecode\+eq\+0/);

  vm.runInContext(`
    Xrm = { Page: {
      data: { entity: {
        getEntityName: () => "new_ticket",
        getId: () => "{11111111-2222-3333-4444-555555555555}",
        getIsDirty: () => true
      } },
      getAttribute: (name) => name === "new_servicetype" ? {
        getValue: () => 2,
        getText: () => "维修",
        getAttributeType: () => "optionset",
        getIsDirty: () => true,
        getSubmitMode: () => "dirty",
        getRequiredLevel: () => "required",
        controls: { get: () => [{
          getName: () => "new_servicetype",
          getLabel: () => "服务类型",
          getVisible: () => true,
          getDisabled: () => false
        }] }
      } : null,
      getControl: () => null
    } };
  `, context);
  context.__formFields = ["new_servicetype", "new_missing"];
  const liveForm = vm.runInContext("readCurrentFormValuesInPage(__formFields)", context);
  assert.equal(liveForm.ok, true);
  assert.equal(liveForm.formDirty, true);
  assert.equal(liveForm.values[0].text, "维修");
  assert.equal(liveForm.values[0].isDirty, true);
  assert.equal(liveForm.values[0].controls[0].visible, true);
  assert.equal(liveForm.values[1].existsOnForm, false);

  const hookPath = path.resolve(__dirname, "../../src/CrmLogicLens.Extension/runtime-query-hook.js");
  const hookWindow = {
    location: { href: "https://crm.example/org/WebResources/new_/dispatch.html", origin: "https://crm.example" },
    fetch: async () => new Response(JSON.stringify({ value: [] }), {
      status: 200,
      headers: { "Content-Type": "application/json" }
    })
  };
  const hookContext = vm.createContext({
    window: hookWindow,
    URL,
    performance,
    Response,
    console
  });
  vm.runInContext(fs.readFileSync(hookPath, "utf8"), hookContext, { filename: hookPath });
  await vm.runInContext(`window.fetch(
    "https://crm.example/org/api/data/v9.1/systemusers?$filter=statecode%20eq%200")`, hookContext);
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.equal(hookWindow.__crmLogicLensDataverseQueryBufferV1.length, 1);
  assert.match(hookWindow.__crmLogicLensDataverseQueryBufferV1[0].details, /\"resultCount\":0/);
  hookWindow.__crmLogicLensEarlyQueryHookActiveV1 = false;
  await vm.runInContext(`window.fetch(
    "https://crm.example/org/api/data/v9.1/accounts?$select=name")`, hookContext);
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.equal(hookWindow.__crmLogicLensDataverseQueryBufferV1.length, 1, "inactive hook must not retain queries");

  console.log("Extension background tests passed.");
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
