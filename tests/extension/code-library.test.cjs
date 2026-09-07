"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

async function run(version) {
  const local = {}, session = {}, calls = [];
  const storage = values => ({ async get(key) { return { [key]: values[key] }; }, async set(update) { Object.assign(values, update); } });
  const context = vm.createContext({ console, URL, TextEncoder, TextDecoder, setTimeout, clearTimeout,
    btoa: value => Buffer.from(value, "binary").toString("base64"), atob: value => Buffer.from(value, "base64").toString("binary"),
    chrome: { storage: { local: storage(local), session: storage(session) },
      runtime: { onInstalled: { addListener() {} }, onStartup: { addListener() {} }, onMessage: { addListener() {} } },
      sidePanel: { async setPanelBehavior() {} } }
  });
  for (const file of ["background.js", "code-library.js"])
    vm.runInContext(fs.readFileSync(path.join(__dirname, "../../src/CrmLogicLens.Extension", file), "utf8"), context);
  const assembly = "11111111-1111-1111-1111-111111111111";
  const type = "22222222-2222-2222-2222-222222222222";
  const step = "33333333-3333-3333-3333-333333333333";
  const filter = "44444444-4444-4444-4444-444444444444";
  const message = "55555555-5555-5555-5555-555555555555";
  const library = "66666666-6666-6666-6666-666666666666";
  context.__settings = { serverUrl: "http://localhost:5165", includePluginAssemblies: true };
  context.__located = { tabId: 1, frameId: 0, context: { organizationUrl: "https://crm.example/Org", pageType: "entityrecord", entityName: "new_equipment" } };
  context.__version = version;
  context.__get = async (_located, url) => {
    calls.push(url);
    assert.ok(url.startsWith(`https://crm.example/Org/api/data/${version}/`));
    if (url.includes("/pluginassemblies?")) {
      assert.ok(url.includes("$top=301")); // $top is a total limit, not a page size. Read one extra to detect overflow.
      return { value: [{ pluginassemblyid: assembly, name: "Contoso.Business", ismanaged: true, sourcetype: 0 }] };
    }
    if (url.includes("/plugintypes?")) {
      assert.ok(url.includes(`_pluginassemblyid_value eq ${assembly}`));
      assert.ok(url.includes("$top=1001"));
      return { value: [{ plugintypeid: type, typename: "Contoso.WorkOrderPlugin", _pluginassemblyid_value: assembly }] };
    }
    if (url.includes("/sdkmessageprocessingsteps?")) {
      assert.ok(url.includes(`_eventhandler_value eq ${type}`));
      assert.ok(url.includes("$top=1001"));
      return { value: [{ sdkmessageprocessingstepid: step, name: "WorkOrder Update", _eventhandler_value: type,
        _sdkmessageid_value: message, _sdkmessagefilterid_value: filter, stage: 40, mode: 0, statecode: 1, filteringattributes: "new_status" }] };
    }
    if (url.includes(`/sdkmessagefilters(${filter})`)) return { sdkmessagefilterid: filter, primaryobjecttypecode: "new_workorder" };
    if (url.includes(`/sdkmessages(${message})`)) return { sdkmessageid: message, name: "Update" };
    if (url.includes(`/pluginassemblies(${assembly})`)) return { pluginassemblyid: assembly, name: "Contoso.Business", version: "1.0", ismanaged: true, sourcetype: 0, content: "TVo=" };
    throw new Error(`Unexpected query ${url}`);
  };
  context.__send = async (_server, route, options) => {
    assert.equal(route, "/api/v1/code-library/batches/stream");
    const catalog = JSON.parse(Buffer.from(options.body.upload.artifacts[0].contentBase64, "base64").toString());
    assert.equal(catalog.filters[0].primaryObjectTypeCode, "new_workorder");
    assert.equal(catalog.steps[0].stateCode, 1); // Disabled registrations are diagnostic evidence too.
    assert.equal(options.body.upload.context.entityName, "new_equipment");
    assert.equal(options.body.expectedAssemblies, 1);
    return { libraryId: library, indexed: 1, cacheHit: false, warnings: [] };
  };
  vm.runInContext("getSettings = async () => __settings; getActiveHttpTab = async () => ({id:1}); locateD365Context = async () => __located; chooseApiVersion = async () => __version; crmGetJson = __get; serviceStreamRequest = __send;", context);
  const plan = await vm.runInContext("prepareCodeLibrary()", context);
  assert.equal(plan.count, 1);
  await vm.runInContext("syncCodeLibraryBatch(0)", context);
  assert.equal(local.codeLibraries["http://localhost:5165|https://crm.example/org"].libraryId, library);
  context.__located.context.organizationUrl = "https://crm.example/OtherOrg";
  await assert.rejects(vm.runInContext("syncCodeLibraryBatch(0)", context), /环境或分析服务器已变化/);
  context.__settings.includePluginAssemblies = false;
  await assert.rejects(vm.runInContext("prepareCodeLibrary()", context), /允许分析插件代码/);
  assert.ok(calls.length >= 6);
  context.__pages = 0;
  vm.runInContext("crmGetJson = async () => (++__pages === 1 ? {value:[1,2], '@odata.nextLink':'https://crm.example/Org/api/data/' + __version + '/pluginassemblies?$skiptoken=next'} : {value:[3,4]});", context);
  const coverage = await vm.runInContext("(async () => { const warnings=[]; const rows=await readLibraryCollection(__located, 'https://crm.example/Org/api/data/' + __version + '/pluginassemblies?$top=4', 3, warnings); return {rows,warnings}; })()", context);
  assert.equal(coverage.rows.length, 3);
  assert.ok(coverage.warnings.length > 0);
}

Promise.resolve().then(() => run("v8.2")).then(() => run("v9.0")).then(() => run("v9.2"))
  .then(() => console.log("Environment library sync tests passed (v8.2, v9.0, v9.2)."))
  .catch(error => { console.error(error); process.exitCode = 1; });
