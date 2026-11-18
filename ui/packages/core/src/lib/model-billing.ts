/**
 * Runner-aware model availability: joins the model catalog with "which AI subscriptions does
 * the user's runner have signed in" and answers, per model, whether the developer's runner can
 * run it now. Every task runs on its owner's runner, on their own subscription.
 *
 * Pure TypeScript on purpose — no React, no use-intl: plain functions any caller (and any
 * test) can import without a DOM. Every string here is a KEY (reason codes, CLI
 * labels); the callers translate.
 *
 * Mirrors the backend seam that reads runner coverage at task creation
 * (`IRunnerCoverageResolver`): same readiness rule, same provider order. Keep the two in step.
 */
import type { AiModel } from "./types";
import { providerFromModelId } from "./utils";

/** Runner CLI keys, as the runner reports them in `RunnerProviderStatus.provider`. */
export type RunnerCli =
  | "claude"
  | "codex"
  | "gemini"
  | "grok"
  | "copilot"
  | "opencode"
  | "ollama";

/** AiProvider enum name → runner CLI key (mirrors backend RunnerProviders). Stays client-side:
 * this maps the catalog's providers onto the RUNNER's authentication keys, which the model
 * catalog knows nothing about. */
export const PROVIDER_TO_CLI: Readonly<Record<string, RunnerCli>> = {
  Anthropic: "claude",
  OpenAI: "codex",
  Google: "gemini",
  GitHubCopilot: "copilot",
  Ollama: "ollama",
  XAI: "grok",
  OpenCode: "opencode",
};

/** Runner CLI key → AiProvider enum name. */
export const CLI_TO_PROVIDER: Readonly<Record<RunnerCli, string>> = {
  claude: "Anthropic",
  codex: "OpenAI",
  gemini: "Google",
  copilot: "GitHubCopilot",
  ollama: "Ollama",
  grok: "XAI",
  opencode: "OpenCode",
};

/**
 * Which ready provider to steer a team toward when several are — mirrors the backend
 * `ProviderPreference.Order` (keep in step): copilot late — metered quota; opencode
 * second-to-last — it is a harness, so auto-picking it silently picks whichever vendor its
 * default points at; ollama last — local models are the weakest auto-pick.
 */
export const RUNNER_PROVIDER_PREFERENCE: readonly RunnerCli[] = [
  "claude",
  "codex",
  "gemini",
  "grok",
  "copilot",
  "opencode",
  "ollama",
];

/** Display names for the runner's CLI keys (matches the backend enum). Product names, not
 * translated. */
export const CLI_PROVIDER_LABELS: Readonly<Record<RunnerCli, string>> = {
  claude: "Claude",
  codex: "Codex",
  gemini: "Gemini",
  copilot: "Copilot",
  ollama: "Ollama",
  grok: "Grok",
  opencode: "OpenCode",
};

export function cliProviderLabel(cli: string): string {
  return (CLI_PROVIDER_LABELS as Record<string, string>)[cli] ?? cli;
}

/** The catalog's provider name for a runner CLI key (unknown keys pass through unchanged). */
export function providerForCli(cli: string): string {
  return (CLI_TO_PROVIDER as Record<string, string>)[cli] ?? cli;
}

/** The runner CLI key for a catalog provider (unknown providers map to themselves lowercased
 * so a provider the runner has never heard of can never accidentally match one it has). */
export function cliForProvider(provider: string): string {
  return (PROVIDER_TO_CLI as Record<string, string>)[provider] ?? provider.toLowerCase();
}

/** The structural subset of `RunnerProviderStatus` this module reads. */
export interface ProviderStatusLike {
  provider: string;
  cliFound: boolean;
  authenticated: boolean;
  models?: string[] | null;
}

/**
 * A provider is ready when BOTH halves hold: credentials are a file check that passes even
 * when the CLI isn't on the runner's PATH, so `authenticated` alone once showed a provider
 * as usable on a machine that failed every task with "Claude Code not found".
 */
