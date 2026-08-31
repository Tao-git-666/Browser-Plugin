"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const backgroundPath = path.resolve(__dirname, "../src/CrmLogicLens.Extension/background.js");
const backgroundSource = fs.readFileSync(backgroundPath, "utf8");
const context = vm.createContext({
  console,
  URL,
  TextEncoder,
  TextDecoder,
  btoa: (value) => Buffer.from(value, "binary").toString("base64"),
  atob: (value) => Buffer.from(value, "base64").toString("binary"),
  setTimeout,
  clearTimeout,
  chrome: {
    runtime: {
      onInstalled: { addListener() {} },
      onStartup: { addListener() {} },
      onMessage: { addListener() {} }
    },
    sidePanel: { async setPanelBehavior() {} }
  }
});
vm.runInContext(backgroundSource, context, { filename: backgroundPath });

const filterRibbon = vm.runInContext("filterRibbonXmlForCustomJavaScript", context);
const isCustom = vm.runInContext("isUnmanagedCustomComponent", context);
const isCustomPlugin = vm.runInContext("isUnmanagedPluginComponent", context);
const entityFilterUrls = vm.runInContext("entityMessageFilterUrls", context);
const stepUrlsForFilters = vm.runInContext("customStepCollectionUrls", context);
const normalizeNextLink = vm.runInContext("normalizeCrmCollectionLink", context);
const collectPluginCatalog = vm.runInContext("collectPluginCatalog", context);
const collectPluginAssemblies = vm.runInContext("collectPluginAssemblies", context);
const defaultSettings = vm.runInContext("DEFAULT_SETTINGS", context);

const ribbon = `
<RibbonDefinitions>
  <CommandDefinitions>
    <CommandDefinition Id="Mscrm.Native.Command">
      <Actions><JavaScriptFunction FunctionName="Mscrm.run" Library="$webresource:msdyn_/native.js" /></Actions>
    </CommandDefinition>
    <CommandDefinition Id="new.Account.Command">
      <EnableRules><EnableRule Id="new.Account.Enabled" /></EnableRules>
      <Actions><JavaScriptFunction FunctionName="new_account.run" Library="$webresource:new_/account.js" /></Actions>
    </CommandDefinition>
  </CommandDefinitions>
  <RuleDefinitions>
    <EnableRule Id="new.Account.Enabled"><ValueRule Field="statecode" Value="0" /></EnableRule>
  </RuleDefinitions>
  <Button Id="Mscrm.Native.Button" Command="Mscrm.Native.Command" />
  <Button Id="new.Account.Button" Command="new.Account.Command" LabelText="自定义审批" />
</RibbonDefinitions>`;

const filtered = filterRibbon(ribbon, new Set(["new_/account.js"]));
assert.match(filtered, /new\.Account\.Command/);
assert.match(filtered, /自定义审批/);
assert.match(filtered, /new\.Account\.Enabled/);
assert.doesNotMatch(filtered, /Mscrm\.Native/);
assert.doesNotMatch(filtered, /msdyn_\/native\.js/);

assert.equal(isCustom({ ismanaged: false }), true);
assert.equal(isCustom({ ismanaged: true, customizationlevel: 0 }), false);
assert.equal(isCustomPlugin({ name: "new.BusinessPlugin", ismanaged: false }), true);
assert.equal(isCustomPlugin({ name: "Microsoft.Crm.NativePlugin", ismanaged: false }), false);
assert.equal(isCustomPlugin({ name: "System native step", ismanaged: false, customizationlevel: 0 }), false);
assert.equal(isCustomPlugin({ name: "new.ManagedPlugin", ismanaged: true, customizationlevel: 1 }), false);
assert.equal(defaultSettings.includePluginAssemblies, true);
assert.equal(defaultSettings.allowCrmDataAccess, false);

const filterUrls = entityFilterUrls("https://crm.example/api/data/v8.2", "new_case");
assert.equal(filterUrls.length, 1);
assert.match(filterUrls[0], /\/sdkmessagefilters\?/);
assert.match(filterUrls[0], /primaryobjecttypecode eq 'new_case'/);
assert.match(filterUrls[0], /secondaryobjecttypecode eq 'new_case'/);
assert.match(filterUrls[0], /\$top=500/);

const filterId1 = "11111111-1111-1111-1111-111111111111";
const filterId2 = "22222222-2222-2222-2222-222222222222";
const scopedStepUrls = stepUrlsForFilters(
  "https://crm.example/api/data/v8.2",
  [filterId1, filterId2],
  75);
