const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const sourcePath = path.resolve(__dirname, "../../src/CrmLogicLens.Extension/sidepanel.js");
const source = fs.readFileSync(sourcePath, "utf8");
const start = source.indexOf("const BUSINESS_EVIDENCE_KINDS");
const end = source.indexOf("async function askQuestion", start);

assert.notEqual(start, -1, "business evidence helpers should exist");
assert.notEqual(end, -1, "business evidence helper boundary should exist");

const context = {};
vm.createContext(context);
vm.runInContext(`${source.slice(start, end)}\nthis.api = { findCurrentEntity, isBusinessEvidenceNode, buildBusinessEvidenceSummary };`, context);

const nodes = [
  { kind: "PageContext", label: "new_case", properties: { Entity: "new_case" } },
  { kind: "ManagedMethod", label: "Newtonsoft.Json.Serialize" },
  { kind: "ManagedType", label: "Newtonsoft.Json.JsonConvert" },
  { kind: "RibbonCommand", label: "提交" },
  { kind: "JavaScriptBehavior", label: "提交校验" },
  { kind: "PluginStep", label: "当前实体步骤", properties: { Entity: "new_case" } },
  { kind: "PluginStep", label: "其他实体步骤", properties: { Entity: "account" } },
  { kind: "PluginAssembly", label: "Custom.Plugins", properties: { ContentAvailable: "true" } }
];

const currentEntity = context.api.findCurrentEntity(nodes);
const businessNodes = nodes.filter(node => context.api.isBusinessEvidenceNode(node, currentEntity));
const summary = context.api.buildBusinessEvidenceSummary(nodes, currentEntity);

assert.equal(currentEntity, "new_case");
assert.equal(businessNodes.some(node => node.kind === "ManagedMethod" || node.kind === "ManagedType"), false);
assert.equal(businessNodes.some(node => node.label === "其他实体步骤"), false);
assert.match(summary, /1 个命令栏动作/);
assert.match(summary, /1 个前端业务行为/);
assert.match(summary, /1 个当前实体插件步骤/);
assert.match(summary, /1 个相关插件程序集/);
assert.doesNotMatch(summary, /托管方法/);

console.log("sidepanel business evidence tests passed");
