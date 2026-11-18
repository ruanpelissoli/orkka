const t0 = Date.now();

function ts() {
  const s = ((Date.now() - t0) / 1000).toFixed(1).padStart(7);
  return `[+${s}s]`;
}

export const log = {
  info: (msg: string, ...rest: unknown[]) => console.log(`${ts()} ${msg}`, ...rest),
  warn: (msg: string, ...rest: unknown[]) => console.warn(`${ts()} WARN ${msg}`, ...rest),
  error: (msg: string, ...rest: unknown[]) => console.error(`${ts()} ERROR ${msg}`, ...rest),
  step: (msg: string) => console.log(`${ts()} ── ${msg}`),
};

/** Exit code reserved for "operator action required" (expired login, missing env, etc.). */
export const EXIT_OPERATOR_ACTION = 3;

export function operatorAction(message: string): never {
  console.error(`\nOPERATOR ACTION REQUIRED: ${message}\n`);
  process.exit(EXIT_OPERATOR_ACTION);
}

/** Exit code for "nothing to check here" (no environment configured). */
export const EXIT_SKIPPED = 0;
