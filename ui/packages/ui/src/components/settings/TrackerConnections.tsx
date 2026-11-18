import { useState } from "react";
import { useTranslations } from "use-intl";
import { Badge, Button, CopyableCommand, Input } from "@/components/ui";
import { TrackerKindIcon } from "@/components/ui/brand-icons";
import { trackerAddCommand } from "@/lib/runner-commands";
import { useOptionalWorkspaces } from "@/components/workspace/WorkspaceProvider";
import { useClientApi } from "@/hooks/use-client-api";
import { api, ApiError } from "@/lib/api";
import type { TrackerConnection, TrackerKind } from "@/lib/types";
import { cn, formatRelativeTime } from "@/lib/utils";

/** Where an Atlassian account creates the API token the Jira form asks for. */
export const JIRA_API_TOKEN_URL = "https://id.atlassian.com/manage-profile/security/api-tokens";

/** "https://acme.atlassian.net" → "acme.atlassian.net"; anything unparseable as typed. */
export function trackerSiteLabel(siteUrl: string): string {
  try {
    const url = new URL(siteUrl);
    return (url.host + url.pathname).replace(/\/$/, "");
  } catch {
    return siteUrl;
  }
}

/**
 * The first message of a ProblemDetails body — a FastEndpoints validation failure keys its
 * messages by field. The host never quotes the submitted token in one, so it is safe to show.
 */
function problemMessage(e: unknown): string | null {
  if (!(e instanceof ApiError)) return null;
  try {
    const problem = JSON.parse(e.body) as {
      errors?: Record<string, string[] | undefined>;
      detail?: string;
      title?: string;
    };
    const fieldMessages = Object.values(problem.errors ?? {}).flatMap((m) => m ?? []);
    return fieldMessages[0] ?? problem.detail ?? problem.title ?? null;
  } catch {
    return null;
  }
}

/**
 * How to add a tracker connection from a terminal instead: it is made on the developer's own
 * machine, so the token is stored there and never travels.
 */
export function TrackerAddCommands() {
  const t = useTranslations("dashboard.trackers");
  // A connection belongs to one workspace on the machine: name the one this page is in.
  const workspace = useOptionalWorkspaces()?.current?.slug ?? null;
  return (
    <div className="space-y-3" data-testid="tracker-add-commands">
      <div className="space-y-1.5">
        <p className="flex items-center gap-1.5 text-xs text-gray-300">
          <TrackerKindIcon kind="jira" className="text-gray-400" />
          {t("kind.jira")}
        </p>
        <CopyableCommand command={trackerAddCommand("jira", workspace)} />
      </div>
      <div className="space-y-1.5">
        <p className="flex items-center gap-1.5 text-xs text-gray-300">
          <TrackerKindIcon kind="ado" className="text-gray-400" />
          {t("kind.ado")}
        </p>
        <CopyableCommand command={trackerAddCommand("ado", workspace)} />
      </div>
      <p className="text-xs text-gray-400">{t("tokenNote")}</p>
    </div>
  );
}

type Outcome = { tone: "ok" | "warn" | "error"; text: string } | null;

function OutcomeLine({ outcome, testId }: { outcome: Outcome; testId: string }) {
  if (!outcome) return null;
  return (
    <p
      role={outcome.tone === "ok" ? "status" : "alert"}
      className={cn(
        "text-xs leading-snug",
        outcome.tone === "ok" && "text-emerald-400",
        outcome.tone === "warn" && "text-amber-400",
        outcome.tone === "error" && "text-red-400"
      )}
      data-testid={testId}
    >
      {outcome.text}
    </p>
  );
}

/**
 * "Add Jira Cloud" / "Add Azure DevOps". The token lives in this component's state only: it is
 * posted once, in a request body, to the host — which relays it to the developer's own runner
 * and stores nothing — and is cleared after every attempt, whatever the outcome. Never a server
 * action, a URL, browser storage or a query key. Deliberately not a `<form>`: a form submitted
 * before hydration would put the fields in the page URL.
 */
