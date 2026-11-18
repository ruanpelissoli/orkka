import { useTranslations } from "use-intl";
import { Badge } from "@/components/ui";
import type { RunnerAuthMode, RunnerProviderStatus } from "@/lib/types";
import { providerLabel } from "@/components/runner/providers";

/** The CLIs whose sign-in Orkka can drive (the guarded three) are always listed; any other
 * CLI only when the runner found it installed. */
const ALWAYS_LISTED = ["claude", "codex", "copilot"];

/** A workspace placeholder in commands — the user's own profile name goes there. */
const WS = "<workspace>";

type HintKey =
  | "claudeLogin"
  | "claudeToken"
  | "claudeApiKey"
  | "claudeBedrock"
  | "claudeVertex"
  | "claudeFoundry"
  | "claudeGateway"
  | "codexLogin"
  | "codexApiKey"
  | "codexAzure"
  | "codexGateway"
  | "copilotLogin"
  | "copilotToken"
  | "copilotEnterprise"
  | "copilotApiKey";

/** How to sign in (or how the current mode is set), with the exact command to run. Commands are
 * not translated: they are what the user types. Never a secret value — `set-env` asks for it hidden. */
export function signInHint(
  provider: string,
  mode: RunnerAuthMode | null | undefined,
  detail: string | null | undefined
): { key: HintKey; command: string } | null {
  switch (provider) {
    case "claude":
      switch (mode) {
        case "token":
          return { key: "claudeToken", command: "claude setup-token" };
        case "api-key":
          return { key: "claudeApiKey", command: `./orkka workspace set-env ${WS} ANTHROPIC_API_KEY` };
        case "bedrock":
          return { key: "claudeBedrock", command: `./orkka workspace set-env ${WS} CLAUDE_CODE_USE_BEDROCK=1` };
        case "vertex":
          return { key: "claudeVertex", command: `./orkka workspace set-env ${WS} CLAUDE_CODE_USE_VERTEX=1` };
        case "foundry":
          return { key: "claudeFoundry", command: `./orkka workspace set-env ${WS} CLAUDE_CODE_USE_FOUNDRY=1` };
        case "gateway":
          return { key: "claudeGateway", command: `./orkka workspace set-env ${WS} ANTHROPIC_BASE_URL=https://…` };
        default:
          return { key: "claudeLogin", command: "claude" };
      }
    case "codex":
      switch (mode) {
        case "api-key":
        case "token":
          return { key: "codexApiKey", command: `./orkka workspace set-env ${WS} CODEX_API_KEY` };
        case "azure-openai":
          return { key: "codexAzure", command: `./orkka workspace set-env ${WS} AZURE_OPENAI_API_KEY` };
        case "gateway":
          return { key: "codexGateway", command: `./orkka workspace set-env ${WS} CODEX_API_KEY` };
        default:
          return { key: "codexLogin", command: "codex login" };
      }
    case "copilot":
      switch (mode) {
        case "token":
          return { key: "copilotToken", command: `./orkka workspace set-env ${WS} COPILOT_GITHUB_TOKEN` };
        case "enterprise-host":
          return {
            key: "copilotEnterprise",
            command: `copilot login --host https://${detail && !detail.includes(" ") ? detail : "<host>"}`,
          };
        case "api-key":
          return { key: "copilotApiKey", command: `./orkka workspace set-env ${WS} COPILOT_PROVIDER_API_KEY` };
        default:
          return { key: "copilotLogin", command: "copilot" };
      }
    default:
      return null;
  }
}

type ModeKey =
  | "login"
  | "chatgpt"
  | "apiKey"
  | "bedrock"
  | "vertex"
  | "foundry"
  | "azureOpenai"
  | "gateway"
  | "token"
  | "enterpriseHost"
  | "none"
  | "unknown";

function modeKey(provider: string, mode: RunnerAuthMode | null | undefined, detail: string | null | undefined): ModeKey {
  switch (mode) {
    case "login":
      return provider === "codex" && detail === "ChatGPT" ? "chatgpt" : "login";
    case "api-key":
      return "apiKey";
    case "azure-openai":
      return "azureOpenai";
    case "enterprise-host":
      return "enterpriseHost";
    case "bedrock":
    case "vertex":
    case "foundry":
    case "gateway":
    case "token":
    case "none":
      return mode;
    default:
      return "unknown";
  }
}

/**
 * Per AI CLI on the runner: installed version, how it signs in ("Claude — Bedrock (us-east-1)"),
 * ready or not, and the exact command for signing in that way. Values that authenticate are never
 * reported by the runner, so none can be shown here.
 */
export function ProviderSignInList({ providers }: { providers: RunnerProviderStatus[] }) {
  const t = useTranslations("dashboard.runnerPage.signIn");
  const rows = providers.filter((p) => ALWAYS_LISTED.includes(p.provider) || p.cliFound);
  if (rows.length === 0) return null;

  return (
    <div className="mt-4" data-testid="provider-sign-in">
      <p className="mb-2 text-label">{t("title")}</p>
      <ul className="space-y-2">
        {rows.map((p) => {
          const ready = p.cliFound && p.authenticated;
          const mode = modeKey(p.provider, p.authMode, p.authDetail);
          // "ChatGPT" is already the mode's own name; other details (a region, a host, a
          // variable name) qualify it.
          const qualifier = p.authDetail && mode !== "chatgpt" ? ` (${p.authDetail})` : "";
          const hint = signInHint(p.provider, p.authMode, p.authDetail);
          return (
            <li
              key={p.provider}
              className="rounded-xl border border-white/[0.06] bg-white/[0.02] p-3"
              data-testid={`provider-sign-in-${p.provider}`}
            >
              <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
                <span className="text-sm font-medium text-gray-100">
                  {providerLabel(p.provider)}
                  {p.cliFound && (
                    <span className="text-gray-300">
                      {" — "}
                      {t(`modes.${mode}`)}
                      {qualifier}
                    </span>
                  )}
                </span>
                <span className="font-mono text-xs text-gray-400">
                  {p.cliFound ? (p.cliVersion ?? t("versionUnknown")) : t("notInstalled")}
                </span>
                <Badge variant={ready ? "success" : "warning"} size="sm" dot>
                  {ready ? t("ready") : t("notReady")}
                </Badge>
              </div>
              {p.detail && <p className="mt-1.5 text-xs leading-snug text-amber-300">{p.detail}</p>}
              {hint && (
                <p className="mt-1.5 text-xs leading-snug text-gray-400">
                  {t(`hints.${hint.key}`)}{" "}
                  <code className="break-all rounded bg-black/40 px-1.5 py-0.5 font-mono text-gray-200">
                    {hint.command}
                  </code>
                </p>
              )}
            </li>
          );
        })}
      </ul>
      <p className="mt-2 text-xs text-gray-400">{t("never")}</p>
    </div>
  );
}
