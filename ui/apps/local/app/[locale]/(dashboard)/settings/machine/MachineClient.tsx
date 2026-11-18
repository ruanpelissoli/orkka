import { useCallback, useEffect, useRef, useState } from "react";
import { useTranslations } from "use-intl";
import { Badge, Button, ConfirmDialog, GlassCard } from "@/components/ui";
import { useClientApi } from "@/hooks/use-client-api";
import { api, extractErrorMessage } from "@/lib/api";
import { cn, formatRelativeTime, formatShortDate } from "@/lib/utils";
import type { MyRunnerStatus, RunnerProviderStatus, RunnerToken } from "@/lib/types";
import { CodeBlock } from "@/components/runner/CodeBlock";
import { ForgeCliSection } from "@/components/runner/ForgeCliSection";
import { providerLabel, readyProviders } from "@/components/runner/providers";
import { ProviderSignInList } from "@/components/runner/ProviderSignInList";

const STATUS_POLL_MS = 10_000;

// ── Provider readiness chip ─────────────────────────────────────────

function ProviderChip({ provider }: { provider: RunnerProviderStatus }) {
  const t = useTranslations("dashboard.runnerPage.status");
  const label = providerLabel(provider.provider);

  // Ready needs BOTH halves. Credentials are a file check under ~/.claude that passes even
  // when the CLI is absent from the runner's PATH, so testing `authenticated` first showed a
  // green "ready" chip for a machine that failed every task with "Claude Code not found".
  const ready = provider.cliFound && provider.authenticated;

  const variant = ready ? "success" : "warning";
  const stateLabel = ready
    ? t("providerReady")
    : provider.authenticated
      ? t("providerCliMissing")
      : provider.cliFound
        ? t("providerNotAuthenticated")
        : t("providerNotInstalled");

  return (
    <Badge
      variant={variant}
      size="md"
      dot
      title={provider.detail ?? `${label} — ${stateLabel}`}
      className={cn(!provider.authenticated && !provider.cliFound && "opacity-60")}
    >
      {label}
    </Badge>
  );
}

// ── Runner status card ──────────────────────────────────────────────

