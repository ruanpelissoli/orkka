import { useCallback, useEffect, useState } from "react";
import { useTranslations } from "use-intl";
import { Link } from "@/i18n/navigation";
import { cn } from "@/lib/utils";
import { GlassCard } from "@/components/ui";
import { api } from "@/lib/api";
import { useClientApi } from "@/hooks/use-client-api";
import type { TaskGroup, TaskGroupMember } from "@/lib/types";
import { StoryPanel } from "./StoryPanel";

/**
 * One cross-repo task, seen whole.
 *
 * Its members are ordinary tasks scattered across lanes and repos, so the board can show each
 * half but never the thing they add up to. The two properties that make them one piece of work
 * live here and nowhere else: the contract every half is coded against, and the gate holding
 * their pull requests until they can land together. Without this view, "everything is Done but
 * nothing has merged" has no explanation anywhere in the product.
 *
 * Renders nothing for an ordinary task, which is nearly all of them.
 */

interface TaskGroupPanelProps {
  groupId: string | null | undefined;
  projectId: string;
  canEdit: boolean;
  /** The task being viewed — a Story marks it as "this one". */
  currentTaskId?: string | null;
}

function MemberRow({ member, projectId }: { member: TaskGroupMember; projectId: string }) {
  const t = useTranslations("dashboard.taskGroup");

  // Three states worth telling apart, because each needs a different response: landed, never
  // going to land on its own, still on its way.
  const stuck = member.prClosed || member.mergeFailureReason !== null;

  return (
    <li className="flex items-center gap-2 rounded-lg px-2 py-1.5 transition-colors hover:bg-white/[0.03]">
      <span
        className="h-2 w-2 shrink-0 rounded-full"
        style={{ backgroundColor: member.repoColor ?? "#6b7280" }}
        aria-hidden
      />
      <Link
        href={`/p/${projectId}/tasks/${member.number}`}
        className="min-w-0 flex-1"
        title={member.title}
      >
        <span className="font-mono text-xs text-gray-400">{member.code}</span>
        <span className="ml-1.5 text-xs text-gray-300 transition-colors hover:text-white">
          {member.title}
        </span>
      </Link>

      {member.repoName && (
        <span className="shrink-0 text-label text-gray-400">{member.repoName}</span>
      )}

      <span
        className={cn(
          "shrink-0 rounded-full px-2 py-0.5 text-label",
          member.isMerged
            ? "bg-emerald-500/10 text-emerald-300"
            : stuck
              ? "bg-red-500/10 text-red-300"
              : "bg-white/[0.06] text-gray-300"
        )}
      >
        {member.isMerged ? t("merged") : stuck ? t("stuck") : member.currentLane}
      </span>
    </li>
  );
}

