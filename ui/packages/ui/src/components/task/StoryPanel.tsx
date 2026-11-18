import { useTranslations } from "use-intl";
import { Link } from "@/i18n/navigation";
import { cn } from "@/lib/utils";
import { GlassCard } from "@/components/ui";
import type { TaskGroup, TaskGroupBranch, TaskGroupMember } from "@/lib/types";

/**
 * A tracker Story, seen whole: its sub-tasks in order (landed / this one / pending), the one
 * branch they all commit on, and the one pull request per repository that the story becomes —
 * a draft after the first sub-task lands, ready for review after the last.
 */

interface StoryPanelProps {
  group: TaskGroup;
  projectId: string;
  currentTaskId?: string | null;
}

/** "landed" once the member's work is on the shared branch (it passed review). */
function memberState(member: TaskGroupMember, currentTaskId?: string | null): "landed" | "current" | "pending" {
  if (member.isMerged) return "landed";
  if (member.id === currentTaskId) return "current";
  return "pending";
}

const MARKERS = { landed: "✓", current: "▶", pending: "○" } as const;

function PullRequestRow({ branch }: { branch: TaskGroupBranch }) {
  const t = useTranslations("dashboard.taskGroup");
  const label =
    branch.prState === "draft"
      ? t("prDraft")
      : branch.prState === "ready"
        ? t("prReady")
        : branch.prState === "merged"
          ? t("prMerged")
          : branch.prState === "closed"
            ? t("prClosed")
            : null;

  return (
    <li className="flex items-center gap-2 text-xs" data-testid="story-pr">
      {branch.repoName && <span className="shrink-0 text-gray-400">{branch.repoName}</span>}
      {branch.prUrl ? (
        <a
          href={branch.prUrl}
          target="_blank"
          rel="noreferrer"
          className="min-w-0 flex-1 truncate text-primary-300 hover:text-primary-200"
        >
          {branch.prUrl}
        </a>
      ) : (
        <span className="min-w-0 flex-1 text-gray-400">
          {branch.prState === "merged" ? t("storyFolder") : t("storyNoPr")}
        </span>
      )}
      {label && branch.prUrl && (
        <span
          className={cn(
            "shrink-0 rounded-full px-2 py-0.5 text-label",
            branch.prState === "ready" || branch.prState === "merged"
              ? "bg-emerald-500/10 text-emerald-300"
              : branch.prState === "closed"
                ? "bg-red-500/10 text-red-300"
                : "bg-white/[0.06] text-gray-300"
          )}
        >
          {label}
        </span>
      )}
    </li>
  );
}

function StoryPanel({ group, projectId, currentTaskId }: StoryPanelProps) {
  const t = useTranslations("dashboard.taskGroup");
  const members = [...group.members].sort((a, b) => (a.storyOrder ?? 0) - (b.storyOrder ?? 0));
  const branches = group.branches ?? [];

  return (
    <GlassCard padding="md" data-testid="story-panel">
      <div className="mb-2 flex items-start justify-between gap-3">
        <div className="min-w-0">
          <h4 className="text-label">{t("storyTitle")}</h4>
          <p className="mt-0.5 text-sm text-gray-300">
            {group.externalKey && group.externalUrl ? (
              <a
                href={group.externalUrl}
                target="_blank"
                rel="noreferrer"
                className="mr-1.5 font-mono text-xs text-primary-300 hover:text-primary-200"
              >
                {group.externalKey}
              </a>
            ) : null}
            {group.title}
          </p>
        </div>
      </div>
      <p className="mb-3 text-xs text-gray-400">{t("storyIntro")}</p>

      <ol className="space-y-0.5">
        {members.map((member) => {
          const state = memberState(member, currentTaskId);
          return (
            <li
              key={member.id}
              data-testid="story-member"
              data-state={state}
              className={cn(
                "flex items-center gap-2 rounded-lg px-2 py-1.5 transition-colors hover:bg-white/[0.03]",
                state === "current" && "bg-primary-500/[0.06]"
              )}
            >
              <span
                className={cn(
                  "w-4 shrink-0 text-center text-xs",
                  state === "landed" ? "text-emerald-300" : state === "current" ? "text-primary-300" : "text-gray-400"
                )}
                aria-label={
                  state === "landed" ? t("storyLanded") : state === "current" ? t("storyCurrent") : t("storyPending")
                }
                role="img"
              >
                {MARKERS[state]}
              </span>
              <Link href={`/p/${projectId}/tasks/${member.number}`} className="min-w-0 flex-1" title={member.title}>
                <span className="font-mono text-xs text-gray-400">{member.code}</span>
                <span className="ml-1.5 text-xs text-gray-300 transition-colors hover:text-white">{member.title}</span>
              </Link>
              {member.repoName && <span className="shrink-0 text-label text-gray-400">{member.repoName}</span>}
              <span className="shrink-0 rounded-full bg-white/[0.06] px-2 py-0.5 text-label text-gray-300">
                {state === "landed" ? t("storyLanded") : member.currentLane}
              </span>
            </li>
          );
        })}
      </ol>

      {group.branchName && (
        <div className="mt-3">
          <span className="text-label">{t("storyBranch")}</span>
          <p className="mt-0.5 font-mono text-xs text-gray-300" data-testid="story-branch">
            {group.branchName}
          </p>
        </div>
      )}

      <div className="mt-3">
        <span className="text-label">{t("storyPullRequest")}</span>
        {branches.length > 0 ? (
          <ul className="mt-1 space-y-1">
            {branches.map((branch) => (
              <PullRequestRow key={branch.repoId} branch={branch} />
            ))}
          </ul>
        ) : (
          <p className="mt-0.5 text-xs text-gray-400">{t("storyNoPr")}</p>
        )}
      </div>
    </GlassCard>
  );
}

export { StoryPanel };