export function AddTrackerConnectionForm({
  kind,
  onAdded,
}: {
  kind: TrackerKind;
  onAdded: (connection: TrackerConnection) => void;
}) {
  const t = useTranslations("dashboard.trackers.add");
  const withToken = useClientApi();
  const [site, setSite] = useState("");
  const [email, setEmail] = useState("");
  const [token, setToken] = useState("");
  const [busy, setBusy] = useState(false);
  const [outcome, setOutcome] = useState<Outcome>(null);

  const isJira = kind === "jira";
  const ready = site.trim() !== "" && token !== "" && (!isJira || email.trim() !== "") && !busy;

  async function submit() {
    if (!ready) return;
    setBusy(true);
    setOutcome(null);
    const secret = token;
    setToken("");
    try {
      const response = await withToken((authToken) =>
        api.trackers.addConnection(
          isJira
            ? { kind, site: site.trim(), email: email.trim(), token: secret }
            : { kind, site: site.trim(), token: secret },
          authToken
        )
      );
      if (!response.supported) {
        setOutcome({ tone: "warn", text: t("runnerUnsupported") });
      } else if (!response.ok || !response.connection) {
        setOutcome({
          tone: "error",
          text: response.error ? t("failed", { error: response.error }) : t("failedGeneric"),
        });
      } else {
        onAdded(response.connection);
        setSite("");
        setEmail("");
        setOutcome({ tone: "ok", text: t("added", { site: trackerSiteLabel(response.connection.siteUrl) }) });
      }
    } catch (e) {
      setOutcome({ tone: "error", text: problemMessage(e) ?? t("requestFailed") });
    } finally {
      setToken("");
      setBusy(false);
    }
  }

  function onEnter(e: React.KeyboardEvent<HTMLInputElement>) {
    if (e.key === "Enter") {
      e.preventDefault();
      void submit();
    }
  }

  const id = (field: string) => `tracker-add-${kind}-${field}`;

  return (
    <div
      className="space-y-3 rounded-xl border border-white/[0.06] bg-white/[0.02] p-4"
      data-testid={`tracker-add-${kind}`}
    >
      <h4 className="flex items-center gap-2 font-display text-sm font-semibold text-white">
        <TrackerKindIcon kind={kind} className="text-gray-300" />
        {isJira ? t("jiraTitle") : t("adoTitle")}
      </h4>
      <Input
        id={id("site")}
        label={isJira ? t("siteLabel") : t("orgLabel")}
        placeholder={isJira ? t("sitePlaceholder") : t("orgPlaceholder")}
        value={site}
        onChange={(e) => setSite(e.target.value)}
        onKeyDown={onEnter}
        autoComplete="off"
        spellCheck={false}
      />
      {isJira && (
        <Input
          id={id("email")}
          label={t("emailLabel")}
          type="email"
          placeholder={t("emailPlaceholder")}
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          onKeyDown={onEnter}
          autoComplete="off"
          spellCheck={false}
        />
      )}
      <div className="space-y-1.5">
        <Input
          id={id("token")}
          label={isJira ? t("tokenLabel") : t("patLabel")}
          type="password"
          autoComplete="off"
          spellCheck={false}
          value={token}
          onChange={(e) => setToken(e.target.value)}
          onKeyDown={onEnter}
          data-testid={`tracker-add-${kind}-token`}
        />
        {isJira ? (
          <a
            href={JIRA_API_TOKEN_URL}
            target="_blank"
            rel="noopener noreferrer"
            className="inline-block text-xs text-cyan-400 hover:text-cyan-300"
          >
            {t("tokenLink")}
          </a>
        ) : (
          <p className="text-xs text-gray-400" data-testid="tracker-add-ado-scopes">
            {t("adoScopes")}
          </p>
        )}
      </div>
      <p className="text-xs text-gray-400">{t("tokenStays")}</p>
      <div className="flex flex-wrap items-center gap-3">
        <Button type="button" size="sm" disabled={!ready} onClick={() => void submit()}>
          {busy ? t("submitting") : t("submit")}
        </Button>
      </div>
      <OutcomeLine outcome={outcome} testId={`tracker-add-${kind}-outcome`} />
    </div>
  );
}