assert.equal(scopedStepUrls.length, 3);
assert.ok(scopedStepUrls.every((url) => url.includes(`_sdkmessagefilterid_value eq ${filterId1}`)));
assert.ok(scopedStepUrls.every((url) => url.includes(`_sdkmessagefilterid_value eq ${filterId2}`)));
assert.ok(scopedStepUrls.every((url) => url.includes("$filter=")));
assert.ok(scopedStepUrls.every((url) => url.includes("$top=75")));
assert.ok(scopedStepUrls.some((url) => url.includes("ismanaged eq false")));

const normalizedNextLink = normalizeNextLink(
  "https://crm-internal.contoso.local/Contoso/api/data/v9.1/sdkmessageprocessingsteps?$skiptoken=next",
  "https://crm/Contoso/api/data/v9.1/sdkmessageprocessingsteps?$select=name");
assert.equal(
  normalizedNextLink,
  "https://crm/Contoso/api/data/v9.1/sdkmessageprocessingsteps?$skiptoken=next");
assert.throws(() => normalizeNextLink(
  "https://crm-internal.contoso.local/OtherOrg/api/data/v9.1/sdkmessageprocessingsteps?$skiptoken=next",
  "https://crm/Contoso/api/data/v9.1/sdkmessageprocessingsteps?$select=name"));

