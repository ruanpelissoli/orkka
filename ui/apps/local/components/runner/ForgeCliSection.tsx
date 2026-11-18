import { useTranslations } from "use-intl";
import { Badge, GlassCard } from "@/components/ui";
import type { ForgeCliName, MyRunnerStatus, RunnerForgeCli } from "@/lib/types";
import { CodeBlock } from "./CodeBlock";

/** The CLIs a runner opens pull requests with, in display order. */
const FORGE_CLIS: ForgeCliName[] = ["gh", "az", "glab"];

/** What makes each CLI ready — copyable, exactly as the user should type it. */
export const FORGE_CLI_REMEDY: Record<ForgeCliName, string[]> = {
  gh: ["gh auth login"],
  az: ["az login", "az extension add --name azure-devops"],
  glab: ["glab auth login"],
};

type CliState = "ready" | "notSignedIn" | "notInstalled";

function stateOf(cli: RunnerForgeCli | undefined): CliState {
  if (!cli || !cli.found) return "notInstalled";
  return cli.authenticated ? "ready" : "notSignedIn";
}

function ForgeCliRow({ name, cli }: { name: ForgeCliName; cli: RunnerForgeCli | undefined }) {
  const t = useTranslations("dashboard.runnerPage.forgeClis");
  const state = stateOf(cli);
  const accounts = (cli?.accounts ?? []).filter(Boolean);

  return (
    <li className="space-y-1.5 py-3 first:pt-0 last:pb-0" data-testid={`forge-cli-${name}`}>
      <div className="flex flex-wrap items-center gap-2">
        <span className="text-sm text-gray-200">{t(`cli.${name}`)}</span>
        <code className="font-mono text-xs text-gray-400">{name}</code>
        <Badge
          variant={state === "ready" ? "success" : "warning"}
          size="sm"
          dot
          className="ml-auto"
        >
          {t(`state.${state}`)}
        </Badge>
      </div>

      {accounts.length > 0 && (
        <p className="text-xs text-gray-400">
          {t("signedInAs", { accounts: accounts.join(", ") })}
        </p>
      )}

      {state !== "ready" && (
        <>
          {cli?.detail && <p className="text-xs leading-snug text-amber-400">{cli.detail}</p>}
          <p className="text-xs text-gray-400">
            {state === "notInstalled" ? t("installThen") : t("remedy")}
          </p>
          <div className="space-y-1.5">
            {FORGE_CLI_REMEDY[name].map((command) => (
              <CodeBlock key={command} code={command} />
            ))}
          </div>
        </>
      )}
    </li>
  );
}

/**
 * "Pull requests" on the runner page: the forge CLIs (gh / az / glab) that open the
 * developer's pull requests when reviewed work lands on a remote repo. They run on this
 * machine under the developer's own logins — Orkka holds no forge credentials and never
 * merges on a forge — so a CLI that is missing or signed out is the one thing to fix here.
 */
export function ForgeCliSection({ status }: { status: MyRunnerStatus }) {
  const t = useTranslations("dashboard.runnerPage.forgeClis");

  const neverConnected = !status.online && !status.machineName && !status.lastSeenAt;
  const clis = status.forgeClis;

  return (
    <GlassCard padding="lg" data-testid="forge-cli-section">
      <h2 className="font-display text-lg font-semibold text-white">{t("title")}</h2>
      <p className="mt-1 text-sm text-gray-400">{t("intro")}</p>

      {neverConnected ? (
        <p className="mt-4 text-sm text-gray-400">{t("notConnected")}</p>
      ) : !clis ? (
        <p className="mt-4 text-sm text-gray-400">{t("notReported")}</p>
      ) : (
        <>
          {!status.online && <p className="mt-3 text-xs text-gray-400">{t("lastKnown")}</p>}
          <ul className="mt-4 divide-y divide-white/[0.06]">
            {FORGE_CLIS.map((name) => (
              <ForgeCliRow key={name} name={name} cli={clis.find((c) => c.cli === name)} />
            ))}
          </ul>
        </>
      )}
    </GlassCard>
  );
}
