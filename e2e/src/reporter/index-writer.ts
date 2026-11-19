import fs from "node:fs";
import path from "node:path";
import { paths } from "../../config/index.js";
import type { RunReport } from "./run-reporter.js";

const HEADER = `# Run index

Every run of the harness appends one row. Reports live next to this file.

| When (local) | Suite | Target | Result | Spend | Report | Note |
|---|---|---|---|---|---|---|
`;

export function appendIndexRow(r: RunReport) {
  const file = path.join(paths.runsDir, "INDEX.md");
  if (!fs.existsSync(file)) fs.writeFileSync(file, HEADER, "utf8");
  const when = new Date(r.startedAt).toLocaleString("sv-SE").slice(0, 16);
  const result = `${r.totals.passed}/${r.cases.length} passed${r.totals.failed + r.totals.timedOut ? `, ${r.totals.failed + r.totals.timedOut} failed` : ""}${r.totals.skipped ? `, ${r.totals.skipped} skipped` : ""}`;
  const note = r.notes[0] ?? "";
  // A file whose last row lost its newline (an editor, a checkout) would glue this row onto it.
  const existing = fs.readFileSync(file, "utf8");
  const lead = existing.length && !existing.endsWith("\n") ? "\n" : "";
  // The Spend column is kept for the historical rows (prod cloud runs); runner-only runs have none.
  fs.appendFileSync(file, `${lead}| ${when} | ${r.suite} | ${r.target} | ${result} | — | [${r.runId}](${r.runId}/report.md) | ${note.replace(/\|/g, "/")} |\n`, "utf8");
}
