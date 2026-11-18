import { useCallback, useEffect, useRef, useState } from "react";
import { useAuth } from "@/lib/local-auth-client";
import { useTranslations } from "use-intl";
import { Button, Input, Modal } from "@/components/ui";
import { api, extractErrorMessage } from "@/lib/api";
import { cn } from "@/lib/utils";
import { useOptionalWorkspaces } from "./WorkspaceProvider";

/** Runner presence per workspace: true online, false offline, undefined not known yet. */
type RunnerDots = Record<string, boolean | undefined>;

function RunnerDot({ online, label }: { online: boolean | undefined; label: string }) {
  if (online === undefined) return null;
  return (
    <span
      className={cn(
        "h-1.5 w-1.5 shrink-0 rounded-full",
        online ? "bg-emerald-400" : "bg-gray-500"
      )}
      title={label}
      aria-label={label}
      role="img"
    />
  );
}

/**
 * The workspace (job) switcher. Lists every workspace the caller belongs to with their role,
 * the current one checked; picking another reloads the app into it. "New workspace" names
 * one, creates it, and opens its onboarding.
 *
 * Runner dots: the current workspace's runner is fetched once on mount; the others only
 * when the menu is opened (one request each, then kept) — never N requests per render.
 */
export function WorkspaceSwitcher() {
  const ws = useOptionalWorkspaces();
  const t = useTranslations("dashboard.workspaces");
  const { getToken } = useAuth();
  const [open, setOpen] = useState(false);
  const [dots, setDots] = useState<RunnerDots>({});
  const [switching, setSwitching] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [newName, setNewName] = useState("");
  const [newOpen, setNewOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const asked = useRef(new Set<string>());

  const currentId = ws?.current?.orgId ?? null;

  /** One request per workspace, ever: the answer is a dot, not a live feed. */
  const fetchDot = useCallback(
    (orgId: string, isCurrent: boolean): Promise<void> => {
      if (asked.current.has(orgId)) return Promise.resolve();
      asked.current.add(orgId);
      return getToken()
        .then((token) => api.runner.status(token, isCurrent ? undefined : orgId))
        .then((status) => {
          if (status) setDots((prev) => ({ ...prev, [orgId]: status.online }));
        })
        .catch(() => {
          // Unknown stays unknown — no dot is better than a wrong one.
        });
    },
    [getToken]
  );

  // The current workspace's runner, once.
  useEffect(() => {
    if (!currentId || asked.current.has(currentId)) return;
    asked.current.add(currentId);
    let cancelled = false;
    getToken()
      .then((token) => api.runner.status(token))
      .then((status) => {
        if (!cancelled && status) setDots((prev) => ({ ...prev, [currentId]: status.online }));
      })
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [currentId, getToken]);

  /** Opening the menu asks about the other workspaces' runners (once each). */
  function toggleMenu() {
    const next = !open;
    setOpen(next);
    if (next && ws) {
      for (const w of ws.workspaces) {
        if (w.orgId !== currentId) void fetchDot(w.orgId, false);
      }
    }
  }

  useEffect(() => {
    if (!open) return;
    function onDown(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    }
    document.addEventListener("mousedown", onDown);
    return () => document.removeEventListener("mousedown", onDown);
  }, [open]);

  if (!ws || !ws.current) return null;
  const { workspaces, current } = ws;

  async function pick(orgId: string) {
    if (!ws || orgId === currentId) {
      setOpen(false);
      return;
    }
    setSwitching(orgId);
    setError(null);
    try {
      await ws.switchTo(orgId);
    } catch (e) {
      setError(extractErrorMessage(e, t("switchFailed")));
      setSwitching(null);
    }
  }

  async function createWorkspace() {
    const name = newName.trim();
    if (!ws || !name || creating) return;
    setCreating(true);
    setError(null);
    try {
      await ws.create(name);
    } catch (e) {
      setError(extractErrorMessage(e, t("createFailed")));
      setCreating(false);
    }
  }

  return (
    <div ref={ref} className="relative min-w-0" data-testid="workspace-switcher">
      <button
        type="button"
        onClick={toggleMenu}
        aria-haspopup="menu"
        aria-expanded={open}
        title={t("label")}
        className={cn(
          "flex items-center gap-2 rounded-lg px-2.5 py-1.5 text-sm font-medium transition-colors hover:bg-white/[0.06]",
          open ? "bg-white/[0.06] text-white" : "text-gray-200"
        )}
      >
        <span className="flex h-5 w-5 shrink-0 items-center justify-center rounded-md bg-white/[0.08] text-xs font-semibold uppercase text-gray-200">
          {current.name.charAt(0)}
        </span>
        <span className="max-w-[8rem] truncate sm:max-w-[11rem]">{current.name}</span>
        <RunnerDot
          online={dots[current.orgId]}
          label={dots[current.orgId] ? t("runnerOnline") : t("runnerOffline")}
        />
        <svg width="12" height="12" viewBox="0 0 16 16" fill="none" className="shrink-0 text-gray-400" aria-hidden>
          <path d="M4 6l4 4 4-4" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      </button>

      {open && (
        <div
          role="menu"
          className="absolute left-0 top-full z-50 mt-1 w-72 rounded-xl border border-white/[0.08] bg-gray-900/95 p-1.5 shadow-2xl backdrop-blur-xl"
        >
          <p className="px-2.5 pb-1 pt-1.5 text-label">{t("label")}</p>
          {workspaces.map((w) => {
            const isCurrent = w.orgId === current.orgId;
            return (
              <button
                key={w.orgId}
                type="button"
                role="menuitemradio"
                aria-checked={isCurrent}
                disabled={switching !== null}
                onClick={() => void pick(w.orgId)}
                className={cn(
                  "flex w-full items-center gap-2 rounded-lg px-2.5 py-2 text-left text-sm transition-colors",
                  isCurrent ? "bg-white/[0.06] text-white" : "text-gray-300 hover:bg-white/[0.04]"
                )}
                data-testid={`workspace-option-${w.orgId}`}
              >
                <span className="min-w-0 flex-1">
                  <span className="block truncate">{w.name}</span>
                  <span className="block text-xs text-gray-400">
                    {w.role === "Owner" ? t("roleOwner") : t("roleMember")}
                  </span>
                </span>
                <RunnerDot
                  online={dots[w.orgId]}
                  label={dots[w.orgId] ? t("runnerOnline") : t("runnerOffline")}
                />
                {switching === w.orgId ? (
                  <span className="text-xs text-gray-400">{t("switching")}</span>
                ) : isCurrent ? (
                  <svg width="14" height="14" viewBox="0 0 16 16" fill="none" className="shrink-0 text-primary-400" aria-hidden>
                    <path d="M3.5 8.5l3 3 6-7" stroke="currentColor" strokeWidth="1.75" strokeLinecap="round" strokeLinejoin="round" />
                  </svg>
                ) : null}
              </button>
            );
          })}
          <div className="my-1 border-t border-white/[0.06]" />
          <button
            type="button"
            role="menuitem"
            onClick={() => {
              setOpen(false);
              setNewOpen(true);
            }}
            className="flex w-full items-center gap-2 rounded-lg px-2.5 py-2 text-left text-sm text-gray-300 transition-colors hover:bg-white/[0.04]"
          >
            <svg width="14" height="14" viewBox="0 0 16 16" fill="none" aria-hidden>
              <path d="M8 3v10M3 8h10" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
            </svg>
            {t("newWorkspace")}
          </button>
          {error && <p className="px-2.5 py-1.5 text-xs text-red-400">{error}</p>}
        </div>
      )}

      <Modal open={newOpen} onClose={() => setNewOpen(false)} title={t("newTitle")}>
        <form
          className="space-y-4"
          onSubmit={(e) => {
            e.preventDefault();
            void createWorkspace();
          }}
        >
          <p className="text-sm text-gray-400">{t("newHint")}</p>
          <Input
            label={t("nameLabel")}
            placeholder={t("namePlaceholder")}
            value={newName}
            onChange={(e) => setNewName(e.target.value)}
            maxLength={80}
            autoFocus
          />
          {error && <p className="text-xs text-red-400">{error}</p>}
          <div className="flex justify-end">
            <Button type="submit" size="sm" disabled={!newName.trim() || creating}>
              {creating ? t("creating") : t("create")}
            </Button>
          </div>
        </form>
      </Modal>
    </div>
  );
}
