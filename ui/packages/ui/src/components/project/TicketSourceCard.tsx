import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslations } from "use-intl";
import {
  Button,
  GlassCard,
  Input,
  SegmentedControl,
  Select,
  Textarea,
  Toggle,
} from "@/components/ui";
import { TrackerKindIcon } from "@/components/ui/brand-icons";
import { TrackerAddCommands, trackerSiteLabel } from "@/components/settings/TrackerConnections";
import { Link } from "@/i18n/navigation";
import { usePermissions } from "@/hooks/use-permissions";
import { useClientApi } from "@/hooks/use-client-api";
import { api, extractErrorMessage } from "@/lib/api";
import {
  GITHUB_DEFAULT_STATUS_FIELD,
  TRACKER_DEFAULT_BRANCH_TEMPLATE,
  TRACKER_DEFAULT_INTAKE_STATUSES,
  TRACKER_DEFAULT_POLL_SECONDS,
  TRACKER_MAX_POLL_SECONDS,
  TRACKER_MIN_POLL_SECONDS,
  TRACKER_STATUS_STEPS,
  duplicateEpicKeys,
  isValidBranchTemplate,
  isValidPollInterval,
  normalizeTrackerSubpath,
  routingRuleKindsFor,
} from "@/lib/tracker";
import { cn, formatRelativeTime } from "@/lib/utils";
import type {
  ProjectRepo,
  ProjectTrackerConfig,
  SaveProjectTrackerRequest,
  TrackerBoard,
  TrackerConnection,
  TrackerDefaults,
  TrackerEpic,
  TrackerField,
  TrackerQueryRequest,
  TrackerQueryResult,
  TrackerRoutingRuleKind,
  TrackerScope,
  TrackerScopeMode,
  TrackerStatus,
  TrackerStatusMap,
} from "@/lib/types";

interface TicketSourceCardProps {
  /** Project id (ULID) the tracker endpoints are addressed by. */
  projectId: string;
  initialConfig: ProjectTrackerConfig | null;
  /** The viewer's own connections — the only ones a ticket source can use. */
  connections: TrackerConnection[];
  repos: ProjectRepo[];
  /** User id of the viewer. */
  viewerUserId: string | null;
  /** Display name of the config's owner when it isn't the viewer. */
  ownerName?: string | null;
}

interface EpicRow {
  epicKey: string;
  epicTitle: string | null;
  /** "" = no repository: the epic only scopes intake. */
  repoId: string;
  subpath: string;
}

interface RuleRow {
  uid: number;
  kind: TrackerRoutingRuleKind;
  value: string;
  repoId: string;
  subpath: string;
}

interface QueryAnswer {
  result: TrackerQueryResult;
  defaults: TrackerDefaults | null;
}

function epicRowsFrom(config: ProjectTrackerConfig | null): EpicRow[] {
  return (config?.epics ?? []).map((e) => ({
    epicKey: e.epicKey,
    epicTitle: e.epicTitle,
    repoId: e.repoId ?? "",
    subpath: e.subpath ?? "",
  }));
}

function ruleRowsFrom(config: ProjectTrackerConfig | null): RuleRow[] {
  return (config?.routingRules ?? []).map((r, i) => ({
    uid: i + 1,
    kind: r.kind,
    value: r.value,
    repoId: r.repoId,
    subpath: r.subpath ?? "",
  }));
}

function moved<T>(list: T[], from: number, to: number): T[] {
  if (to < 0 || to >= list.length) return list;
  const next = [...list];
  const [item] = next.splice(from, 1);
  next.splice(to, 0, item);
  return next;
}

/** A setting with its explanation on the left and a switch on the right. */
function ToggleRow({
  label,
  hint,
  checked,
  onChange,
  disabled,
  testId,
}: {
  label: string;
  hint: string;
  checked: boolean;
  onChange: (value: boolean) => void;
  disabled: boolean;
  testId?: string;
}) {
  return (
    <div className="flex items-start justify-between gap-4" data-testid={testId}>
      <div className="min-w-0">
        <p className="text-sm text-gray-300">{label}</p>
        <p className="mt-0.5 text-xs text-gray-400">{hint}</p>
      </div>
      <Toggle checked={checked} onChange={onChange} disabled={disabled} />
    </div>
  );
}

/** The tracker a connection talks to; anything unknown reads as Jira, the first one. */
function trackerKindOf(kind: string | null | undefined): "jira" | "ado" | "github" {
  return kind === "ado" ? "ado" : kind === "github" ? "github" : "jira";
}

/**
 * A project's "Ticket source": which of the viewer's Jira / Azure DevOps / GitHub connections
 * feeds it. For GitHub the scope is an owner (an organization or the viewer) and the board is
 * one of its Projects — required, with the single-select field that holds the status and an
 * optional repository filter;
 * which tickets are picked up (everything assigned to the viewer in an intake status, or only
 * some epics'), and which repository each ticket is worked in. Everything browsable here
 * (projects, epics, statuses, the preview) is answered by the viewer's own runner — the token
 * is on that machine — so an offline runner is a state to explain, not an error.
 *
 * The defaults are meant to be enough: pick the connection and the project, and the intake
 * ("To Do") and status map come from the tracker's own statuses.
 */
