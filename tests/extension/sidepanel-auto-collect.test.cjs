const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.resolve(__dirname, "../..");
const source = fs.readFileSync(path.join(root, "src/CrmLogicLens.Extension/sidepanel.js"), "utf8");
const html = fs.readFileSync(path.join(root, "src/CrmLogicLens.Extension/sidepanel.html"), "utf8");
const manifest = JSON.parse(fs.readFileSync(path.join(root, "src/CrmLogicLens.Extension/manifest.json"), "utf8"));

for (const id of ["tracePanel", "warningPanel", "evidencePanel"]) {
  assert.match(html, new RegExp(`<details[^>]+id=["']${id}["']`));
}
for (const id of ["faultRecorder", "recordButton", "recordStatus", "recordCount"]) {
  assert.match(html, new RegExp(`id=["']${id}["']`));
}
for (const id of ["openToolsButton", "toolsDialog", "inspectToolGrid", "diagnosticToolGrid", "toolResult"]) {
  assert.match(html, new RegExp(`id=["']${id}["']`));
}
assert.ok(manifest.host_permissions.includes("http://*/*"), "on-premises HTTP CRM must remain supported");
assert.ok(manifest.host_permissions.includes("https://*/*"), "online and HTTPS CRM must remain supported");
assert.equal(manifest.version, "0.8.0");
assert.match(source, /START_RUNTIME_RECORDING/);
assert.match(source, /STOP_RUNTIME_RECORDING/);
assert.match(source, /RUN_ENHANCED_TOOL/);
assert.match(source, /ui\.chatForm\.requestSubmit\(\);/);
assert.match(source, /const context = await refreshContext\(false\);/);
assert.match(source, /void collectAndUpload\(\{ automatic: true \}\);/);

const start = source.indexOf("function sameLogicScope");
const end = source.indexOf("function delay", start);
assert.notEqual(start, -1);
assert.notEqual(end, -1);

const context = {
  state: { collecting: false, run: null },
  isFailureStatus: value => ["failed", "failure", "error", "cancelled", "canceled"]
    .includes(String(value || "").toLowerCase())
};
vm.createContext(context);
vm.runInContext(
  `${source.slice(start, end)}\nthis.shouldAutoCollect = shouldAutoCollect;`,
  context
);

const current = {
  organizationUrl: "https://crm.example.test/org",
  entityName: "new_ticket",
  formId: "form-1"
};
assert.equal(context.shouldAutoCollect(current), true, "first open should collect");

context.state.run = {
  snapshotId: "snapshot-1",
  status: "Completed",
  context: { ...current }
};
assert.equal(context.shouldAutoCollect(current), false, "completed current form should restore");

assert.equal(
  context.shouldAutoCollect({ ...current, formId: "form-2" }),
  true,
  "a newly opened form should collect"
);

context.state.run.status = "Failed";
assert.equal(context.shouldAutoCollect(current), true, "failed collection should retry on open");

console.log("sidepanel auto-collect tests passed");
