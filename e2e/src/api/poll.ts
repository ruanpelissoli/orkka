import { sleep } from "./http.js";

export class PollTimeout<T> extends Error {
  constructor(
    label: string,
    public readonly lastValue: T | undefined,
    public readonly elapsedMs: number,
  ) {
    super(`Timed out after ${Math.round(elapsedMs / 1000)}s waiting for: ${label}. Last value: ${safeJson(lastValue)}`);
    this.name = "PollTimeout";
  }
}
export class PollFailed<T> extends Error {
  constructor(
    label: string,
    reason: string,
    public readonly lastValue: T | undefined,
  ) {
    super(`Failed while waiting for: ${label}. ${reason}. Last value: ${safeJson(lastValue)}`);
    this.name = "PollFailed";
  }
}

export interface PollOptions<T> {
  label: string;
  deadlineMs: number;
  intervalMs?: number;
  fetch: () => Promise<T>;
  done: (v: T) => boolean;
  /** Return a string to abort early with that reason. */
  fail?: (v: T) => string | undefined;
  onTick?: (v: T, elapsedMs: number) => void;
  /** Swallow transient fetch errors up to this many in a row (default 5). */
  tolerateErrors?: number;
}

export async function pollUntil<T>(opts: PollOptions<T>): Promise<T> {
  const started = Date.now();
  const interval = opts.intervalMs ?? 5000;
  let last: T | undefined;
  let errorsInARow = 0;
  for (;;) {
    try {
      last = await opts.fetch();
      errorsInARow = 0;
      const elapsed = Date.now() - started;
      opts.onTick?.(last, elapsed);
      const failReason = opts.fail?.(last);
      if (failReason) throw new PollFailed(opts.label, failReason, last);
      if (opts.done(last)) return last;
    } catch (e) {
      if (e instanceof PollFailed) throw e;
      errorsInARow++;
      if (errorsInARow > (opts.tolerateErrors ?? 5)) throw e;
    }
    const elapsed = Date.now() - started;
    if (elapsed + interval > opts.deadlineMs) throw new PollTimeout(opts.label, last, elapsed);
    await sleep(interval);
  }
}

export function safeJson(v: unknown): string {
  try {
    const s = JSON.stringify(v);
    return s.length > 600 ? s.slice(0, 600) + "…" : s;
  } catch {
    return String(v);
  }
}
