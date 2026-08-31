"use strict";

// Read-only Dynamics form inspectors inspired by Level Up for Dynamics 365/Power Apps.
// This file deliberately excludes God Mode, cloning, impersonation, arbitrary JavaScript,
// and every other capability that can modify CRM data or bypass form behavior.

const ENHANCED_TOOL_DEFINITIONS = Object.freeze([
  {
    id: "form-overview",
    label: "窗体概览",
    description: "统计当前窗体、字段、控件、页签及未保存状态。",
    group: "inspect",
    readsBusinessData: false
  },
  {
    id: "changed-fields",
    label: "已修改字段",
    description: "查看当前记录中尚未保存的字段；只有授权后才返回字段值。",
    group: "inspect",
    readsBusinessData: true
  },
  {
    id: "field-states",
    label: "字段状态",
    description: "查看字段是否可见、只读、必填以及是否已修改。",
    group: "inspect",
    readsBusinessData: false
  },
  {
    id: "option-sets",
    label: "选项集",
    description: "读取当前窗体选项字段的标签和值定义。",
    group: "inspect",
    readsBusinessData: false
  },
  {
    id: "table-processes",
    label: "当前表流程",
    description: "只查询当前实体的工作流、业务规则、Action、业务流程和绑定自定义 API。",
    group: "inspect",
    readsBusinessData: false,
    crmMetadataQuery: true
  },
  {
    id: "forms-monitor",
    label: "Forms Monitor",
    description: "重新载入 CRM，并打开微软内置窗体监视器。",
    group: "diagnostics",
    navigationParameter: "monitor"
  },
  {
    id: "command-checker",
    label: "Command Checker",
    description: "重新载入 CRM，并打开命令栏规则检查器。",
    group: "diagnostics",
    navigationParameter: "ribbondebug"
  },
  {
    id: "performance-center",
    label: "Performance Center",
    description: "重新载入 CRM，并打开客户端性能中心。",
    group: "diagnostics",
    navigationParameter: "perf"
  }
]);

