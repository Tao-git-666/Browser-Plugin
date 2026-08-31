const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

class FakeElement {
  constructor(tagName) {
    this.tagName = tagName.toUpperCase();
    this.children = [];
    this.className = "";
    this.textContent = "";
    this.title = "";
  }

  append(...children) {
    this.children.push(...children);
  }
}

const document = {
  createElement: tagName => new FakeElement(tagName),
  createTextNode: text => ({ tagName: "#TEXT", textContent: text })
};

const sourcePath = path.resolve(__dirname, "../../src/CrmLogicLens.Extension/sidepanel.js");
const source = fs.readFileSync(sourcePath, "utf8");
const start = source.indexOf("function renderBusinessAnswer");
const end = source.indexOf("async function saveSettings", start);
assert.notEqual(start, -1);
assert.notEqual(end, -1);

const context = { document };
vm.createContext(context);
vm.runInContext(`${source.slice(start, end)}\nthis.renderBusinessAnswer = renderBusinessAnswer;`, context);

const container = new FakeElement("div");
context.renderBusinessAnswer(
  container,
  "这个字段不能编辑，是因为当前记录已经不是草稿。\n\n- 改回草稿后会恢复编辑\n- 如果仍不能编辑，再检查字段权限\n\n## 为什么\n窗体脚本会根据方案状态切换只读。",
  [{ nodeId: "js:1", label: "字段只读逻辑" }]
);

assert.equal(container.children.length, 1);
const article = container.children[0];
assert.equal(article.className, "business-answer");
const natural = article.children[0];
assert.equal(natural.className, "natural-answer");
assert.deepEqual(
  Array.from(natural.children, child => child.tagName),
  ["P", "UL", "H3", "P"]
);
assert.equal(article.children[1].className, "technical-details");

const tracedContainer = new FakeElement("div");
context.renderBusinessAnswer(
  tracedContainer,
  "字段因为状态限制而隐藏。",
  [{ nodeId: "field:1", label: "字段显示规则" }],
  [
    { sequence: 1, title: "限定分析范围", summary: "只检查当前窗体。", status: "completed" },
    { sequence: 2, title: "采用诊断 Skill", summary: "读取 field-not-visible 的必查项和分支条件。", toolName: "read_diagnostic_skill", status: "completed" },
    { sequence: 3, title: "读取相关窗体脚本", summary: "取得 2 条证据。", toolName: "read_javascript_function", status: "completed", durationMs: 1250 },
    { sequence: 4, title: "检查 Skill 必查项", summary: "Skill 的必查项已全部完成。", toolName: "check_diagnostic_progress", status: "completed" }
  ]
);
const tracedArticle = tracedContainer.children[0];
assert.equal(tracedArticle.children[1].className, "analysis-trace-details");
assert.equal(tracedArticle.children[2].className, "technical-details");
assert.equal(tracedArticle.children[1].children[1].className, "analysis-trace-list");
assert.equal(tracedArticle.children[1].children[1].children.length, 4);
assert.equal(tracedArticle.children[1].children[1].children[1].children[1].children[0].children[0].textContent, "采用诊断 Skill");
assert.doesNotMatch(source, /label:\s*"什么时候发生"/);
assert.doesNotMatch(source, /label:\s*"系统做了什么"/);
assert.doesNotMatch(source, /label:\s*"现在还看不出来的"/);

console.log("sidepanel natural answer tests passed");