async function runPluginScopeTests() {
  const stepId = "33333333-3333-3333-3333-333333333333";
  const managedStepId = "44444444-4444-4444-4444-444444444444";
  const typeId = "55555555-5555-5555-5555-555555555555";
  const managedTypeId = "66666666-6666-6666-6666-666666666666";
  const assemblyId = "77777777-7777-7777-7777-777777777777";
  const unrelatedAssemblyId = "88888888-8888-8888-8888-888888888888";
  const messageId = "99999999-9999-9999-9999-999999999999";
  const diskStepId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
  const diskTypeId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
  const diskAssemblyId = "cccccccc-cccc-cccc-cccc-cccccccccccc";
  const calls = [];

  context.__mockCrmGetJson = async (_located, url) => {
    calls.push(url);
    if (url.includes("/sdkmessagefilters?")) {
      return {
        value: [
          {
            sdkmessagefilterid: filterId1,
            primaryobjecttypecode: "new_case",
            secondaryobjecttypecode: null
          },
          {
            sdkmessagefilterid: filterId2,
            primaryobjecttypecode: "contact",
            secondaryobjecttypecode: null
          }
        ]
      };
    }
    if (url.includes("customizationlevel")) {
      throw new Error("Field customizationlevel is unavailable in this CRM version");
    }
    if (url.includes("/sdkmessageprocessingsteps?")) {
      return {
        value: [
          {
            sdkmessageprocessingstepid: stepId,
            name: "new_case Update validation",
            _sdkmessageid_value: messageId,
            _sdkmessagefilterid_value: filterId1,
            _eventhandler_value: typeId,
            stage: 20,
            mode: 0,
            rank: 1,
            statecode: 0,
            ismanaged: false
          },
          {
            sdkmessageprocessingstepid: managedStepId,
            name: "Microsoft managed step",
            _sdkmessageid_value: messageId,
            _sdkmessagefilterid_value: filterId1,
            _eventhandler_value: managedTypeId,
            stage: 20,
            mode: 0,
            rank: 1,
            statecode: 0,
            ismanaged: true
          }
        ]
      };
    }
    if (url.includes(`/plugintypes(${typeId})`)) {
      return {
        plugintypeid: typeId,
        typename: "Contoso.Crm.NewCasePlugin",
        name: "Contoso.Crm.NewCasePlugin",
        _pluginassemblyid_value: assemblyId,
        ismanaged: false
      };
    }
    if (url.includes(`/pluginassemblies(${assemblyId})`)) {
      return {
        pluginassemblyid: assemblyId,
        name: "Contoso.Crm.Plugins",
        version: "1.0.0.0",
        sourcetype: 0,
        isolationmode: 2,
        ismanaged: false
      };
    }
    if (url.includes(`/sdkmessages(${messageId})`)) {
      return { sdkmessageid: messageId, name: "Update" };
    }
    throw new Error(`Unexpected unscoped CRM request: ${url}`);
  };
  vm.runInContext("crmGetJson = globalThis.__mockCrmGetJson", context);

  const warnings = [];
  const located = {
    context: {
      organizationUrl: "https://crm.example/Contoso",
      organizationId: null,
      version: "9.1.0.0",
      entityName: "new_case"
    }
  };
  const result = await collectPluginCatalog(located, "v9.1", warnings);
  assert.ok(result?.catalog);
  assert.equal(result.catalog.steps.length, 1);
  assert.equal(result.catalog.types.length, 1);
  assert.equal(result.catalog.assemblies.length, 1);
  assert.equal(result.catalog.messages.length, 1);
  assert.equal(result.catalog.filters.length, 1);
  assert.ok(warnings.some((warning) => warning.includes("兼容字段")));

  const stepCalls = calls.filter((url) => url.includes("/sdkmessageprocessingsteps?"));
  assert.ok(stepCalls.length > 0);
  assert.ok(stepCalls.every((url) => url.includes(`_sdkmessagefilterid_value eq ${filterId1}`)));
  assert.ok(stepCalls.every((url) => !url.includes(filterId2)));
  assert.ok(calls.some((url) => url.includes(`/plugintypes(${typeId})`)));
  assert.ok(calls.some((url) => url.includes(`/pluginassemblies(${assemblyId})`)));
  assert.ok(!calls.some((url) => url.includes(`/plugintypes(${managedTypeId})`)));
  assert.ok(!calls.some((url) => /\/plugintypes\?/.test(url)));
  assert.ok(!calls.some((url) => /\/pluginassemblies\?/.test(url)));

  const dllCalls = [];
  context.__mockCrmGetJson = async (_located, url) => {
    dllCalls.push(url);
    if (url.includes(`/pluginassemblies(${assemblyId})`)) {
      return {
        pluginassemblyid: assemblyId,
        name: "Contoso.Crm.Plugins",
        version: "1.0.0.0",
        sourcetype: 0,
        content: "TVo="
      };
    }
    throw new Error(`Unrelated DLL must not be requested: ${url}`);
  };
  vm.runInContext("crmGetJson = globalThis.__mockCrmGetJson", context);
  const dllCatalog = {
    filters: [
      { id: filterId1, primaryObjectTypeCode: "new_case", secondaryObjectTypeCode: null },
      { id: filterId2, primaryObjectTypeCode: "contact", secondaryObjectTypeCode: null }
    ],
    steps: [
      { id: stepId, filterId: filterId1, eventHandlerId: typeId, stateCode: 0 },
      { id: diskStepId, filterId: filterId1, eventHandlerId: diskTypeId, stateCode: 0 },
      { id: managedStepId, filterId: filterId2, eventHandlerId: managedTypeId, stateCode: 0 }
    ],
    types: [
      { id: typeId, assemblyId },
      { id: diskTypeId, assemblyId: diskAssemblyId },
      { id: managedTypeId, assemblyId: unrelatedAssemblyId }
    ],
    assemblies: [
      { id: assemblyId, name: "Contoso.Crm.Plugins", sourceType: 0 },
      { id: diskAssemblyId, name: "Contoso.Crm.DiskPlugins", sourceType: 1 },
      { id: unrelatedAssemblyId, name: "Other.Entity.Plugins", sourceType: 0 }
    ]
  };
  const dllWarnings = [];
  const dlls = await collectPluginAssemblies(
    located,
    "v9.1",
    dllCatalog,
    null,
    {
      remainingArtifacts: 10,
      remainingBytes: 1024 * 1024,
      maxArtifactBytes: 1024 * 1024
    },
    dllWarnings);
  assert.equal(dlls.length, 1);
  assert.equal(dlls[0].componentId, assemblyId);
  assert.equal(dllCalls.length, 1);
  assert.ok(dllCalls[0].includes(`/pluginassemblies(${assemblyId})`));
  assert.ok(!dllCalls[0].includes(unrelatedAssemblyId));
  assert.ok(!dllCalls[0].includes(diskAssemblyId));
  assert.ok(dllWarnings.some((warning) => warning.includes("磁盘部署 DLL")));

  const noEntityCalls = [];
  context.__mockCrmGetJson = async (_located, url) => {
    noEntityCalls.push(url);
    throw new Error("No CRM request expected without an entity");
  };
  vm.runInContext("crmGetJson = globalThis.__mockCrmGetJson", context);
  const noEntityResult = await collectPluginCatalog(
    { context: { organizationUrl: "https://crm.example/Contoso", entityName: null } },
    "v8.2",
    []);
  assert.equal(noEntityResult, null);
  assert.equal(noEntityCalls.length, 0);
}

runPluginScopeTests()
  .then(() => console.log("Extension entity-scoped custom-component filtering smoke test passed."))
  .catch((error) => {
    console.error(error);
    process.exitCode = 1;
  });
