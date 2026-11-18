import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslations } from "use-intl";
import { Badge, Button, ConfirmDialog, CopyableCommand, GlassCard, Input, Modal, Select } from "@/components/ui";
import { usePermissions } from "@/hooks/use-permissions";
import { useClientApi } from "@/hooks/use-client-api";
import { api, extractErrorMessage } from "@/lib/api";
import { PERMISSION_GROUPS, type Permission } from "@/lib/constants";
import { formatRelativeTime } from "@/lib/utils";
import type { ApiKey, Project } from "@/lib/types";

interface ApiKeysPanelProps {
  /** Public origin of the Orkka API (for the MCP config snippet). */
  apiBaseUrl: string;
  /** Projects the key can optionally be bound to. */
  projects?: Project[];
}

/** Sensible starting set for a "manage the board from Claude Code" key. */
const DEFAULT_PERMISSIONS: Permission[] = [
  "TasksCreate",
  "TasksExecute",
  "PlansReview",
  "CodeReviewReview",
  "CodeReviewMerge",
  "AgentsChat",
];

/**
 * Personal API keys for the REST API and the MCP endpoint (`/mcp`). A key acts as its creator
 * with a permission subset (never Owner) and can be pinned to one project. The raw key is shown
 * exactly once, with a ready-to-paste MCP config for Claude Code / Cursor.
 */
