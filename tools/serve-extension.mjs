import http from "node:http";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../src/CrmLogicLens.Extension");
const port = Number(process.argv[2] || 4173);
const mediaTypes = new Map([
  [".html", "text/html; charset=utf-8"],
  [".css", "text/css; charset=utf-8"],
  [".js", "application/javascript; charset=utf-8"],
  [".json", "application/json; charset=utf-8"]
]);

http.createServer(async (request, response) => {
  try {
    const pathname = new URL(request.url, "http://localhost").pathname;
    const relative = pathname === "/" ? "sidepanel.html" : decodeURIComponent(pathname).replace(/^\/+/, "");
    const target = path.resolve(root, relative);
    if (target !== root && !target.startsWith(`${root}${path.sep}`)) {
      response.writeHead(403).end("Forbidden");
      return;
    }
    const content = await readFile(target);
    response.writeHead(200, {
      "Content-Type": mediaTypes.get(path.extname(target).toLowerCase()) || "application/octet-stream",
      "Cache-Control": "no-store"
    });
    response.end(content);
  } catch {
    response.writeHead(404).end("Not Found");
  }
}).listen(port, "127.0.0.1", () => {
  console.log(`Extension preview: http://127.0.0.1:${port}/sidepanel.html`);
});
