"use strict";

(() => {
  const installedKey = "__crmLogicLensEarlyQueryHookInstalledV1";
  const bufferKey = "__crmLogicLensDataverseQueryBufferV1";
  if (window[installedKey]) return;
  window[installedKey] = true;
  window.__crmLogicLensEarlyQueryHookActiveV1 = true;
  const buffer = Array.isArray(window[bufferKey]) ? window[bufferKey] : [];
  window[bufferKey] = buffer;

  const dataverseUrl = (rawUrl) => {
    try {
      const url = new URL(String(rawUrl || ""), window.location.href);
      return url.origin === window.location.origin && /\/api\/data\/v\d+\.\d+\//i.test(url.pathname)
        ? url
        : null;
    } catch {
      return null;
    }
  };

  const sanitize = (name, value) => {
    let safe = String(value || "").slice(0, 12000);
    if (String(name).toLowerCase() === "fetchxml") {
      return safe
        .replace(/(\bvalue(?:of)?\s*=\s*)(["'])[^"']*\2/gi, "$1$2<value>$2")
        .replace(/\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/gi, "<guid>")
        .slice(0, 7000);
    }
    return safe
      .replace(/\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/gi, "<guid>")
      .replace(/(\b(?:eq|ne|gt|ge|lt|le)\s+)(?:guid'[^']*'|'(?:''|[^'])*'|-?\d+(?:\.\d+)?)/gi, "$1<value>")
      .replace(/(['"])(?:https?:\/\/|mailto:)[^'"]+\1/gi, "$1<url>$1")
      .slice(0, 7000);
  };

  const record = (method, rawUrl, status, responseBody, durationMs) => {
    try {
      if (window.__crmLogicLensEarlyQueryHookActiveV1 !== true) return;
      if (String(method || "GET").toUpperCase() !== "GET" || buffer.length >= 60) return;
      const url = dataverseUrl(rawUrl);
      if (!url) return;
      const match = url.pathname.match(/\/api\/data\/v\d+\.\d+\/(.*)$/i);
      const entitySet = String(match?.[1] || "").split(/[/(]/, 1)[0].slice(0, 256);
      const queryOptions = {};
      let remaining = 6800;
      for (const [name, value] of url.searchParams.entries()) {
        const normalized = name.toLowerCase();
        if (!["$select", "$filter", "$expand", "$orderby", "$top", "fetchxml", "savedquery", "userquery"].includes(normalized)) continue;
        const safe = sanitize(normalized, value).slice(0, Math.max(0, remaining));
        if (!safe) continue;
        queryOptions[normalized] = safe;
        remaining -= safe.length;
        if (remaining <= 0) break;
      }
      let resultCount = null;
      if (responseBody != null && String(responseBody).length <= 2_000_000) {
        try {
          const payload = typeof responseBody === "string" ? JSON.parse(responseBody) : responseBody;
          if (Array.isArray(payload?.value)) resultCount = payload.value.length;
          else if (payload && typeof payload === "object" && !payload.error) resultCount = 1;
        } catch { /* optional */ }
      }
      buffer.push({
        kind: "dataverse-query",
        summary: `查询 ${entitySet || "Dataverse 数据"}${Number.isInteger(resultCount) ? `，返回 ${resultCount} 条` : ""}`.slice(0, 500),
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
        status: Number(status) || 0
      });
    } catch {
      // The hook must remain invisible to page behavior.
    }
  };

  const originalFetch = window.fetch;
  if (typeof originalFetch === "function") {
    window.fetch = async function (...args) {
      const startedAt = performance.now();
      const response = await Reflect.apply(originalFetch, this, args);
      const input = args[0];
      const method = args[1]?.method || input?.method || "GET";
      const url = typeof input === "string" ? input : input?.url;
      if (String(method).toUpperCase() === "GET" && dataverseUrl(url)) {
        const size = Number(response.headers?.get?.("content-length") || 0);
        const body = size <= 2_000_000
          ? response.clone().text().catch(() => "")
          : Promise.resolve("");
        void body.then(text => record(method, url, response.status, text, performance.now() - startedAt));
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
        if (!request || String(request.method).toUpperCase() !== "GET") return;
        let body = "";
        try {
          body = this.responseType === "json"
            ? JSON.stringify(this.response)
            : this.responseType === "" || this.responseType === "text"
              ? this.responseText
              : "";
        } catch { /* optional */ }
        record(request.method, request.url, this.status, body, performance.now() - request.startedAt);
      }, { once: true });
      return Reflect.apply(originalSend, this, args);
    };
  }
})();