export function isProviderReady(p: ProviderStatusLike): boolean {
  return p.cliFound && p.authenticated;
}

/**
 * Ready providers in `RUNNER_PROVIDER_PREFERENCE` order (unknown keys sort last, stable).
 * Generic so callers that render the status rows keep their own element type.
 */
export function readyProviders<T extends ProviderStatusLike>(
  providers: readonly T[] | null | undefined
): T[] {
  const rank = (p: ProviderStatusLike) => {
    const i = RUNNER_PROVIDER_PREFERENCE.indexOf(p.provider as RunnerCli);
    return i === -1 ? RUNNER_PROVIDER_PREFERENCE.length : i;
  };
  return (providers ?? [])
    .filter(isProviderReady)
    .map((p, index) => ({ p, index }))
    .sort((a, b) => rank(a.p) - rank(b.p) || a.index - b.index)
    .map(({ p }) => p);
}

/** What the app knows about the user's runner — the live Hello when online, the last Hello
 * the backend snapshotted when not. */
export interface RunnerCoverageInput {
  online: boolean;
  providers: readonly ProviderStatusLike[] | null | undefined;
  /** The provider rows from the runner's last Hello before it went offline. */
  lastKnownProviders?: readonly ProviderStatusLike[] | null;
  /** When that last Hello was received (ISO). */
  lastKnownAt?: string | null;
}

export interface RunnerCoverage {
  online: boolean;
  /** CLI keys whose provider is ready (two-part rule). */
  ready: Set<string>;
  /** True when `ready` comes from the last-known snapshot of an offline runner. */
  stale: boolean;
  /** As of when the coverage was observed — null when live. */
  asOf: string | null;
  /**
   * Per CLI key, the model ids the runner reported it can run. Only the providers the catalog
   * cannot enumerate populate this: Ollama (the tags pulled locally) and OpenCode (whatever
   * its authenticated providers expose — it ships no models of its own).
   */
  discovered: Record<string, string[]>;
}

/**
 * Live providers when the runner is online; the last-known snapshot (flagged `stale`) when
 * it is offline but has connected before; null when nothing is known — no status loaded, or
 * an offline runner with no snapshot.
 */
export function runnerCoverage(
  input: RunnerCoverageInput | null | undefined
): RunnerCoverage | null {
  if (!input) return null;
  const live = input.online;
  const source = live ? (input.providers ?? []) : input.lastKnownProviders;
  if (!live && !source) return null;

  const rows = source ?? [];
  const discovered: Record<string, string[]> = {};
  for (const p of rows) {
    if (DISCOVERED_PROVIDERS.some((d) => d.cli === p.provider) && p.models?.length) {
      discovered[p.provider] = [...p.models];
    }
  }
  return {
    online: live,
    ready: new Set(rows.filter(isProviderReady).map((p) => p.provider)),
    stale: !live,
    asOf: live ? null : (input.lastKnownAt ?? null),
    discovered,
  };
}

/** Whether a task on this model can run on the developer's runner. */
export type ModelBilling = "runner" | "unavailable";

/**
 * Why — a key the UI translates. `runnerReady*` ⇒ runs now on the user's subscription;
 * `runnerOffline` ⇒ the runner once it connects (coverage unknown); `runnerWaitingSignIn` ⇒
 * the runner, but it waits until the provider's CLI is signed in there; `runnerOnlyNotReady`
 * ⇒ a model the runner only discovers locally (Ollama, OpenCode, Copilot) that it does not
 * have right now.
 */
export type ModelBillingReason =
  | "runnerReady"
  | "runnerReadyStale"
  | "runnerOffline"
  | "runnerWaitingSignIn"
  | "runnerOnlyNotReady";

export interface ModelBillingDecision {
  billing: ModelBilling;
  reason: ModelBillingReason;
}