export function TicketSourceCard({
  projectId,
  initialConfig,
  connections,
  repos,
  viewerUserId,
  ownerName,
}: TicketSourceCardProps) {
  const t = useTranslations("dashboard.ticketSource");
  const tTrackers = useTranslations("dashboard.trackers");
  const withToken = useClientApi();
  const { has } = usePermissions();
  const canManage = has("ProjectsManage");

  const [config, setConfig] = useState<ProjectTrackerConfig | null>(initialConfig);
  const ownedByOther = !!config && !!viewerUserId && config.ownerUserId !== viewerUserId;
  // A saved source keeps what its owner chose; a new one takes the tracker's defaults.
  const startedNew = initialConfig === null;

  const [connectionId, setConnectionId] = useState<string>(() => {
    const configured = initialConfig?.trackerConnectionId;
    if (configured && connections.some((c) => c.id === configured)) return configured;
    return connections[0]?.id ?? "";
  });
  const connection = connections.find((c) => c.id === connectionId) ?? null;
  const kind = trackerKindOf(connection?.kind);
  const isGitHub = kind === "github";
  const ruleKinds = routingRuleKindsFor(kind);

  const [scope, setScope] = useState(initialConfig?.scope ?? "");
  // "" = the whole project (every board).
  const [boardId, setBoardId] = useState(initialConfig?.boardId ?? "");
  const [boardName, setBoardName] = useState(initialConfig?.boardName ?? "");
  // GitHub: the single-select field the status lives in, and the repositories intake keeps.
  const [statusField, setStatusField] = useState(
    initialConfig?.statusField || GITHUB_DEFAULT_STATUS_FIELD
  );
  const [repoFilter, setRepoFilter] = useState<string[]>(() => [...(initialConfig?.repoFilter ?? [])]);
  const [scopeMode, setScopeMode] = useState<TrackerScopeMode>(initialConfig?.scopeMode ?? "All");
  const [intakeStatuses, setIntakeStatuses] = useState<string[]>(
    initialConfig?.intakeStatuses?.length ? initialConfig.intakeStatuses : [...TRACKER_DEFAULT_INTAKE_STATUSES]
  );
  const [rows, setRows] = useState<EpicRow[]>(() => epicRowsFrom(initialConfig));
  const [rules, setRules] = useState<RuleRow[]>(() => ruleRowsFrom(initialConfig));
  const [nextUid, setNextUid] = useState(() => (initialConfig?.routingRules?.length ?? 0) + 1);
  const [defaultRepoId, setDefaultRepoId] = useState<string>(() =>
    initialConfig ? (initialConfig.defaultRepoId ?? "") : repos.length === 1 ? repos[0].id : ""
  );
  const [extraClause, setExtraClause] = useState(initialConfig?.extraClause ?? "");
  const [includeDescendants, setIncludeDescendants] = useState(
    initialConfig?.includeDescendants ?? false
  );
  const [autoSubmit, setAutoSubmit] = useState(initialConfig?.autoSubmit ?? true);
  const [storyMode, setStoryMode] = useState((initialConfig?.storyMode ?? 1) === 1);
  const [pollText, setPollText] = useState(
    String(initialConfig?.pollIntervalSeconds ?? TRACKER_DEFAULT_POLL_SECONDS)
  );
  const [branchTemplate, setBranchTemplate] = useState(
    initialConfig?.branchTemplate ?? TRACKER_DEFAULT_BRANCH_TEMPLATE
  );
  const [enabled, setEnabled] = useState(initialConfig?.enabled ?? true);
  const [statusMap, setStatusMap] = useState<TrackerStatusMap>(
    () => ({ ...(initialConfig?.statusMap ?? {}) })
  );

  const [scopes, setScopes] = useState<TrackerScope[]>([]);
  const [boards, setBoards] = useState<TrackerBoard[]>([]);
  const [statuses, setStatuses] = useState<TrackerStatus[]>([]);
  const [fields, setFields] = useState<TrackerField[]>([]);
  const [projectRepos, setProjectRepos] = useState<string[]>([]);
  const [epicSearch, setEpicSearch] = useState("");
  const [epicResults, setEpicResults] = useState<TrackerEpic[] | null>(null);
  const [preview, setPreview] = useState<TrackerQueryResult | null>(null);
  const [testResult, setTestResult] = useState<TrackerQueryResult | null>(null);

  const [runnerOffline, setRunnerOffline] = useState(false);
  // Per query kind, so a later query that succeeds doesn't wipe an earlier one's failure.
  // "" = failed with no explanation from the tracker.
  const [queryErrors, setQueryErrors] = useState<Partial<Record<TrackerQueryRequest["kind"], string>>>({});
  const queryError = Object.values(queryErrors).find((e) => e !== undefined);
  const [busy, setBusy] = useState<"test" | "preview" | "save" | "remove" | null>(null);
  const [notice, setNotice] = useState<{ kind: "ok" | "error"; text: string } | null>(null);
  const [confirmingRemove, setConfirmingRemove] = useState(false);

  /** One tracker question through the viewer's runner. Null when it could not be answered. */
  // Deliberately independent of `t`: the effects below depend on this callback, and a
  // translation function is not guaranteed to be referentially stable.
  const query = useCallback(
    // A promise chain rather than async/await: every state update lands in a callback, so the
    // effects below can start a query without setting state synchronously.
    (request: TrackerQueryRequest): Promise<QueryAnswer | null> => {
      if (!connectionId) return Promise.resolve(null);
      const setError = (error: string | undefined) =>
        setQueryErrors((prev) => ({ ...prev, [request.kind]: error }));
      return withToken((token) => api.trackers.query(connectionId, request, token)).then(
        (response): QueryAnswer | null => {
          if (!response.supported || !response.result) {
            setRunnerOffline(true);
            return null;
          }
          setRunnerOffline(false);
          setError(response.result.ok ? undefined : (response.result.error ?? ""));
          return { result: response.result, defaults: response.defaults ?? null };
        },
        (e: unknown) => {
          setError(extractErrorMessage(e, ""));
          return null;
        }
      );
    },
    [connectionId, withToken]
  );

  // The scope list follows the connection.
  useEffect(() => {
    if (!connectionId) return;
    let cancelled = false;
    void query({ kind: "scopes" }).then((answer) => {
      if (!cancelled && answer?.result.ok) setScopes(answer.result.scopes ?? []);
    });
    return () => {
      cancelled = true;
    };
  }, [connectionId, query]);

  // The board list follows the scope.
  useEffect(() => {
    if (!connectionId || !scope) return;
    let cancelled = false;
    void query({ kind: "boards", scope }).then((answer) => {
      if (!cancelled && answer?.result.ok) setBoards(answer.result.boards ?? []);
    });
    return () => {
      cancelled = true;
    };
  }, [connectionId, scope, query]);

  // GitHub: the project's single-select fields and the repositories on it follow the project.
  useEffect(() => {
    if (!connectionId || !isGitHub || !scope || !boardId) return;
    let cancelled = false;
    void query({ kind: "fields", scope, boardId, boardName: boardName || undefined }).then((answer) => {
      if (!cancelled && answer?.result.ok) setFields(answer.result.fields ?? []);
    });
    void query({ kind: "repos", scope, boardId, boardName: boardName || undefined }).then((answer) => {
      if (!cancelled && answer?.result.ok) setProjectRepos(answer.result.repositories ?? []);
    });
    return () => {
      cancelled = true;
    };
  }, [connectionId, isGitHub, scope, boardId, boardName, query]);

  // Statuses (intake + status sync) follow the scope — for GitHub the project's status field —
  // and bring the defaults with them: a new source takes them whole; a saved one only fills a
  // Developing / PR-opened step left empty (the steps the first version of the map moved).
  // Only GitHub's statuses depend on the project and the field; Jira / ADO ask once per scope.
  const statusesBoard = isGitHub ? boardId : "";
  const statusesField = isGitHub ? statusField || GITHUB_DEFAULT_STATUS_FIELD : "";
  useEffect(() => {
    if (!connectionId || !scope || (isGitHub && !statusesBoard)) return;
    let cancelled = false;
    const request: TrackerQueryRequest = isGitHub
      ? { kind: "statuses", scope, boardId: statusesBoard, statusField: statusesField }
      : { kind: "statuses", scope };
    void query(request).then((answer) => {
      if (cancelled || !answer?.result.ok) return;
      setStatuses(answer.result.statuses ?? []);
      const defaults = answer.defaults;
      if (!defaults) return;
      if (startedNew) {
        setStatusMap({ ...defaults.statusMap });
        if (defaults.intakeStatuses.length > 0) setIntakeStatuses([...defaults.intakeStatuses]);
      } else {
        setStatusMap((prev) => ({
          ...prev,
          developing: prev.developing ?? defaults.statusMap.developing ?? null,
          prOpened: prev.prOpened ?? defaults.statusMap.prOpened ?? null,
        }));
      }
    });
    return () => {
      cancelled = true;
    };
  }, [connectionId, scope, isGitHub, statusesBoard, statusesField, query, startedNew]);

  // Epic search, debounced on the typed text. GitHub has no epics.
  useEffect(() => {
    const text = epicSearch.trim();
    if (!connectionId || !scope || !text || isGitHub) return;
    let cancelled = false;
    const timer = setTimeout(() => {
      void query({ kind: "epics", scope, search: text }).then((answer) => {
        if (!cancelled) setEpicResults(answer?.result.ok ? (answer.result.epics ?? []) : []);
      });
    }, 350);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [connectionId, scope, epicSearch, isGitHub, query]);

  // ── Validation (mirrors the server's SaveProjectTrackerValidator) ──
  const pollSeconds = Number(pollText);
  const epicSubpathErrors = rows.map((r) => !normalizeTrackerSubpath(r.subpath).ok);
  const ruleSubpathErrors = rules.map((r) => !normalizeTrackerSubpath(r.subpath).ok);
  const duplicates = duplicateEpicKeys(rows.map((r) => r.epicKey));
  const problems = useMemo(() => {
    const list: string[] = [];
    if (!connection) list.push(t("errors.connection"));
    if (!scope.trim()) list.push(t("errors.scope"));
    if (isGitHub && !boardId) list.push(t("errors.project"));
    if (intakeStatuses.length === 0) list.push(t("errors.intake"));
    if (scopeMode === "Epics" && rows.length === 0) list.push(t("errors.noEpics"));
    if (duplicates.length > 0) list.push(t("errors.duplicateEpic"));
    if (rules.some((r) => !r.value.trim() || !r.repoId)) list.push(t("errors.rule"));
    if (epicSubpathErrors.some(Boolean) || ruleSubpathErrors.some(Boolean)) list.push(t("errors.subpath"));
    if (!isValidPollInterval(pollSeconds))
      list.push(t("errors.poll", { min: TRACKER_MIN_POLL_SECONDS, max: TRACKER_MAX_POLL_SECONDS }));
    if (!isValidBranchTemplate(branchTemplate)) list.push(t("errors.branch", { key: "{key}" }));
    return list;
  }, [
    connection,
    scope,
    isGitHub,
    boardId,
    intakeStatuses.length,
    scopeMode,
    rows.length,
    duplicates.length,
    rules,
    epicSubpathErrors,
    ruleSubpathErrors,
    pollSeconds,
    branchTemplate,
    t,
  ]);

  function addEpic(epic: TrackerEpic) {
    setRows((prev) =>
      prev.some((r) => r.epicKey.toLowerCase() === epic.key.toLowerCase())
        ? prev
        : [...prev, { epicKey: epic.key, epicTitle: epic.title, repoId: "", subpath: "" }]
    );
    setEpicSearch("");
    setEpicResults(null);
  }

  function updateRow(index: number, patch: Partial<EpicRow>) {
    setRows((prev) => prev.map((r, i) => (i === index ? { ...r, ...patch } : r)));
  }

  function addRule() {
    setRules((prev) => [
      ...prev,
      { uid: nextUid, kind: ruleKinds[0], value: "", repoId: repos[0]?.id ?? "", subpath: "" },
    ]);
    setNextUid((n) => n + 1);
  }

  function updateRule(index: number, patch: Partial<RuleRow>) {
    setRules((prev) => prev.map((r, i) => (i === index ? { ...r, ...patch } : r)));
  }

  /** A board belongs to one scope (and one connection): changing either forgets it — and with
   * a GitHub project, its fields, repositories and the repository filter. */
  function clearBoard() {
    setBoards([]);
    setBoardId("");
    setBoardName("");
    clearProjectDetails();
  }

  function clearProjectDetails() {
    setFields([]);
    setProjectRepos([]);
    setRepoFilter([]);
  }

  function toggleRepoFilter(name: string) {
    setRepoFilter((prev) =>
      prev.some((r) => r.toLowerCase() === name.toLowerCase())
        ? prev.filter((r) => r.toLowerCase() !== name.toLowerCase())
        : [...prev, name]
    );
    setPreview(null);
  }

  function toggleIntake(name: string) {
    setIntakeStatuses((prev) =>
      prev.some((s) => s.toLowerCase() === name.toLowerCase())
        ? prev.filter((s) => s.toLowerCase() !== name.toLowerCase())
        : [...prev, name]
    );
    setPreview(null);
  }

  async function runTest() {
    setBusy("test");
    setTestResult(null);
    const answer = await query({ kind: "test" });
    setTestResult(answer?.result ?? null);
    setBusy(null);
  }

  async function runPreview() {
    setBusy("preview");
    setPreview(null);
    const answer = await query(
      isGitHub
        ? {
            kind: "preview",
            scope,
            scopeMode: "All",
            intakeStatuses,
            boardId: boardId || undefined,
            boardName: boardId ? boardName || undefined : undefined,
            statusField: statusField || GITHUB_DEFAULT_STATUS_FIELD,
            repoFilter,
          }
        : {
            kind: "preview",
            scope,
            scopeMode,
            intakeStatuses,
            epicKeys: scopeMode === "Epics" ? rows.map((r) => r.epicKey) : undefined,
            extraClause: extraClause.trim() || undefined,
            includeDescendants,
            boardId: boardId || undefined,
            boardName: boardId ? boardName || undefined : undefined,
          }
    );
    setPreview(answer?.result.ok ? answer.result : null);
    setBusy(null);
  }

  function buildRequest(): SaveProjectTrackerRequest {
    if (isGitHub) {
      return {
        trackerConnectionId: connectionId,
        scope: scope.trim(),
        scopeMode: "All",
        epics: [],
        routingRules: rules.map((r) => ({
          kind: r.kind,
          value: r.value.trim(),
          repoId: r.repoId,
          subpath: normalizeTrackerSubpath(r.subpath).normalized,
        })),
        intakeStatuses,
        defaultRepoId: defaultRepoId || null,
        storyMode: storyMode ? 1 : 0,
        boardId: boardId || null,
        boardName: boardId ? boardName || null : null,
        statusField: statusField.trim() || null,
        repoFilter,
        extraClause: null,
        includeDescendants: false,
        autoSubmit,
        pollIntervalSeconds: pollSeconds,
        statusMap: Object.fromEntries(
          TRACKER_STATUS_STEPS.map((step) => [step, statusMap[step] || null])
        ) as TrackerStatusMap,
        branchTemplate: branchTemplate.trim() || null,
        enabled,
      };
    }
    return {
      trackerConnectionId: connectionId,
      scope: scope.trim(),
      scopeMode,
      epics: rows.map((r) => ({
        epicKey: r.epicKey.trim(),
        epicTitle: r.epicTitle,
        repoId: r.repoId || null,
        subpath: r.repoId ? normalizeTrackerSubpath(r.subpath).normalized : null,
      })),
      routingRules: rules.map((r) => ({
        kind: r.kind,
        value: r.value.trim(),
        repoId: r.repoId,
        subpath: normalizeTrackerSubpath(r.subpath).normalized,
      })),
      intakeStatuses,
      defaultRepoId: defaultRepoId || null,
      storyMode: storyMode ? 1 : 0,
      boardId: boardId || null,
      boardName: boardId ? boardName || null : null,
      extraClause: extraClause.trim() || null,
      includeDescendants,
      autoSubmit,
      pollIntervalSeconds: pollSeconds,
      statusMap: Object.fromEntries(
        TRACKER_STATUS_STEPS.map((step) => [step, statusMap[step] || null])
      ) as TrackerStatusMap,
      branchTemplate: branchTemplate.trim() || null,
      enabled,
    };
  }

  async function save() {
    if (problems.length > 0) return;
    setBusy("save");
    setNotice(null);
    try {
      const saved = await withToken((token) =>
        api.trackers.saveProjectConfig(projectId, buildRequest(), token)
      );
      setConfig(saved);
      setNotice({ kind: "ok", text: t("saved") });
    } catch (e) {
      setNotice({ kind: "error", text: extractErrorMessage(e, t("saveFailed")) });
    } finally {
      setBusy(null);
    }
  }

  async function remove() {
    setBusy("remove");
    setNotice(null);
    try {
      await withToken((token) => api.trackers.deleteProjectConfig(projectId, token));
      setConfig(null);
      setRows([]);
      setRules([]);
      setScope("");
      clearBoard();
      setPreview(null);
      setConfirmingRemove(false);
      setNotice({ kind: "ok", text: t("removed") });
    } catch (e) {
      setNotice({ kind: "error", text: extractErrorMessage(e, t("removeFailed")) });
    } finally {
      setBusy(null);
    }
  }

  // The scope / status lists come from the runner; keep the saved values selectable while
  // they load, or when the runner is offline, so nothing reads as blank.
  const scopeOptions =
    scope && !scopes.some((s) => s.key === scope) ? [{ key: scope, name: scope }, ...scopes] : scopes;
  const boardOptions: TrackerBoard[] =
    boardId && !boards.some((b) => b.id === boardId)
      ? [{ id: boardId, name: boardName || boardId }, ...boards]
      : boards;
  const statusNames = statuses.map((s) => s.name);
  for (const value of Object.values(statusMap)) {
    if (value && !statusNames.includes(value)) statusNames.push(value);
  }
  const intakeOptions = statuses.map((s) => s.name);
  for (const value of intakeStatuses) {
    if (!intakeOptions.some((o) => o.toLowerCase() === value.toLowerCase())) intakeOptions.push(value);
  }
  const repoName = (id: string) => repos.find((r) => r.id === id)?.name ?? id;
  const kindLabel = (k: string) => tTrackers(`kind.${trackerKindOf(k)}`);
  const fieldOptions: TrackerField[] =
    statusField && !fields.some((f) => f.name === statusField)
      ? [{ id: statusField, name: statusField, options: [] }, ...fields]
      : fields;
  const repoFilterOptions = [...projectRepos];
  for (const value of repoFilter) {
    if (!repoFilterOptions.some((o) => o.toLowerCase() === value.toLowerCase())) repoFilterOptions.push(value);
  }

  return (
    <GlassCard padding="lg" id="ticket-source" data-testid="ticket-source-card">
      <h2 className="font-display text-lg font-semibold text-white">{t("title")}</h2>
      <p className="mt-1 text-sm text-gray-400">{t("intro")}</p>

      {config && (
        <p className="mt-3 text-xs text-gray-400" data-testid="ticket-source-poll">
          {config.lastPolledAt
            ? t("lastPolled", { when: formatRelativeTime(config.lastPolledAt) })
            : t("notPolledYet")}
          {config.lastPollError && (
            <span className="mt-1 block text-amber-400">
              {t("lastPollError", { error: config.lastPollError })}
            </span>
          )}
        </p>
      )}

      {ownedByOther && (
        <p
          className="mt-3 rounded-lg border border-amber-500/20 bg-amber-500/[0.06] px-3 py-2 text-xs text-amber-300"
          data-testid="ticket-source-owner"
        >
          {t("ownedBy", { owner: ownerName || t("anotherMember") })}
        </p>
      )}

      {connections.length === 0 ? (
        <div className="mt-4 space-y-3" data-testid="ticket-source-no-connections">
          <p className="text-sm text-gray-300">{t("noConnections")}</p>
          <Link
            href="/settings/trackers"
            className="inline-block text-sm text-cyan-400 hover:text-cyan-300"
          >
            {t("addConnectionLink")}
          </Link>
          <details className="text-xs text-gray-400">
            <summary className="cursor-pointer select-none text-gray-300">
              {tTrackers("addWithCli")}
            </summary>
            <div className="mt-3">
              <TrackerAddCommands />
            </div>
          </details>
        </div>
      ) : (
        <fieldset disabled={!canManage} className="mt-5 space-y-6 disabled:opacity-80">
          {!canManage && <p className="text-xs text-gray-400">{t("readOnly")}</p>}

          {/* Connection */}
          <div className="space-y-2">
            <Select
              label={t("connectionLabel")}
              value={connectionId}
              onChange={(e) => {
                setConnectionId(e.target.value);
                setQueryErrors({});
                setScopes([]);
                setStatuses([]);
                clearBoard();
                setTestResult(null);
                setPreview(null);
              }}
              data-testid="ticket-source-connection"
            >
              {connections.map((c) => (
                <option key={c.id} value={c.id}>
                  {t("connectionOption", {
                    kind: kindLabel(c.kind),
                    site: trackerSiteLabel(c.siteUrl),
                    machine: c.machineName,
                  })}
                  {c.healthy ? "" : ` — ${tTrackers("unhealthy")}`}
                </option>
              ))}
            </Select>
            {connection && (
              <div className="flex flex-wrap items-center gap-2 text-xs text-gray-400">
                <TrackerKindIcon kind={connection.kind} className="text-gray-300" />
                <span className={connection.healthy ? "text-emerald-400" : "text-amber-400"}>
                  {connection.healthy ? tTrackers("healthy") : tTrackers("unhealthy")}
                </span>
                <span>· {tTrackers("machine", { machine: connection.machineName })}</span>
                <Button
                  type="button"
                  size="sm"
                  variant="secondary"
                  className="ml-auto"
                  disabled={busy !== null}
                  onClick={runTest}
                >
                  {busy === "test" ? t("testing") : t("test")}
                </Button>
              </div>
            )}
            {testResult?.ok && testResult.connection && (
              <p
                className={cn(
                  "text-xs",
                  testResult.connection.healthy ? "text-emerald-400" : "text-amber-400"
                )}
                data-testid="ticket-source-test-result"
              >
                {testResult.connection.healthy
                  ? t("testOk", {
                      account:
                        testResult.connection.displayName ?? testResult.connection.accountId ?? "",
                    })
                  : (testResult.connection.healthDetail ?? tTrackers("unhealthy"))}
              </p>
            )}
          </div>

          {runnerOffline && (
            <p
              className="rounded-lg border border-white/[0.08] bg-white/[0.03] px-3 py-2 text-xs text-gray-300"
              data-testid="ticket-source-offline"
            >
              {t("runnerOffline")}
            </p>
          )}
          {queryError !== undefined && !runnerOffline && (
            <p className="text-xs text-red-400" data-testid="ticket-source-query-error">
              {queryError ? t("queryFailed", { error: queryError }) : t("queryFailedGeneric")}
            </p>
          )}

          {/* Scope */}
          <Select
            label={t(`scopeLabel.${kind}`)}
            value={scope}
            onChange={(e) => {
              setScope(e.target.value);
              setStatuses([]);
              clearBoard();
              setPreview(null);
            }}
            data-testid="ticket-source-scope"
          >
            <option value="">{t("scopePlaceholder")}</option>
            {scopeOptions.map((s) => (
              <option key={s.key} value={s.key}>
                {s.name === s.key ? s.key : `${s.name} (${s.key})`}
              </option>
            ))}
          </Select>

          {/* Board — narrows intake to one board of the scope (a Jira board, an ADO team); for
              GitHub it is the Project the tickets come from, and required */}
          <Select
            label={isGitHub ? t("projectLabel") : t("boardLabel")}
            hint={isGitHub ? t("projectHint") : t("boardHint")}
            value={boardId}
            disabled={!scope}
            onChange={(e) => {
              const picked = boardOptions.find((b) => b.id === e.target.value);
              setBoardId(picked?.id ?? "");
              setBoardName(picked?.name ?? "");
              clearProjectDetails();
              setStatuses([]);
              setPreview(null);
            }}
            data-testid="ticket-source-board"
          >
            <option value="">{isGitHub ? t("projectPlaceholder") : t("boardWhole")}</option>
            {boardOptions.map((b) => (
              <option key={b.id} value={b.id}>
                {kind === "ado"
                  ? t("boardOptionAdo", { team: b.name })
                  : isGitHub && b.number != null
                    ? t("projectOption", { number: b.number, title: b.name })
                    : b.name}
              </option>
            ))}
          </Select>

          {isGitHub && (
            <div className="space-y-4" data-testid="ticket-source-github">
              <Select
                label={t("statusFieldLabel")}
                hint={t("statusFieldHint")}
                value={statusField}
                disabled={!boardId}
                onChange={(e) => {
                  setStatusField(e.target.value || GITHUB_DEFAULT_STATUS_FIELD);
                  setStatuses([]);
                  setPreview(null);
                }}
                data-testid="ticket-source-status-field"
              >
                {fieldOptions.length === 0 && (
                  <option value={GITHUB_DEFAULT_STATUS_FIELD}>{GITHUB_DEFAULT_STATUS_FIELD}</option>
                )}
                {fieldOptions.map((f) => (
                  <option key={f.id} value={f.name}>
                    {f.options.length > 0
                      ? t("statusFieldOption", { name: f.name, count: f.options.length })
                      : f.name}
                  </option>
                ))}
              </Select>
              <div className="space-y-1.5" data-testid="ticket-source-repo-filter">
                <p className="text-xs text-gray-300">{t("repoFilterLabel")}</p>
                {repoFilterOptions.length > 0 ? (
                  <div className="flex flex-wrap gap-1.5">
                    {repoFilterOptions.map((name) => {
                      const selected = repoFilter.some((r) => r.toLowerCase() === name.toLowerCase());
                      return (
                        <button
                          key={name}
                          type="button"
                          aria-pressed={selected}
                          onClick={() => toggleRepoFilter(name)}
                          className={cn(
                            "rounded-full border px-2.5 py-1 font-mono text-xs transition-colors",
                            selected
                              ? "border-primary-500/50 bg-primary-500/15 text-primary-200"
                              : "border-white/[0.08] bg-white/[0.02] text-gray-400 hover:text-gray-200"
                          )}
                        >
                          {name}
                        </button>
                      );
                    })}
                  </div>
                ) : null}
                <p className="text-xs text-gray-400">
                  {repoFilter.length === 0 ? t("repoFilterAll") : t("repoFilterSome", { count: repoFilter.length })}
                </p>
              </div>
            </div>
          )}

          {/* Which tickets */}
          <div className="space-y-3">
            <h3 className="text-sm font-medium text-gray-100">{t("whichTickets")}</h3>
            {isGitHub ? (
              <p className="text-xs text-gray-400" data-testid="ticket-source-github-intake-hint">
                {t("githubIntakeHint")}
              </p>
            ) : (
              <SegmentedControl<TrackerScopeMode>
                aria-label={t("whichTickets")}
                value={scopeMode}
                onChange={(mode) => {
                  setScopeMode(mode);
                  setPreview(null);
                }}
                options={[
                  { value: "All", label: t("scopeMode.All"), testId: "ticket-source-mode-all" },
                  { value: "Epics", label: t("scopeMode.Epics"), testId: "ticket-source-mode-epics" },
                ]}
              />
            )}
            <div className="space-y-1.5" data-testid="ticket-source-intake">
              <p className="text-xs text-gray-300">{t("intakeLabel")}</p>
              <div className="flex flex-wrap gap-1.5">
                {intakeOptions.map((name) => {
                  const selected = intakeStatuses.some((s) => s.toLowerCase() === name.toLowerCase());
                  return (
                    <button
                      key={name}
                      type="button"
                      aria-pressed={selected}
                      onClick={() => toggleIntake(name)}
                      className={cn(
                        "rounded-full border px-2.5 py-1 text-xs transition-colors",
                        selected
                          ? "border-primary-500/50 bg-primary-500/15 text-primary-200"
                          : "border-white/[0.08] bg-white/[0.02] text-gray-400 hover:text-gray-200"
                      )}
                    >
                      {name}
                    </button>
                  );
                })}
              </div>
              <p className="text-xs text-gray-400">
                {statuses.length > 0 ? t("intakeHint") : t("intakeHintNoStatuses")}
              </p>
            </div>
          </div>

          {/* Epics (GitHub has none) */}
          {!isGitHub && (
          <div className="space-y-2">
            <h3 className="text-sm font-medium text-gray-100">{t("epicsTitle")}</h3>
            <p className="text-xs text-gray-400">
              {scopeMode === "Epics" ? t("epicsHint") : t("epicsHintAll")}
            </p>

            <Input
              placeholder={t("epicSearchPlaceholder")}
              value={epicSearch}
              onChange={(e) => {
                setEpicSearch(e.target.value);
                if (!e.target.value.trim()) setEpicResults(null);
              }}
              disabled={!scope}
              data-testid="ticket-source-epic-search"
            />
            {epicResults && epicSearch.trim() && (
              <ul
                className="max-h-56 overflow-y-auto rounded-lg border border-white/[0.06]"
                data-testid="ticket-source-epic-results"
              >
                {epicResults.length === 0 ? (
                  <li className="px-3 py-2 text-xs text-gray-400">{t("noEpicsFound")}</li>
                ) : (
                  epicResults.map((epic) => (
                    <li key={epic.key}>
                      <button
                        type="button"
                        onClick={() => addEpic(epic)}
                        className="flex w-full items-center gap-2 px-3 py-2 text-left text-sm hover:bg-white/[0.04]"
                      >
                        <span className="font-mono text-xs text-cyan-400">{epic.key}</span>
                        <span className="min-w-0 flex-1 truncate text-gray-200">{epic.title}</span>
                        {epic.status && <span className="text-xs text-gray-400">{epic.status}</span>}
                      </button>
                    </li>
                  ))
                )}
              </ul>
            )}

            {rows.length > 0 && (
              <div className="space-y-2" data-testid="ticket-source-epics">
                {rows.map((row, i) => (
                  <div
                    key={row.epicKey}
                    className="grid gap-2 rounded-lg border border-white/[0.06] bg-white/[0.02] p-3 sm:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_minmax(0,1fr)_auto] sm:items-start"
                  >
                    <div className="min-w-0 pt-1">
                      <p className="font-mono text-xs text-cyan-400">{row.epicKey}</p>
                      {row.epicTitle && (
                        <p className="truncate text-xs text-gray-300">{row.epicTitle}</p>
                      )}
                    </div>
                    <Select
                      aria-label={t("repoColumn")}
                      value={row.repoId}
                      onChange={(e) => updateRow(i, { repoId: e.target.value })}
                      size="sm"
                    >
                      <option value="">{t("epicNoRepo")}</option>
                      {repos.map((r) => (
                        <option key={r.id} value={r.id}>
                          {r.name}
                        </option>
                      ))}
                    </Select>
                    <Input
                      aria-label={t("subpathColumn")}
                      placeholder={t("subpathPlaceholder")}
                      value={row.subpath}
                      onChange={(e) => updateRow(i, { subpath: e.target.value })}
                      disabled={!row.repoId}
                      error={epicSubpathErrors[i] ? t("errors.subpath") : undefined}
                      className="font-mono text-xs"
                    />
                    <Button
                      type="button"
                      size="sm"
                      variant="ghost"
                      onClick={() => setRows((prev) => prev.filter((_, j) => j !== i))}
                      title={t("removeEpic", { key: row.epicKey })}
                      aria-label={t("removeEpic", { key: row.epicKey })}
                    >
                      ×
                    </Button>
                  </div>
                ))}
              </div>
            )}
          </div>
          )}

          {/* Routing */}
          <div className="space-y-2">
            <h3 className="text-sm font-medium text-gray-100">{t("routingTitle")}</h3>
            <p className="text-xs text-gray-400" data-testid="ticket-source-routing-hint">
              {isGitHub ? t("routingHintGithub") : t("routingHint")}
            </p>
            {rules.length > 0 && (
              <ol className="space-y-2" data-testid="ticket-source-rules">
                {rules.map((rule, i) => (
                  <li
                    key={rule.uid}
                    className="grid gap-2 rounded-lg border border-white/[0.06] bg-white/[0.02] p-3 sm:grid-cols-[auto_minmax(0,1fr)_minmax(0,1fr)_minmax(0,1fr)_auto] sm:items-start"
                    data-testid="ticket-source-rule"
                  >
                    <Select
                      aria-label={t("ruleKindLabel")}
                      value={rule.kind}
                      onChange={(e) => updateRule(i, { kind: e.target.value as TrackerRoutingRuleKind })}
                      size="sm"
                    >
                      {(ruleKinds.includes(rule.kind) ? ruleKinds : [rule.kind, ...ruleKinds]).map((k) => (
                        <option key={k} value={k}>
                          {t(`ruleKind.${k}`)}
                        </option>
                      ))}
                    </Select>
                    <Input
                      aria-label={t("ruleValueLabel")}
                      placeholder={t(`ruleValuePlaceholder.${rule.kind}`)}
                      value={rule.value}
                      onChange={(e) => updateRule(i, { value: e.target.value })}
                    />
                    <Select
                      aria-label={t("ruleRepoLabel")}
                      value={rule.repoId}
                      onChange={(e) => updateRule(i, { repoId: e.target.value })}
                      size="sm"
                    >
                      <option value="">{t("repoPlaceholder")}</option>
                      {repos.map((r) => (
                        <option key={r.id} value={r.id}>
                          {r.name}
                        </option>
                      ))}
                    </Select>
                    <Input
                      aria-label={t("subpathColumn")}
                      placeholder={t("subpathPlaceholder")}
                      value={rule.subpath}
                      onChange={(e) => updateRule(i, { subpath: e.target.value })}
                      error={ruleSubpathErrors[i] ? t("errors.subpath") : undefined}
                      className="font-mono text-xs"
                    />
                    <div className="flex items-center gap-1">
                      <Button
                        type="button"
                        size="sm"
                        variant="ghost"
                        disabled={i === 0}
                        onClick={() => setRules((prev) => moved(prev, i, i - 1))}
                        aria-label={t("ruleUp")}
                        title={t("ruleUp")}
                      >
                        ↑
                      </Button>
                      <Button
                        type="button"
                        size="sm"
                        variant="ghost"
                        disabled={i === rules.length - 1}
                        onClick={() => setRules((prev) => moved(prev, i, i + 1))}
                        aria-label={t("ruleDown")}
                        title={t("ruleDown")}
                      >
                        ↓
                      </Button>
                      <Button
                        type="button"
                        size="sm"
                        variant="ghost"
                        onClick={() => setRules((prev) => prev.filter((_, j) => j !== i))}
                        aria-label={t("ruleRemove", { value: rule.value || repoName(rule.repoId) })}
                        title={t("ruleRemove", { value: rule.value || repoName(rule.repoId) })}
                      >
                        ×
                      </Button>
                    </div>
                  </li>
                ))}
              </ol>
            )}
            <Button
              type="button"
              size="sm"
              variant="secondary"
              onClick={addRule}
              disabled={repos.length === 0}
            >
              {t("ruleAdd")}
            </Button>
            <Select
              label={t("defaultRepoLabel")}
              value={defaultRepoId}
              onChange={(e) => setDefaultRepoId(e.target.value)}
              data-testid="ticket-source-default-repo"
            >
              <option value="">{t("defaultRepoNone")}</option>
              {repos.map((r) => (
                <option key={r.id} value={r.id}>
                  {r.name}
                </option>
              ))}
            </Select>
          </div>

          {/* Preview */}
          <div className="space-y-3">
            <Button
              type="button"
              size="sm"
              variant="secondary"
              disabled={
                busy !== null ||
                !connection ||
                !scope ||
                (isGitHub && !boardId) ||
                intakeStatuses.length === 0 ||
                (scopeMode === "Epics" && rows.length === 0)
              }
              onClick={runPreview}
            >
              {busy === "preview" ? t("previewing") : t("preview")}
            </Button>
            {preview && (
              <div
                className="space-y-2 rounded-lg border border-white/[0.06] bg-white/[0.02] p-3"
                data-testid="ticket-source-preview"
              >
                <p className="text-sm text-gray-200">
                  {t("previewCount", { count: preview.matchCount ?? 0 })}
                </p>
                {(preview.sample ?? []).length > 0 && (
                  <ul className="space-y-1">
                    {(preview.sample ?? []).map((ticket) => (
                      <li key={ticket.key} className="flex items-center gap-2 text-xs">
                        {ticket.url ? (
                          <a
                            href={ticket.url}
                            target="_blank"
                            rel="noopener noreferrer"
                            className="font-mono text-cyan-400 hover:text-cyan-300"
                          >
                            {ticket.key}
                          </a>
                        ) : (
                          <span className="font-mono text-cyan-400">{ticket.key}</span>
                        )}
                        <span className="min-w-0 flex-1 truncate text-gray-300">{ticket.title}</span>
                        {ticket.status && <span className="text-gray-400">{ticket.status}</span>}
                      </li>
                    ))}
                  </ul>
                )}
                {preview.effectiveQuery && (
                  <div>
                    <p className="text-label">{t("effectiveQuery")}</p>
                    <pre className="mt-1 overflow-x-auto rounded-md bg-black/30 px-2 py-1.5 font-mono text-xs text-gray-300">
                      {preview.effectiveQuery}
                    </pre>
                  </div>
                )}
              </div>
            )}
          </div>

          {/* Ticket status sync — one row per Orkka step */}
          <div className="space-y-2" data-testid="ticket-source-status-sync">
            <h3 className="text-sm font-medium text-gray-100">{t("statusMapTitle")}</h3>
            <p className="text-xs text-gray-400">{t("statusMapHint")}</p>
            <ul className="divide-y divide-white/[0.06] rounded-lg border border-white/[0.06]">
              {TRACKER_STATUS_STEPS.map((step) => (
                <li
                  key={step}
                  className="grid gap-2 px-3 py-2.5 sm:grid-cols-[minmax(0,1fr)_minmax(0,14rem)] sm:items-center"
                  data-testid={`ticket-source-status-row-${step}`}
                >
                  <div className="min-w-0">
                    <label
                      htmlFor={`ticket-source-status-${step}`}
                      className="block text-sm text-gray-200"
                    >
                      {t(`statusStep.${step}`)}
                    </label>
                    <p className="text-xs text-gray-400">{t(`statusStepHint.${step}`)}</p>
                  </div>
                  <Select
                    id={`ticket-source-status-${step}`}
                    value={statusMap[step] ?? ""}
                    onChange={(e) =>
                      setStatusMap((prev) => ({ ...prev, [step]: e.target.value || null }))
                    }
                    size="sm"
                    data-testid={`ticket-source-status-${step}`}
                  >
                    <option value="">{t("dontChange")}</option>
                    {statusNames.map((name) => (
                      <option key={name} value={name}>
                        {name}
                      </option>
                    ))}
                  </Select>
                </li>
              ))}
            </ul>
            <p className="text-xs text-gray-400">{t("statusMapNote")}</p>
          </div>

          {/* Behaviour */}
          <div className="space-y-4">
            <ToggleRow
              label={t("autoSubmit")}
              hint={t("autoSubmitHint")}
              checked={autoSubmit}
              onChange={setAutoSubmit}
              disabled={!canManage}
            />
            <ToggleRow
              label={t("storyMode")}
              hint={t("storyModeHint")}
              checked={storyMode}
              onChange={setStoryMode}
              disabled={!canManage}
              testId="ticket-source-story-mode"
            />
            <ToggleRow
              label={t("enabled")}
              hint={t("enabledHint")}
              checked={enabled}
              onChange={setEnabled}
              disabled={!canManage}
            />
          </div>

          {/* Advanced — the defaults are fine for most */}
          <details className="group space-y-3" data-testid="ticket-source-advanced">
            <summary className="cursor-pointer select-none text-sm text-gray-300">
              {t("advanced")}
            </summary>
            <div className="mt-3 space-y-4">
              <div className="grid gap-3 sm:grid-cols-2">
                <Input
                  id="ticket-source-poll-interval"
                  label={t("pollLabel")}
                  type="number"
                  min={TRACKER_MIN_POLL_SECONDS}
                  max={TRACKER_MAX_POLL_SECONDS}
                  value={pollText}
                  onChange={(e) => setPollText(e.target.value)}
                  error={
                    isValidPollInterval(pollSeconds)
                      ? undefined
                      : t("errors.poll", { min: TRACKER_MIN_POLL_SECONDS, max: TRACKER_MAX_POLL_SECONDS })
                  }
                />
                <Input
                  id="ticket-source-branch-template"
                  label={t("branchLabel")}
                  hint={t("branchHint", { type: "{type}", key: "{key}", slug: "{slug}" })}
                  value={branchTemplate}
                  onChange={(e) => setBranchTemplate(e.target.value)}
                  error={isValidBranchTemplate(branchTemplate) ? undefined : t("errors.branch", { key: "{key}" })}
                  className="font-mono text-xs"
                />
              </div>
              {!isGitHub && (
                <>
                  <div className="space-y-1">
                    <Textarea
                      label={t(`extraClauseLabel.${kind}`)}
                      value={extraClause}
                      onChange={(e) => setExtraClause(e.target.value)}
                      rows={2}
                      className="font-mono text-xs"
                    />
                    <p className="text-xs text-gray-400">{t("extraClauseHint")}</p>
                  </div>
                  <ToggleRow
                    label={t("includeDescendants")}
                    hint={t("includeDescendantsHint")}
                    checked={includeDescendants}
                    onChange={setIncludeDescendants}
                    disabled={!canManage}
                  />
                </>
              )}
            </div>
          </details>

          {/* Save / remove */}
          {canManage && (
            <div className="space-y-2 border-t border-white/[0.06] pt-4">
              {problems.length > 0 && (
                <ul className="space-y-0.5 text-xs text-amber-400" data-testid="ticket-source-problems">
                  {problems.map((p) => (
                    <li key={p}>{p}</li>
                  ))}
                </ul>
              )}
              <div className="flex flex-wrap items-center gap-2">
                <Button
                  type="button"
                  size="sm"
                  disabled={busy !== null || problems.length > 0}
                  onClick={save}
                >
                  {busy === "save" ? t("saving") : t("save")}
                </Button>
                {config &&
                  (confirmingRemove ? (
                    <>
                      <span className="text-xs text-gray-400">{t("removeConfirm")}</span>
                      <Button
                        type="button"
                        size="sm"
                        variant="danger"
                        disabled={busy !== null}
                        onClick={remove}
                      >
                        {busy === "remove" ? t("removing") : t("remove")}
                      </Button>
                      <Button
                        type="button"
                        size="sm"
                        variant="ghost"
                        onClick={() => setConfirmingRemove(false)}
                      >
                        {t("cancel")}
                      </Button>
                    </>
                  ) : (
                    <Button
                      type="button"
                      size="sm"
                      variant="ghost"
                      className="text-red-400"
                      onClick={() => setConfirmingRemove(true)}
                    >
                      {t("remove")}
                    </Button>
                  ))}
              </div>
            </div>
          )}

          {notice && (
            <p
              className={cn("text-xs", notice.kind === "ok" ? "text-emerald-400" : "text-red-400")}
              data-testid="ticket-source-notice"
            >
              {notice.text}
            </p>
          )}
        </fieldset>
      )}
    </GlassCard>
  );
}
