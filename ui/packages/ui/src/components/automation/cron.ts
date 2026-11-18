/** Cron helpers for the Automation page: presets ⇄ 5-field cron, and a small humanizer. */

export type CronPreset = "hourly" | "daily" | "weekdays" | "weekly" | "custom";

export interface CronDraft {
  preset: CronPreset;
  /** "HH:MM" for daily/weekdays/weekly. */
  time: string;
  /** 0–6 (Sunday = 0) for weekly. */
  weekday: number;
  /** Raw expression for custom. */
  raw: string;
}

export const DEFAULT_CRON_DRAFT: CronDraft = { preset: "daily", time: "09:00", weekday: 1, raw: "0 9 * * *" };

export function draftToCron(d: CronDraft): string {
  const [h, m] = parseTime(d.time);
  switch (d.preset) {
    case "hourly":
      return "0 * * * *";
    case "daily":
      return `${m} ${h} * * *`;
    case "weekdays":
      return `${m} ${h} * * 1-5`;
    case "weekly":
      return `${m} ${h} * * ${d.weekday}`;
    case "custom":
      return d.raw.trim();
  }
}

/** Best-effort inverse: recognizes the shapes the presets emit, else "custom". */
export function cronToDraft(cron: string | null | undefined): CronDraft {
  if (!cron) return DEFAULT_CRON_DRAFT;
  const parts = cron.trim().split(/\s+/);
  if (parts.length !== 5) return { ...DEFAULT_CRON_DRAFT, preset: "custom", raw: cron };
  const [min, hour, dom, mon, dow] = parts as [string, string, string, string, string];
  const time = /^\d+$/.test(min) && /^\d+$/.test(hour) ? `${pad(hour)}:${pad(min)}` : "09:00";
  if (min === "0" && hour === "*" && dom === "*" && mon === "*" && dow === "*")
    return { preset: "hourly", time: "09:00", weekday: 1, raw: cron };
  if (dom === "*" && mon === "*" && /^\d+$/.test(min) && /^\d+$/.test(hour)) {
    if (dow === "*") return { preset: "daily", time, weekday: 1, raw: cron };
    if (dow === "1-5") return { preset: "weekdays", time, weekday: 1, raw: cron };
    if (/^[0-6]$/.test(dow)) return { preset: "weekly", time, weekday: Number(dow), raw: cron };
  }
  return { preset: "custom", time, weekday: 1, raw: cron };
}

const DAYS = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

/** "Daily at 09:00", "Weekdays at 18:30", "Weekly on Monday at 09:00", "Hourly", else the raw cron. */
export function humanizeCron(cron: string | null | undefined): string {
  if (!cron) return "";
  const d = cronToDraft(cron);
  switch (d.preset) {
    case "hourly":
      return "Hourly";
    case "daily":
      return `Daily at ${d.time}`;
    case "weekdays":
      return `Weekdays at ${d.time}`;
    case "weekly":
      return `Weekly on ${DAYS[d.weekday]} at ${d.time}`;
    default:
      return cron;
  }
}

export function browserTimeZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC";
  } catch {
    return "UTC";
  }
}

function parseTime(t: string): [number, number] {
  const m = /^(\d{1,2}):(\d{2})$/.exec(t.trim());
  if (!m) return [9, 0];
  const h = Math.min(23, Math.max(0, Number(m[1])));
  const min = Math.min(59, Math.max(0, Number(m[2])));
  return [h, min];
}

function pad(v: string): string {
  return v.length === 1 ? `0${v}` : v;
}

export { DAYS as WEEKDAY_NAMES };

/** "in 3h", "in 2d", "tomorrow 09:00"-ish relative phrasing for a future instant; past → "now". */
export function formatUntil(iso: string): string {
  const diff = new Date(iso).getTime() - Date.now();
  if (diff <= 0) return "now";
  const m = Math.round(diff / 60000);
  if (m < 60) return `in ${m}m`;
  const h = Math.round(m / 60);
  if (h < 48) return `in ${h}h`;
  return `in ${Math.round(h / 24)}d`;
}