function runEnhancedReadOnlyToolInPage(toolId, includeBusinessValues) {
  const xrm = globalThis.Xrm;
  const page = xrm?.Page;
  if (!page?.data?.entity || !page?.ui) {
    return { ok: false, error: "当前 frame 没有可访问的 Dynamics 记录窗体。" };
  }

  const safeCall = (callback, fallback = null) => {
    try {
      const value = callback();
      return value === undefined ? fallback : value;
    } catch {
      return fallback;
    }
  };
  const asArray = (collection) => {
    const value = safeCall(() => collection?.get?.(), []);
    return Array.isArray(value) ? value : [];
  };
  const cleanGuid = (value) => String(value || "").replace(/[{}]/g, "").toLowerCase() || null;
  const bound = (value, max = 500) => String(value ?? "").replace(/\s+/g, " ").trim().slice(0, max);
  const normalizeValue = (value, depth = 0) => {
    if (value == null || typeof value === "string" || typeof value === "number" || typeof value === "boolean") return value;
    if (value instanceof Date) return value.toISOString();
    if (depth >= 2) return bound(value, 300);
    if (Array.isArray(value)) return value.slice(0, 20).map((item) => normalizeValue(item, depth + 1));
    if (typeof value === "object") {
      const result = {};
      for (const key of ["id", "name", "entityType", "typename", "type"]) {
        if (value[key] != null) result[key] = normalizeValue(value[key], depth + 1);
      }
      return Object.keys(result).length ? result : bound(value, 300);
    }
    return bound(value, 300);
  };

  const attributes = asArray(page.data.entity.attributes).slice(0, 500);
  const controls = asArray(page.ui.controls).slice(0, 800);
  const attributeByName = new Map();
  for (const attribute of attributes) {
    const name = safeCall(() => attribute.getName?.(), null);
    if (name) attributeByName.set(String(name).toLowerCase(), attribute);
  }
  const controlsForAttribute = (name, attribute) => {
    const direct = asArray(attribute?.controls);
    if (direct.length) return direct;
    return controls.filter((control) => {
      const controlAttribute = safeCall(() => control.getAttribute?.(), null);
      return String(safeCall(() => controlAttribute?.getName?.(), "")).toLowerCase() === String(name).toLowerCase();
    });
  };
  const labelFor = (name, attribute) => {
    for (const control of controlsForAttribute(name, attribute)) {
      const label = safeCall(() => control.getLabel?.(), null);
      if (label) return bound(label, 300);
    }
    return name;
  };
  const inspectControl = (control) => ({
    name: safeCall(() => control.getName?.(), null),
    label: safeCall(() => control.getLabel?.(), null),
    type: safeCall(() => control.getControlType?.(), null),
    visible: safeCall(() => control.getVisible?.(), null),
    disabled: safeCall(() => control.getDisabled?.(), null)
  });
  const inspectAttribute = (attribute) => {
    const name = bound(safeCall(() => attribute.getName?.(), ""), 160);
    const result = {
      name,
      label: labelFor(name, attribute),
      type: safeCall(() => attribute.getAttributeType?.(), null),
      format: safeCall(() => attribute.getFormat?.(), null),
      requiredLevel: safeCall(() => attribute.getRequiredLevel?.(), null),
      submitMode: safeCall(() => attribute.getSubmitMode?.(), null),
      dirty: Boolean(safeCall(() => attribute.getIsDirty?.(), false)),
      controls: controlsForAttribute(name, attribute).slice(0, 10).map(inspectControl)
    };
    if (includeBusinessValues) {
      result.value = normalizeValue(safeCall(() => attribute.getValue?.(), null));
      result.text = normalizeValue(safeCall(() => attribute.getText?.(), null));
    }
    return result;
  };

  if (toolId === "form-overview") {
    const tabs = asArray(page.ui.tabs);
    const sections = tabs.flatMap((tab) => asArray(tab.sections));
    const changedCount = attributes.filter((attribute) => Boolean(safeCall(() => attribute.getIsDirty?.(), false))).length;
    const hiddenCount = controls.filter((control) => safeCall(() => control.getVisible?.(), true) === false).length;
    const disabledCount = controls.filter((control) => safeCall(() => control.getDisabled?.(), false) === true).length;
    const globalContext = safeCall(() => xrm.Utility?.getGlobalContext?.(), null);
    return {
      ok: true,
      toolId,
      title: "当前窗体概览",
      data: {
        entityName: safeCall(() => page.data.entity.getEntityName?.(), null),
        entityId: cleanGuid(safeCall(() => page.data.entity.getId?.(), null)),
        formType: safeCall(() => page.ui.getFormType?.(), null),
        formDirty: Boolean(safeCall(() => page.data.entity.getIsDirty?.(), false)),
        attributeCount: attributes.length,
        controlCount: controls.length,
        changedFieldCount: changedCount,
        hiddenControlCount: hiddenCount,
        disabledControlCount: disabledCount,
        tabCount: tabs.length,
        sectionCount: sections.length,
        client: safeCall(() => globalContext?.client?.getClient?.(), null),
        clientState: safeCall(() => globalContext?.client?.getClientState?.(), null),
        crmVersion: safeCall(() => globalContext?.getVersion?.(), null)
      }
    };
  }

  if (toolId === "changed-fields") {
    const fields = attributes
      .filter((attribute) => Boolean(safeCall(() => attribute.getIsDirty?.(), false)))
      .map(inspectAttribute);
    return {
      ok: true,
      toolId,
      title: fields.length ? `${fields.length} 个未保存字段` : "没有未保存字段",
      valuesIncluded: Boolean(includeBusinessValues),
      data: fields
    };
  }

  if (toolId === "field-states") {
    const fields = attributes.map(inspectAttribute);
    return {
      ok: true,
      toolId,
      title: `${fields.length} 个窗体字段状态`,
      valuesIncluded: Boolean(includeBusinessValues),
      data: fields
    };
  }

  if (toolId === "option-sets") {
    const fields = [];
    let optionBudget = 1200;
    for (const control of controls) {
      const type = String(safeCall(() => control.getControlType?.(), "")).toLowerCase();
      if (type !== "optionset" && type !== "multiselectoptionset") continue;
      const name = bound(safeCall(() => control.getName?.(), ""), 160);
      const attribute = safeCall(() => control.getAttribute?.(), null) || attributeByName.get(name.toLowerCase());
      const rawOptions = safeCall(() => control.getOptions?.(), []);
      const options = (Array.isArray(rawOptions) ? rawOptions : []).slice(0, Math.max(0, optionBudget)).map((option) => ({
        value: option?.value ?? null,
        text: bound(option?.text, 300)
      }));
      optionBudget -= options.length;
      const field = {
        name,
        label: bound(safeCall(() => control.getLabel?.(), name), 300),
        type,
        options
      };
      if (includeBusinessValues) {
        field.selectedValue = normalizeValue(safeCall(() => attribute?.getValue?.(), null));
        field.selectedText = normalizeValue(safeCall(() => attribute?.getText?.(), null));
      }
      fields.push(field);
      if (optionBudget <= 0) break;
    }
    return {
      ok: true,
      toolId,
      title: fields.length ? `${fields.length} 个选项字段` : "当前窗体没有选项字段",
      valuesIncluded: Boolean(includeBusinessValues),
      truncated: optionBudget <= 0,
      data: fields
    };
  }

  return { ok: false, error: "不支持的增强工具。" };
}

globalThis.CRM_LOGIC_LENS_ENHANCED_TOOLS = Object.freeze({
  definitions: ENHANCED_TOOL_DEFINITIONS,
  runInPage: runEnhancedReadOnlyToolInPage
});