function ApiKeysPanel({ apiBaseUrl, projects = [] }: ApiKeysPanelProps) {
  const t = useTranslations("dashboard.apiKeysPage");
  const tMisc = useTranslations("dashboard.uiMisc");
  const tPerm = useTranslations("dashboard.permissions");
  const withToken = useClientApi();
  const { has, isOwner } = usePermissions();

  const [keys, setKeys] = useState<ApiKey[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [name, setName] = useState("");
  const [projectId, setProjectId] = useState("");
  const [selected, setSelected] = useState<Set<Permission>>(() => new Set(DEFAULT_PERMISSIONS));
  const [busy, setBusy] = useState(false);
  const [minted, setMinted] = useState<{ key: ApiKey; rawKey: string } | null>(null);
  const [pendingRevoke, setPendingRevoke] = useState<ApiKey | null>(null);

  // A promise chain rather than async/await, so the mount effect below sets no state synchronously.
  const load = useCallback(
    (): Promise<void> =>
      withToken((token) => api.apiKeys.list(token)).then(
        (list) => {
          setKeys(list);
          setError(null);
        },
        (err: unknown) => {
          setError(extractErrorMessage(err, t("loadFailed")));
        }
      ),
    [withToken, t]
  );

  useEffect(() => {
    void load();
  }, [load]);

  // You can only grant what you hold — mirror the server rule in the picker.
  const grantable = useMemo(
    () => PERMISSION_GROUPS.flatMap((g) => g.permissions).filter((p) => isOwner || has(p)),
    [has, isOwner]
  );

  const create = useCallback(async () => {
    setBusy(true);
    setError(null);
    try {
      const result = await withToken((token) =>
        api.apiKeys.create(
          { name: name.trim(), permissions: [...selected], projectId: projectId || null },
          token
        )
      );
      setMinted({ key: result.key, rawKey: result.rawKey });
      setCreating(false);
      setName("");
      await load();
    } catch (err) {
      setError(extractErrorMessage(err, t("createFailed")));
    } finally {
      setBusy(false);
    }
  }, [withToken, name, selected, projectId, load, t]);

  const revoke = useCallback(async () => {
    if (!pendingRevoke) return;
    setBusy(true);
    try {
      await withToken((token) => api.apiKeys.revoke(pendingRevoke.id, token));
      setPendingRevoke(null);
      await load();
    } catch (err) {
      setError(extractErrorMessage(err, t("revokeFailed")));
    } finally {
      setBusy(false);
    }
  }, [pendingRevoke, withToken, load, t]);

  const mcpUrl = `${apiBaseUrl.replace(/\/$/, "")}/mcp`;

  return (
    <div className="space-y-4" data-testid="api-keys-panel">
      <GlassCard padding="md">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div>
            <h3 className="font-display text-sm font-semibold text-white">{t("title")}</h3>
            <p className="mt-1 max-w-xl text-xs text-gray-400">{t("intro")}</p>
          </div>
          <Button size="sm" onClick={() => setCreating(true)} disabled={grantable.length === 0}>
            {t("newKey")}
          </Button>
        </div>

        {error && <p className="mt-3 text-sm text-red-400">{error}</p>}

        <div className="mt-4">
          {keys === null ? (
            <div className="h-10 animate-pulse rounded bg-white/[0.04]" />
          ) : keys.length === 0 ? (
            <p className="text-sm text-gray-400">{t("empty")}</p>
          ) : (
            <ul className="divide-y divide-white/[0.04]">
              {keys.map((k) => (
                <li key={k.id} className="flex flex-wrap items-center justify-between gap-3 py-2.5" data-testid="api-key-row">
                  <div className="min-w-0">
                    <div className="flex items-center gap-2">
                      <span className="text-sm text-gray-100">{k.name}</span>
                      <code className="font-mono text-xs text-gray-400">{k.prefix}…</code>
                      <Badge size="sm" variant={k.revoked ? "error" : "success"}>
                        {k.revoked ? t("revoked") : t("active")}
                      </Badge>
                    </div>
                    <p className="mt-0.5 text-xs text-gray-400">
                      {k.permissions.map((p) => tPerm(p as Permission)).join(" · ")}
                      {k.projectId && projects.length > 0 && (
                        <> · {t("boundTo", { project: projects.find((p) => p.id === k.projectId)?.name ?? k.projectId })}</>
                      )}
                      {" · "}
                      {k.lastUsedAt ? t("lastUsed", { when: formatRelativeTime(k.lastUsedAt) }) : t("neverUsed")}
                    </p>
                  </div>
                  {!k.revoked && (
                    <Button size="sm" variant="ghost" className="text-red-400" onClick={() => setPendingRevoke(k)}>
                      {t("revoke")}
                    </Button>
                  )}
                </li>
              ))}
            </ul>
          )}
        </div>
      </GlassCard>

      {/* How to connect */}
      <GlassCard padding="md">
        <h3 className="font-display text-sm font-semibold text-white">{t("connectTitle")}</h3>
        <p className="mt-1 text-xs text-gray-400">{t("connectHint")}</p>
        <div className="mt-3 space-y-2">
          <CopyableCommand command={`claude mcp add --transport http orkka ${mcpUrl} --header "Authorization: Bearer <YOUR_KEY>"`} />
          <CopyableCommand
            command={`{"mcpServers":{"orkka":{"type":"http","url":"${mcpUrl}","headers":{"Authorization":"Bearer <YOUR_KEY>"}}}}`}
          />
        </div>
      </GlassCard>

      {/* Create modal */}
      <Modal open={creating} onClose={() => !busy && setCreating(false)} title={t("newKey")}>
        <div className="space-y-4">
          <Input label={t("fieldName")} value={name} onChange={(e) => setName(e.target.value)} maxLength={100} placeholder={tMisc("apiKeyNamePlaceholder")} autoFocus />
          {projects.length > 0 && (
            <Select
              label={t("fieldProject")}
              value={projectId}
              onChange={(e) => setProjectId(e.target.value)}
            >
              <option value="">{t("anyProject")}</option>
              {projects.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name}
                </option>
              ))}
            </Select>
          )}
          <div>
            <span className="mb-1.5 block text-sm font-medium text-gray-300">{t("fieldPermissions")}</span>
            <p className="mb-2 text-xs text-gray-400">{t("permissionsHint")}</p>
            <div className="grid grid-cols-1 gap-1.5 sm:grid-cols-2">
              {grantable.map((p) => (
                <label key={p} className="flex cursor-pointer items-center gap-2 text-xs text-gray-200">
                  <input
                    type="checkbox"
                    checked={selected.has(p)}
                    onChange={(e) =>
                      setSelected((prev) => {
                        const next = new Set(prev);
                        if (e.target.checked) next.add(p);
                        else next.delete(p);
                        return next;
                      })
                    }
                  />
                  {tPerm(p)}
                </label>
              ))}
            </div>
          </div>
          <div className="flex justify-end gap-2 pt-2">
            <Button variant="ghost" onClick={() => setCreating(false)} disabled={busy}>
              {t("cancel")}
            </Button>
            <Button onClick={() => void create()} disabled={busy || name.trim().length === 0 || selected.size === 0} loading={busy}>
              {t("create")}
            </Button>
          </div>
        </div>
      </Modal>

      {/* One-time reveal */}
      <Modal open={minted !== null} onClose={() => setMinted(null)} title={t("mintedTitle")}>
        {minted && (
          <div className="space-y-3">
            <div className="rounded-xl border border-amber-500/30 bg-amber-500/10 p-3 text-xs text-amber-200">{t("mintedWarning")}</div>
            <CopyableCommand command={minted.rawKey} />
            <p className="text-xs text-gray-400">{t("mintedHint")}</p>
            <CopyableCommand command={`claude mcp add --transport http orkka ${mcpUrl} --header "Authorization: Bearer ${minted.rawKey}"`} />
            <div className="flex justify-end">
              <Button onClick={() => setMinted(null)}>{t("done")}</Button>
            </div>
          </div>
        )}
      </Modal>

      <ConfirmDialog
        open={pendingRevoke !== null}
        title={t("revokeTitle")}
        message={t("revokeMessage", { name: pendingRevoke?.name ?? "" })}
        confirmLabel={t("revoke")}
        cancelLabel={t("cancel")}
        danger
        pending={busy}
        onConfirm={() => void revoke()}
        onCancel={() => setPendingRevoke(null)}
      />
    </div>
  );
}

export { ApiKeysPanel };