function TaskGroupPanel({ groupId, projectId, canEdit, currentTaskId }: TaskGroupPanelProps) {
  const t = useTranslations("dashboard.taskGroup");
  const withToken = useClientApi();
  const [loadedGroup, setGroup] = useState<TaskGroup | null>(null);
  // No group id means no group, whatever an earlier id loaded.
  const group = groupId ? loadedGroup : null;
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState("");
  const [saving, setSaving] = useState(false);
  const [resolving, setResolving] = useState<string | null>(null);

  const load = useCallback(() => {
    if (!groupId) return Promise.resolve();
    return withToken((token) => api.taskGroups.get(projectId, groupId, token))
      .then(setGroup)
      // Supplementary card: a failed fetch stays silent rather than putting an error where
      // the user expects information.
      .catch(() => setGroup(null));
  }, [groupId, projectId, withToken]);

  useEffect(() => {
    if (!groupId) return;
    let cancelled = false;
    withToken((token) => api.taskGroups.get(projectId, groupId, token))
      .then((g) => {
        if (!cancelled) setGroup(g);
      })
      .catch(() => {
        if (!cancelled) setGroup(null);
      });
    return () => {
      cancelled = true;
    };
  }, [groupId, projectId, withToken]);

  if (!group) return null;

  // A tracker Story is a different shape of group: ordered sub-tasks on one branch, one pull
  // request — no contract, no merge gate.
  if (group.kind === "Story")
    return <StoryPanel group={group} projectId={projectId} currentTaskId={currentTaskId} />;

  // Once the work has settled the contract records what the halves were built against, rather
  // than instructing them. Editing it then would misreport what shipped.
  const settled = group.status === "Merged" || group.status === "Cancelled";

  const pendingRequests = group.repoRequests.filter((r) => r.status === "Pending");

  const handleResolve = async (repoName: string, approve: boolean) => {
    if (!groupId) return;
    setResolving(repoName);
    try {
      await withToken((token) =>
        api.taskGroups.resolveRepoRequest(projectId, groupId, repoName, approve, token)
      );
      await load();
    } finally {
      setResolving(null);
    }
  };

  const handleSave = async () => {
    if (!groupId) return;
    setSaving(true);
    try {
      await withToken((token) =>
        api.taskGroups.updateContract(projectId, groupId, draft, token)
      );
      setEditing(false);
      await load();
    } finally {
      setSaving(false);
    }
  };

  return (
    <GlassCard padding="md">
      <div className="mb-3 flex items-start justify-between gap-3">
        <div className="min-w-0">
          <h4 className="text-label">{t("title")}</h4>
          <p className="mt-0.5 text-sm text-gray-300">{group.title}</p>
        </div>
        <span
          className={cn(
            "shrink-0 rounded-full px-2 py-0.5 text-label",
            group.status === "Merged"
              ? "bg-emerald-500/10 text-emerald-300"
              : group.status === "PartiallyMerged"
                ? "bg-red-500/10 text-red-300"
                : "bg-white/[0.06] text-gray-300"
          )}
        >
          {group.status}
        </span>
      </div>

      {/* The honest caveat, stated where the consequence lands rather than in a doc nobody
          opens: a merged pull request cannot be un-merged, so a group can genuinely end up
          half-landed and no amount of retrying fixes it. */}
      {group.status === "PartiallyMerged" && (
        <p className="mb-3 rounded-lg bg-red-500/10 px-3 py-2 text-xs text-red-300">
          {t("partiallyMerged")}
        </p>
      )}

      <ul className="space-y-0.5">
        {group.members.map((member) => (
          <MemberRow key={member.id} member={member} projectId={projectId} />
        ))}
      </ul>

      {/* An agent hit a wall it cannot get past from its own worktree. Approving widens the
          task's blast radius and its bill and adds another half for the gate to wait on, which
          is exactly why the agent was not allowed to do it itself. */}
      {pendingRequests.length > 0 && (
        <div className="mt-3 space-y-2">
          {pendingRequests.map((request) => (
            <div
              key={request.repoName}
              className="rounded-lg bg-amber-500/10 px-3 py-2"
            >
              <p className="text-xs text-amber-200">
                {t("repoRequested", { repo: request.repoName })}
              </p>
              <p className="mt-1 text-xs text-gray-300">{request.reason}</p>
              {canEdit && (
                <div className="mt-2 flex gap-2">
                  <button
                    type="button"
                    disabled={resolving !== null}
                    onClick={() => handleResolve(request.repoName, true)}
                    className="rounded-lg bg-primary-500/20 px-3 py-1 text-xs text-primary-200 transition-colors hover:bg-primary-500/30 disabled:opacity-50"
                  >
                    {t("approveRepo")}
                  </button>
                  <button
                    type="button"
                    disabled={resolving !== null}
                    onClick={() => handleResolve(request.repoName, false)}
                    className="rounded-lg px-3 py-1 text-xs text-gray-400 transition-colors hover:text-white disabled:opacity-50"
                  >
                    {t("declineRepo")}
                  </button>
                </div>
              )}
            </div>
          ))}
        </div>
      )}

      {group.mergePolicy === "Gated" && !group.ready && group.blockedBy && (
        <p className="mt-3 text-xs text-amber-400/80">
          {t("gateHolding", { reason: group.blockedBy })}
        </p>
      )}

      {group.mergePolicy === "Gated" && group.ready && group.status !== "Merged" && (
        <p className="mt-3 text-xs text-emerald-300/80">{t("gateReady")}</p>
      )}

      <div className="mt-4">
        <div className="mb-1.5 flex items-center justify-between">
          <span className="text-label">{t("contract")}</span>
          {canEdit && !settled && !editing && (
            <button
              type="button"
              onClick={() => {
                setDraft(group.contract ?? "");
                setEditing(true);
              }}
              className="text-xs text-gray-400 transition-colors hover:text-white"
            >
              {t("editContract")}
            </button>
          )}
        </div>

        {editing ? (
          <div className="space-y-2">
            <textarea
              value={draft}
              onChange={(e) => setDraft(e.target.value)}
              rows={12}
              className={cn(
                "w-full rounded-xl px-3 py-2 font-mono text-xs text-gray-100",
                "bg-white/[0.03] border border-white/[0.06]",
                "focus:bg-white/[0.06] focus:border-primary-500/50 focus:outline-none"
              )}
            />
            <p className="text-xs text-gray-400">{t("editHint")}</p>
            <div className="flex gap-2">
              <button
                type="button"
                onClick={handleSave}
                disabled={saving}
                className="rounded-lg bg-primary-500/20 px-3 py-1.5 text-xs text-primary-200 transition-colors hover:bg-primary-500/30 disabled:opacity-50"
              >
                {t("saveContract")}
              </button>
              <button
                type="button"
                onClick={() => setEditing(false)}
                disabled={saving}
                className="rounded-lg px-3 py-1.5 text-xs text-gray-400 transition-colors hover:text-white"
              >
                {t("cancel")}
              </button>
            </div>
          </div>
        ) : group.contract ? (
          <pre className="max-h-80 overflow-auto whitespace-pre-wrap rounded-xl bg-white/[0.02] px-3 py-2 font-mono text-xs text-gray-300">
            {group.contract}
          </pre>
        ) : (
          <p className="text-xs text-gray-400">{t("noContract")}</p>
        )}
      </div>
    </GlassCard>
  );
}

export { TaskGroupPanel };