/** One connection: what it is, whether it works, and re-test / remove. */
function TrackerConnectionRow({
  connection: c,
  onChanged,
  onRemoved,
}: {
  connection: TrackerConnection;
  onChanged: (connection: TrackerConnection) => void;
  onRemoved: (id: string) => void;
}) {
  const t = useTranslations("dashboard.trackers");
  const withToken = useClientApi();
  const [busy, setBusy] = useState<"test" | "remove" | null>(null);
  const [confirming, setConfirming] = useState(false);
  const [outcome, setOutcome] = useState<Outcome>(null);

  async function retest() {
    setBusy("test");
    setOutcome(null);
    try {
      const response = await withToken((token) => api.trackers.query(c.id, { kind: "test" }, token));
      const result = response.result;
      if (!response.supported || !result) {
        setOutcome({ tone: "warn", text: t("add.runnerUnsupported") });
      } else if (!result.ok || !result.connection) {
        setOutcome({
          tone: "error",
          text: result.error ? t("add.failed", { error: result.error }) : t("add.failedGeneric"),
        });
      } else {
        const tested = result.connection;
        onChanged({
          ...c,
          healthy: tested.healthy,
          healthDetail: tested.healthDetail,
          accountId: tested.accountId ?? c.accountId,
          displayName: tested.displayName ?? c.displayName,
          checkedAt: tested.checkedAt ?? new Date().toISOString(),
        });
        setOutcome(
          tested.healthy
            ? { tone: "ok", text: t("retestOk") }
            : { tone: "error", text: tested.healthDetail ?? t("unhealthy") }
        );
      }
    } catch (e) {
      setOutcome({ tone: "error", text: problemMessage(e) ?? t("retestFailed") });
    } finally {
      setBusy(null);
    }
  }

  async function remove() {
    setBusy("remove");
    setOutcome(null);
    try {
      const response = await withToken((token) => api.trackers.removeConnection(c.id, token));
      if (!response.supported) {
        setOutcome({ tone: "warn", text: t("add.runnerUnsupported") });
      } else if (!response.ok) {
        setOutcome({
          tone: "error",
          text: response.error ? t("removeFailed", { error: response.error }) : t("removeFailedGeneric"),
        });
      } else {
        onRemoved(c.id);
        return;
      }
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) {
        onRemoved(c.id);
        return;
      }
      setOutcome({ tone: "error", text: problemMessage(e) ?? t("removeFailedGeneric") });
    } finally {
      setConfirming(false);
      setBusy(null);
    }
  }

  return (
    <li className="space-y-1.5 py-3 first:pt-0" data-testid={`tracker-connection-${c.key}`}>
      <div className="flex flex-wrap items-center gap-2">
        <TrackerKindIcon kind={c.kind} className="shrink-0 text-gray-300" />
        <span className="text-sm text-gray-100">{trackerSiteLabel(c.siteUrl)}</span>
        <span className="text-xs text-gray-400">{c.kind === "ado" ? t("kind.ado") : t("kind.jira")}</span>
        <Badge variant={c.healthy ? "success" : "warning"} size="sm" dot className="ml-auto">
          {c.healthy ? t("healthy") : t("unhealthy")}
        </Badge>
      </div>
      <p className="text-xs text-gray-400">
        {[
          c.displayName || c.accountId ? t("account", { account: c.displayName ?? c.accountId ?? "" }) : null,
          t("machine", { machine: c.machineName }),
          t("lastSeen", { when: formatRelativeTime(c.lastSeenAt) }),
        ]
          .filter(Boolean)
          .join(" · ")}
      </p>
      {!c.healthy && c.healthDetail && (
        <p className="text-xs leading-snug text-amber-400">{c.healthDetail}</p>
      )}
      <div className="flex flex-wrap items-center gap-2 pt-1">
        <Button type="button" size="sm" variant="secondary" disabled={busy !== null} onClick={() => void retest()}>
          {busy === "test" ? t("retesting") : t("retest")}
        </Button>
        {confirming ? (
          <>
            <span className="text-xs text-gray-300">{t("removeConfirm", { machine: c.machineName })}</span>
            <Button type="button" size="sm" variant="danger" disabled={busy !== null} onClick={() => void remove()}>
              {busy === "remove" ? t("removing") : t("remove")}
            </Button>
            <Button type="button" size="sm" variant="ghost" disabled={busy !== null} onClick={() => setConfirming(false)}>
              {t("cancel")}
            </Button>
          </>
        ) : (
          <Button
            type="button"
            size="sm"
            variant="ghost"
            className="text-red-400"
            disabled={busy !== null}
            onClick={() => setConfirming(true)}
          >
            {t("remove")}
          </Button>
        )}
      </div>
      <OutcomeLine outcome={outcome} testId={`tracker-connection-${c.key}-outcome`} />
    </li>
  );
}

/**
 * Settings → Trackers: the caller's own connections, across their machines, with the forms
 * that add one through the runner and the terminal commands as the alternative.
 */
export function TrackerConnectionsList({ connections: initial }: { connections: TrackerConnection[] }) {
  const t = useTranslations("dashboard.trackers");
  const [connections, setConnections] = useState(initial);
  const [notice, setNotice] = useState<string | null>(null);

  function upsert(connection: TrackerConnection) {
    setConnections((prev) =>
      prev.some((c) => c.id === connection.id)
        ? prev.map((c) => (c.id === connection.id ? connection : c))
        : [...prev, connection]
    );
  }

  return (
    <div className="space-y-6">
      {connections.length === 0 ? (
        <p className="text-sm text-gray-400">{t("none")}</p>
      ) : (
        <ul className="divide-y divide-white/[0.06]" data-testid="tracker-connections">
          {connections.map((c) => (
            <TrackerConnectionRow
              key={c.id}
              connection={c}
              onChanged={upsert}
              onRemoved={(id) => {
                setConnections((prev) => prev.filter((x) => x.id !== id));
                setNotice(t("removed"));
              }}
            />
          ))}
        </ul>
      )}
      {notice && (
        <p role="status" className="text-xs text-emerald-400" data-testid="tracker-notice">
          {notice}
        </p>
      )}

      <div className="grid gap-4 lg:grid-cols-2">
        <AddTrackerConnectionForm kind="jira" onAdded={upsert} />
        <AddTrackerConnectionForm kind="ado" onAdded={upsert} />
      </div>

      <details className="text-xs text-gray-400">
        <summary className="cursor-pointer select-none text-gray-300">{t("addWithCli")}</summary>
        <div className="mt-3">
          <TrackerAddCommands />
        </div>
      </details>
    </div>
  );
}