export interface AnnotatedModel extends AiModel {
  /** Runner CLI key for the model's provider. */
  cli: string;
  /** The provider is ready on the runner (live or last-known). */
  connected: boolean;
  billing: ModelBilling;
  reason: ModelBillingReason;
}

export interface AnnotateOptions {
  /** The agent's or task's currently saved model — always listed so a form never silently
   * rewrites it, even when it could not run today. */
  currentModel?: string | null;
}

/** Bare Ollama tag ("qwen3-coder:30b") → display name ("Qwen3 Coder 30B"). */
export function ollamaModelName(id: string): string {
  const tag = id.replace(/^ollama\//i, "");
  const [model, variant] = tag.split(":");
  const pretty = model
    .split(/[-_]/)
    .map((part) =>
      /\d/.test(part) ? part.toUpperCase() : part.charAt(0).toUpperCase() + part.slice(1)
    )
    .join(" ");
  return variant && variant !== "latest" ? `${pretty} ${variant.toUpperCase()}` : pretty;
}

/** "opencode/anthropic/claude-sonnet-4-6" → "Anthropic · claude-sonnet-4-6". The model id is
 * left VERBATIM: OpenCode reaches ~75 vendors and their ids are what users recognise —
 * prettifying "claude-sonnet-4-6" into "Claude Sonnet 4 6" would only obscure it. */
export function openCodeModelName(id: string): string {
  const pair = id.replace(/^opencode\//i, "");
  const slash = pair.indexOf("/");
  if (slash <= 0) return pair;
  const vendor = pair.slice(0, slash);
  return `${vendor.charAt(0).toUpperCase() + vendor.slice(1)} · ${pair.slice(slash + 1)}`;
}

/**
 * Providers whose selectable models are DISCOVERED on the runner instead of coming from the
 * catalog — mirrors the backend's RunnerProviders.DiscoversModels. Ollama reports the tags
 * actually pulled; OpenCode ships no models at all and reports whatever its
 * `opencode auth login` providers expose. Only the user's machine knows the answer.
 */
const DISCOVERED_PROVIDERS = [
  {
    provider: "Ollama",
    cli: "ollama",
    prefix: "ollama/",
    label: (id: string) => `${ollamaModelName(id)} (Ollama)`,
    present: "Installed locally via Ollama",
    absent: "Not currently pulled in Ollama",
  },
  {
    provider: "OpenCode",
    cli: "opencode",
    prefix: "opencode/",
    label: (id: string) => `${openCodeModelName(id)} (OpenCode)`,
    present: "Available through your OpenCode provider logins",
    absent: "Not available from your current OpenCode logins",
  },
] as const;

type DiscoveredProvider = (typeof DISCOVERED_PROVIDERS)[number];

/** The discovering provider a model id belongs to, by its namespace prefix. */
export function discoveredProviderFor(modelId: string): DiscoveredProvider | undefined {
  const id = modelId.toLowerCase();
  return DISCOVERED_PROVIDERS.find((p) => id.startsWith(p.prefix));
}

/** A synthesized catalog row for a discovered id the catalog doesn't list. */
function discoveredEntry(source: DiscoveredProvider, id: string, description: string): AiModel {
  return {
    id,
    name: source.label(id),
    provider: source.provider,
    kind: "Chat",
    description,
    supportsMcp: false,
    isEnabled: true,
    requiresRunner: true,
    sortOrder: 9999,
  };
}

/** Providers the catalog cannot vouch for, for ids it doesn't list: only the runner knows
 * whether they exist, so an unready one is hidden rather than offered as "waiting". */
const RUNNER_ONLY_FALLBACK = new Set<string>(["copilot", "ollama", "opencode"]);

function decide(
  cli: string,
  runnerOnly: boolean,
  coverage: RunnerCoverage | null
): ModelBillingDecision & { listed: boolean } {
  const ready = coverage?.ready.has(cli) ?? false;

  if (ready) {
    return {
      billing: "runner",
      reason: coverage?.stale ? "runnerReadyStale" : "runnerReady",
      listed: true,
    };
  }
  if (!runnerOnly) {
    // Coverage known (live, or a last-known snapshot) says the provider isn't signed in: the
    // task is still created on the runner and waits until the developer signs that CLI in.
    // No coverage at all means the runner has never told us: the task waits for it to connect.
    return coverage
      ? { billing: "runner", reason: "runnerWaitingSignIn", listed: true }
      : { billing: "runner", reason: "runnerOffline", listed: true };
  }
  return { billing: "unavailable", reason: "runnerOnlyNotReady", listed: false };
}

/**
 * The chat catalog joined with runner coverage: every model that should appear in a picker,
 * annotated with whether the runner can run it and why. Hidden rows (locally-discovered
 * models the runner does not have) are dropped — except `currentModel`, which always
 * stays so a form never silently rewrites a saved value.
 *
 * Ollama and OpenCode are special: once coverage is known their catalog anchors are
 * replaced by whatever the runner reports (catalog metadata kept when ids match). A saved
 * discovered id the runner no longer reports stays listed, unavailable.
 */
export function annotateModels(
  catalog: readonly AiModel[] | null | undefined,
  coverage: RunnerCoverage | null,
  options: AnnotateOptions = {}
): AnnotatedModel[] {
  const currentModel = options.currentModel?.trim() || null;
  const currentLower = currentModel?.toLowerCase() ?? null;
  const isCurrent = (id: string) => currentLower !== null && id.toLowerCase() === currentLower;

  // Image rows live in the same catalog but are picked by provider key elsewhere — they must
  // never appear in a chat-model select. Retired models are dropped too, except the saved one.
  const chat = (catalog ?? []).filter(
    (m) => m.kind === "Chat" && (m.isEnabled || isCurrent(m.id))
  );

  let rows: AiModel[];
  if (coverage) {
    const anchors = chat.filter(
      (m) => !DISCOVERED_PROVIDERS.some((p) => p.provider === m.provider)
    );
    const discovered = DISCOVERED_PROVIDERS.flatMap((source) =>
      (coverage.discovered[source.cli] ?? []).map(
        (id) =>
          chat.find((m) => m.id.toLowerCase() === id.toLowerCase()) ??
          discoveredEntry(source, id, source.present)
      )
    );
    rows = [...anchors, ...discovered];
    const savedSource = currentModel ? discoveredProviderFor(currentModel) : undefined;
    if (currentModel && savedSource && !rows.some((m) => isCurrent(m.id))) {
      rows.push(discoveredEntry(savedSource, currentModel, savedSource.absent));
    }
  } else {
    rows = chat;
  }

  const annotated: AnnotatedModel[] = [];
  for (const m of rows) {
    const cli = cliForProvider(m.provider);
    const { billing, reason, listed } = decide(cli, m.requiresRunner, coverage);
    const current = isCurrent(m.id);
    // Locally-discovered models the runner does not have are hidden.
    if (!listed && !current) continue;
    annotated.push({
      ...m,
      cli,
      connected: coverage?.ready.has(cli) ?? false,
      billing,
      reason,
    });
  }
  return annotated;
}

/**
 * Billing for one model id — an agent default or a saved value — looked up in an annotated
 * list, falling back to the id-prefix provider rule for ids the catalog can't enumerate.
 */
export function billingForModel(
  modelId: string,
  annotated: readonly AnnotatedModel[] | null | undefined,
  coverage: RunnerCoverage | null
): ModelBillingDecision {
  const id = modelId.trim().toLowerCase();
  const known = annotated?.find((m) => m.id.toLowerCase() === id);
  if (known) return { billing: known.billing, reason: known.reason };
  const cli = cliForProvider(providerFromModelId(modelId));
  const { billing, reason } = decide(cli, RUNNER_ONLY_FALLBACK.has(cli), coverage);
  return { billing, reason };
}
