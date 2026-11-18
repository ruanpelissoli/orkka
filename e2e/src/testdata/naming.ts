import { cfg } from "../../config/index.js";
import { rand, stamp } from "../util/fs.js";

/**
 * `e2e-<suite>-<yyyymmdd-HHmm>-<rand>` — the prefix is what the delete guard checks.
 * Never 26 characters: the API's project resolver tries `Ulid.TryParse` before the slug lookup and a
 * 26-char slug is swallowed as a (garbage) ULID → 404 (observed in prod 2026-09-15, see NEG-030).
 */
export function e2eName(suite: string): string {
  let name = `${cfg.projectPrefix}${suite}-${stamp()}-${rand(4)}`;
  while (name.length === 26) name += rand(1);
  return name;
}

/** A unique token an agent can be asked to write into the fixture so the diff is verifiable. */
export function featureToken(): string {
  return `e2e-${stamp()}-${rand(5)}`;
}
