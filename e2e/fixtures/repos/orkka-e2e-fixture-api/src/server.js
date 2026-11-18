import http from "node:http";
import { features } from "./features.js";

const port = Number(process.env.PORT ?? 4000);

export function handle(req, res) {
  const url = new URL(req.url ?? "/", "http://localhost");
  res.setHeader("content-type", "application/json");
  if (url.pathname === "/health") return res.end(JSON.stringify({ ok: true }));
  if (url.pathname === "/features") return res.end(JSON.stringify({ features }));
  res.statusCode = 404;
  res.end(JSON.stringify({ error: "not found" }));
}

if (process.argv[1] && process.argv[1].endsWith("server.js")) {
  http.createServer(handle).listen(port, "127.0.0.1", () => console.log(`fixture api listening on http://127.0.0.1:${port}`));
}