function RunnerStatusCard({ status }: { status: MyRunnerStatus }) {
  const t = useTranslations("dashboard.runnerPage.status");

  const neverConnected =
    !status.online && !status.machineName && !status.lastSeenAt;
  const providers = status.providers ?? [];
  // A CLI listed in the sign-in list shows its detail there; the rest keep the one-line list.
  const providerDetails = providers.filter(
    (p) => p.detail && !(["claude", "codex", "copilot"].includes(p.provider) || p.cliFound)
  );
  // Two-part rule (CLI found AND signed in), in preference order — the same list the model
  // pickers and the navbar badge annotate from.
  const ready = readyProviders(providers);
  const copilotReady = ready.some((p) => p.provider === "copilot");

  return (
    <GlassCard padding="lg">
      <div className="flex flex-wrap items-center gap-3">
        <h2 className="font-display text-lg font-semibold text-white">
          {t("title")}
        </h2>
        <span className="flex items-center gap-2">
          <span className="relative flex h-2.5 w-2.5">
            {status.online && (
              <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-emerald-400 opacity-40" />
            )}
            <span
              className={cn(
                "relative inline-flex h-2.5 w-2.5 rounded-full",
                status.online ? "bg-emerald-400" : "bg-gray-500"
              )}
            />
          </span>
          <span
            className={cn(
              "text-sm font-medium",
              status.online ? "text-emerald-400" : "text-gray-400"
            )}
          >
            {status.online ? t("online") : t("offline")}
          </span>
        </span>
      </div>

      {neverConnected ? (
        <p className="mt-4 text-sm text-gray-400">{t("neverConnected")}</p>
      ) : (
        <>
          <dl className="mt-4 grid grid-cols-1 gap-x-6 gap-y-3 text-sm sm:grid-cols-3">
            <div>
              <dt className="text-xs text-gray-400">{t("machine")}</dt>
              <dd className="mt-0.5 truncate font-mono text-gray-200">
                {status.machineName ?? "—"}
              </dd>
            </div>
            <div>
              <dt className="text-xs text-gray-400">{t("version")}</dt>
              <dd className="mt-0.5 font-mono text-gray-200">
                {status.daemonVersion ?? "—"}
              </dd>
            </div>
            <div>
              <dt className="text-xs text-gray-400">{t("lastSeen")}</dt>
              <dd className="mt-0.5 text-gray-200">
                {status.lastSeenAt ? formatRelativeTime(status.lastSeenAt) : "—"}
              </dd>
            </div>
          </dl>

          {/* Push identity. Agent pushes on a runner authenticate with THIS machine's
              credential helper — so a wrong account here is invisible until an agent
              finishes its work and fails at the push. */}
          {status.gitIdentity && (
            <div className="mt-5">
              <p className="mb-2 text-label">
                {t("pushIdentity")}
              </p>
              {status.gitIdentity.defaultAccount ? (
                <p className="text-sm text-gray-300">
                  {t("pushesAs", { account: status.gitIdentity.defaultAccount })}{" "}
                  <span className="text-xs text-gray-400">
                    (
                    {status.gitIdentity.scopedPerRepoPath
                      ? t("pushIdentityScoped")
                      : t("pushIdentityShared")}
                    )
                  </span>
                </p>
              ) : (
                <p className="text-sm text-amber-400">{t("pushIdentityUnknown")}</p>
              )}

              {/* Per-repo overrides — the machine default is a lie when these differ. */}
              {(status.gitIdentity.repos ?? []).filter((r) => r.account).length > 0 && (
                <ul className="mt-1.5 space-y-0.5">
                  {(status.gitIdentity.repos ?? [])
                    .filter((r) => r.account)
                    .map((r) => (
                      <li key={r.repoUrl} className="font-mono text-xs text-gray-400">
                        {(r.repoUrl ?? "").replace(/^https:\/\/github\.com\//, "").replace(/\.git$/, "")}
                        {" → "}
                        <span className="text-gray-400">{r.account}</span>
                      </li>
                    ))}
                </ul>
              )}

              {status.gitIdentity.detail && (
                <p className="mt-1.5 text-xs leading-snug text-amber-400">
                  {status.gitIdentity.detail}
                </p>
              )}
            </div>
          )}

          <div className="mt-5">
            <p className="mb-2 text-label">
              {t("providers")}
            </p>
            {providers.length === 0 ? (
              <p className="text-sm text-gray-400">{t("noProviders")}</p>
            ) : (
              <>
                <div className="flex flex-wrap gap-1.5">
                  {providers.map((p) => (
                    <ProviderChip key={p.provider} provider={p} />
                  ))}
                </div>
                {/* What the chips add up to for the user's wallet: tasks on these providers
                    are free, anything else bills — said in one line rather than inferred. */}
                {ready.length > 0 ? (
                  <p className="mt-2 text-xs text-emerald-400">
                    {t("readySummary", {
                      providers: ready.map((p) => providerLabel(p.provider)).join(", "),
                    })}
                  </p>
                ) : (
                  <p className="mt-2 text-xs text-amber-300">{t("readyNone")}</p>
                )}
                {copilotReady && (
                  <p className="mt-1 text-xs text-gray-400">{t("copilotNote")}</p>
                )}
                <ProviderSignInList providers={providers} />
                {providerDetails.length > 0 && (
                  <ul className="mt-2 space-y-0.5">
                    {providerDetails.map((p) => (
                      <li key={p.provider} className="text-xs leading-snug text-gray-400">
                        <span className="text-gray-400">{providerLabel(p.provider)}:</span>{" "}
                        {p.detail}
                      </li>
                    ))}
                  </ul>
                )}
              </>
            )}
          </div>
        </>
      )}
    </GlassCard>
  );
}

// ── Token row ───────────────────────────────────────────────────────

function TokenRow({
  token,
  busy,
  onRevoke,
}: {
  token: RunnerToken;
  busy: boolean;
  onRevoke: () => void;
}) {
  const t = useTranslations("dashboard.runnerPage.tokens");
  // Expired tokens stay listed (the audit trail) but can no longer connect or be revoked.
  // The clock is read once, when the row mounts — the list reloads on every change anyway.
  const [now] = useState(() => Date.now());
  const expired = !token.revoked && new Date(token.expiresAt).getTime() <= now;

  return (
    <div className="flex flex-col gap-2 rounded-xl border border-white/[0.06] bg-white/[0.02] p-3 sm:flex-row sm:items-center sm:justify-between">
      <div className="min-w-0">
        <div className="flex flex-wrap items-center gap-2">
          <span
            className={cn(
              "truncate text-sm font-medium",
              // A revoked token is a disabled state, which the contrast contract
              // exempts: the strikethrough carries the meaning and the dim grey
              // is what makes it read as inactive.
              token.revoked
                ? // eslint-disable-next-line no-restricted-syntax -- disabled state
                  "text-gray-500 line-through"
                : "text-gray-100"
            )}
          >
            {token.name}
          </span>
          {token.revoked ? (
            <Badge variant="error" size="sm">
              {t("revokedBadge")}
            </Badge>
          ) : expired ? (
            <Badge variant="warning" size="sm">
              {t("expiredBadge")}
            </Badge>
          ) : (
            <Badge variant="success" size="sm">
              {t("activeBadge")}
            </Badge>
          )}
        </div>
        <div className="mt-1 flex flex-wrap items-center gap-x-3 gap-y-0.5 text-xs text-gray-400">
          <span>{t("created", { date: formatShortDate(token.createdAt) })}</span>
          <span>
            {token.lastUsedAt
              ? t("lastUsed", { time: formatRelativeTime(token.lastUsedAt) })
              : t("neverUsed")}
          </span>
          {token.lastMachineName && (
            <span className="font-mono">{token.lastMachineName}</span>
          )}
          {!token.revoked && !expired && (
            <span>{t("expires", { date: formatShortDate(token.expiresAt) })}</span>
          )}
        </div>
      </div>
      {!token.revoked && !expired && (
        <Button
          variant="ghost"
          size="sm"
          onClick={onRevoke}
          disabled={busy}
          className="self-start text-red-400 hover:text-red-300 sm:self-center"
        >
          {t("revoke")}
        </Button>
      )}
    </div>
  );
}

// ── Repository commands waiting for approval ────────────────────────

/**
 * Commands a repository asked to run that this machine has not vouched for. Read-only on
 * purpose: approving one is a decision made at the machine's own terminal, never from a page
 * a browser tab could be tricked into clicking through.
 */
function PendingCommandsCard({ status }: { status: MyRunnerStatus }) {
  const t = useTranslations("dashboard.runnerPage.commands");
  const pending = status.pendingCommandApprovals ?? [];

  return (
    <GlassCard padding="lg" data-testid="pending-commands">
      <h2 className="font-display text-lg font-semibold text-white">{t("title")}</h2>
      <p className="mt-1 text-sm text-gray-400">{t("intro")}</p>

      {pending.length > 0 && (
        <ul className="mt-4 space-y-2">
          {pending.map((c) => (
            <li
              key={c.id}
              className="rounded-xl border border-amber-500/20 bg-amber-500/5 p-3"
            >
              <p className="text-xs text-gray-400">
                {[c.repo, c.kind, c.machine].filter(Boolean).join(" · ")}
              </p>
              <p className="mt-1 break-all font-mono text-sm text-gray-100">{c.command}</p>
              <CodeBlock code={`./orkka commands approve ${c.id}`} className="mt-2" />
            </li>
          ))}
        </ul>
      )}

      <div className="mt-4 space-y-2">
        <p className="text-xs text-gray-400">{t("listHint")}</p>
        <CodeBlock code="./orkka commands" />
        <p className="text-xs text-gray-400">{t("approveHint")}</p>
        <CodeBlock code="./orkka commands approve <id>" />
      </div>
    </GlassCard>
  );
}

// ── Page client ─────────────────────────────────────────────────────

export function MachineClient({
  initialStatus,
  initialTokens,
}: {
  initialStatus: MyRunnerStatus;
  initialTokens: RunnerToken[];
}) {
  const t = useTranslations("dashboard.runnerPage");
  const tActions = useTranslations("common.actions");
  const withToken = useClientApi();

  const [status, setStatus] = useState<MyRunnerStatus>(initialStatus);
  const [tokens, setTokens] = useState<RunnerToken[]>(initialTokens);
  const [revokingId, setRevokingId] = useState<string | null>(null);
  const [confirmingRevoke, setConfirmingRevoke] = useState<RunnerToken | null>(null);
  const [error, setError] = useState<string | null>(null);
  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null);

  const refreshStatus = useCallback(async () => {
    try {
      const s = await withToken((tok) => api.runner.status(tok));
      setStatus(s);
    } catch {
      // Transient — next poll retries.
    }
  }, [withToken]);

  // Poll presence while the page is open.
  useEffect(() => {
    pollRef.current = setInterval(() => void refreshStatus(), STATUS_POLL_MS);
    return () => {
      if (pollRef.current) clearInterval(pollRef.current);
    };
  }, [refreshStatus]);

  async function refreshTokens() {
    try {
      const list = await withToken((tok) => api.runner.tokens.list(tok));
      setTokens(list);
    } catch {
      // Keep the current list — the next mutation refetches.
    }
  }

  async function handleRevoke(id: string) {
    setConfirmingRevoke(null);
    setRevokingId(id);
    setError(null);
    try {
      await withToken((tok) => api.runner.tokens.revoke(id, tok));
      await refreshTokens();
      void refreshStatus();
    } catch (err) {
      setError(extractErrorMessage(err, t("tokens.errorGeneric")));
    } finally {
      setRevokingId(null);
    }
  }

  return (
    <div className="space-y-6">
      <div>
        <h2 className="font-display text-lg font-semibold text-white">
          {t("title")}
        </h2>
        <p className="mt-1 text-sm text-gray-400">{t("subtitle")}</p>
      </div>

      {/* Runner status */}
      <RunnerStatusCard status={status} />

      {/* Forge CLIs — what opens the developer's pull requests */}
      <ForgeCliSection status={status} />

      {/* Repository commands this machine has not approved yet */}
      <PendingCommandsCard status={status} />

      {/* Tokens */}
      <GlassCard padding="lg">
        <h2 className="font-display text-lg font-semibold text-white">
          {t("tokens.title")}
        </h2>
        <p className="mt-1 text-sm text-gray-400">{t("tokens.subtitle")}</p>

        {error && <p className="mt-2 text-xs text-red-400">{error}</p>}

        <div className="mt-4 space-y-2">
          {tokens.length === 0 ? (
            <p className="text-sm text-gray-400">{t("tokens.empty")}</p>
          ) : (
            tokens.map((token) => (
              <TokenRow
                key={token.id}
                token={token}
                busy={revokingId === token.id}
                onRevoke={() => setConfirmingRevoke(token)}
              />
            ))
          )}
        </div>
      </GlassCard>

      <ConfirmDialog
        open={confirmingRevoke !== null}
        title={confirmingRevoke?.name ?? ""}
        message={t("tokens.confirmRevoke")}
        confirmLabel={t("tokens.revoke")}
        cancelLabel={tActions("cancel")}
        danger
        onConfirm={() => {
          if (confirmingRevoke) void handleRevoke(confirmingRevoke.id);
        }}
        onCancel={() => setConfirmingRevoke(null)}
      />
    </div>
  );
}
