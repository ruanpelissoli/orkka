import { useEffect, useMemo, useState } from "react";
import { AnimatePresence, motion } from "framer-motion";
import { Avatar, FloatingDockSlot, FLOATING_DOCK_ORDER } from "@/components/ui";
import { api } from "@/lib/api";
import { useClientApi } from "@/hooks/use-client-api";
import { AGENT_ROLES, type AgentRoleKey } from "@/lib/constants";
import type { ProjectAgent } from "@/lib/types";
import { useTranslations } from "use-intl";
import { AgentChatTab } from "./AgentChatTab";

/** Attachment request from the host page (e.g. Design iterate). A fresh object per click
 * so re-iterating the same asset after closing the drawer still reopens it. */
interface AttachRequest {
  assetId: string;
}

interface AgentChatDrawerProps {
  projectId: string;
  /** Which agent to chat with — a single role, or several in preference order (e.g. the
   * Design page offers Designer and GameDesigner). Each resolves to the project's first
   * active agent of that role; with several matches the header shows a switcher and the
   * first listed role is the default. */
  role: AgentRoleKey | AgentRoleKey[];
  attach?: AttachRequest | null;
  /** Gallery folder open on the page behind the drawer, so assets the agent creates are
   * filed where the user is looking instead of at the root. */
  designFolderPath?: string | null;
}

/**
 * Floating launcher + slide-over chat with a page's companion agent (Luna on Documents,
 * Archie on the Architecture Board, Muse/Sprite on Design) — ask for tasks, diagrams, or
 * design iterations without leaving the page. No backdrop: the page stays interactive
 * beside it. Renders nothing when the project has no active agent of the given role(s).
 */
function AgentChatDrawer({ projectId, role, attach, designFolderPath }: AgentChatDrawerProps) {
  const t = useTranslations("dashboard.agentChat");
  const withToken = useClientApi();
  const [candidates, setCandidates] = useState<ProjectAgent[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [open, setOpen] = useState(false);

  // Stable key — call sites pass array literals, which would re-trigger the effect
  // every render if the array itself were a dependency.
  const rolesKey = Array.isArray(role) ? role.join(",") : role;

  useEffect(() => {
    const roles = rolesKey.split(",") as AgentRoleKey[];
    let cancelled = false;
    withToken((token) => api.agents.list(projectId, token))
      .then((agents) => {
        if (cancelled) return;
        // One agent per role, in the caller's preference order.
        setCandidates(
          roles.flatMap((r) => agents.find((a) => a.role === r) ?? [])
        );
      })
      .catch(() => {
        /* agent lookup failed — the launcher simply doesn't render */
      });
    return () => {
      cancelled = true;
    };
  }, [projectId, rolesKey, withToken]);

  // An attach request from the page opens the drawer with the asset pre-attached.
  // Adjusted during render for the same reason as elsewhere: an effect would open the
  // drawer one commit late. Keyed on the request itself, so closing it stays closed
  // until a NEW attach arrives.
  const [lastAttach, setLastAttach] = useState(attach);
  if (lastAttach !== attach) {
    setLastAttach(attach);
    if (attach) setOpen(true);
  }

  const agent = candidates.find((a) => a.id === selectedId) ?? candidates[0] ?? null;
  const roleMeta = useMemo(
    () => (agent ? AGENT_ROLES.find((r) => r.key === agent.role) : undefined),
    [agent]
  );

  if (!agent) return null;

  return (
    <>
      {!open && (
        <FloatingDockSlot order={FLOATING_DOCK_ORDER.agentChat}>
          <button
            type="button"
            onClick={() => setOpen(true)}
            aria-label={t("openChat", { agentName: agent.name })}
            title={t("openChat", { agentName: agent.name })}
            className="flex items-center gap-2 rounded-full border border-white/[0.08] bg-gray-900/90 py-2 pl-2 pr-4 shadow-lg backdrop-blur-xl transition-colors hover:bg-gray-800/90"
          >
            <Avatar name={agent.name} size="sm" />
            <span className="text-sm font-medium text-gray-200">{agent.name}</span>
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" className="text-gray-400">
              <path d="M21 11.5a8.38 8.38 0 01-.9 3.8 8.5 8.5 0 01-7.6 4.7 8.38 8.38 0 01-3.8-.9L3 21l1.9-5.7a8.38 8.38 0 01-.9-3.8 8.5 8.5 0 014.7-7.6 8.38 8.38 0 013.8-.9h.5a8.48 8.48 0 018 8v.5z" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
          </button>
        </FloatingDockSlot>
      )}

      <AnimatePresence>
        {open && (
          <motion.div
            initial={{ x: "100%" }}
            animate={{ x: 0 }}
            exit={{ x: "100%" }}
            transition={{ type: "tween", duration: 0.18 }}
            className="fixed inset-y-0 right-0 z-[60] flex w-full max-w-md flex-col border-l border-white/[0.06] bg-gray-950/95 pb-[env(safe-area-inset-bottom)] shadow-2xl backdrop-blur-xl lg:z-50"
          >
            <div className="flex items-center justify-between border-b border-white/[0.04] px-4 py-3">
              <div className="flex items-center gap-2.5">
                <Avatar name={agent.name} size="sm" />
                <div>
                  <p className="text-sm font-medium text-gray-100">{agent.name}</p>
                  {roleMeta && (
                    <p className="text-xs" style={{ color: roleMeta.color }}>
                      {roleMeta.label}
                    </p>
                  )}
                </div>
              </div>
              <div className="flex items-center gap-1.5">
                {/* More than one companion agent on this page (e.g. Muse + Sprite) — switch between them */}
                {candidates.length > 1 &&
                  candidates
                    .filter((c) => c.id !== agent.id)
                    .map((c) => (
                      <button
                        key={c.id}
                        type="button"
                        onClick={() => setSelectedId(c.id)}
                        title={t("openChat", { agentName: c.name })}
                        className="flex items-center gap-1.5 rounded-full border border-white/[0.08] bg-white/[0.03] py-1 pl-1 pr-2.5 transition-colors hover:bg-white/[0.08]"
                      >
                        <Avatar name={c.name} size="sm" />
                        <span className="text-xs text-gray-300">{c.name}</span>
                      </button>
                    ))}
                <button
                  type="button"
                  onClick={() => setOpen(false)}
                  aria-label={t("closeChat")}
                  className="rounded-lg p-1.5 text-gray-400 transition-colors hover:bg-white/[0.06] hover:text-gray-300"
                >
                  <svg width="14" height="14" viewBox="0 0 16 16" fill="none">
                    <path d="M12 4L4 12M4 4l8 8" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
                  </svg>
                </button>
              </div>
            </div>
            <div className="min-h-0 flex-1">
              {/* Keyed by agent so switching companions resets conversation state */}
              <AgentChatTab
                key={agent.id}
                agent={agent}
                projectId={projectId}
                initialAssetId={attach?.assetId ?? null}
                designFolderPath={designFolderPath}
              />
            </div>
          </motion.div>
        )}
      </AnimatePresence>
    </>
  );
}

export { AgentChatDrawer };
export type { AttachRequest };
