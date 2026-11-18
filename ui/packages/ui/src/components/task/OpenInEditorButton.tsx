import { useEffect, useState } from "react";
import { useTranslations } from "use-intl";
import { Dropdown } from "@/components/ui";
import { useClientApi } from "@/hooks/use-client-api";
import { api, extractErrorMessage } from "@/lib/api";
import { cn } from "@/lib/utils";
import type { EditorOption } from "@/lib/types";

/** What to open: a task's worktree (the agent's branch) or a repository on its default branch. */
type EditorTarget =
  | { kind: "task"; taskId: string }
  | { kind: "repo"; projectId: string; repoId: string };

interface OpenInEditorButtonProps {
  /** Shorthand for `target={{ kind: "task", taskId }}` — the original prop. */
  taskId?: string;
  target?: EditorTarget;
  className?: string;
  /** Hide the trailing hint text (compact placements like a repo card). */
  compact?: boolean;
}

/**
 * The escape hatch: hands a task's worktree — or the repo itself — to a desktop editor so the
 * human can finish (or correct) the work directly instead of describing the fix back to the
 * agent. Renders nothing unless a runner is connected and reports an installed editor — a
 * dead button here would be worse than no button.
 *
 * The API only answers for tasks that actually ran on the caller's own machine (a cloud task's worktree lives on platform infrastructure,
 * and a colleague's laptop is not ours to launch processes on) and, for repos, only for the
 * caller's own runner — so the same "renders nothing when unsupported" rule quietly covers
 * those cases too.
 */
function OpenInEditorButton({ taskId, target, className, compact }: OpenInEditorButtonProps) {
  const t = useTranslations("dashboard.editor");
  const withToken = useClientApi();

  const resolved: EditorTarget | null =
    target ?? (taskId ? { kind: "task", taskId } : null);
  const targetKey = resolved
    ? resolved.kind === "task"
      ? `task:${resolved.taskId}`
      : `repo:${resolved.projectId}:${resolved.repoId}`
    : null;

  const [editors, setEditors] = useState<EditorOption[]>([]);
  const [isOpening, setIsOpening] = useState(false);
  const [status, setStatus] = useState<string | null>(null);

  useEffect(() => {
    if (!resolved) return;
    let cancelled = false;
    const list =
      resolved.kind === "task"
        ? (token: string) => api.editors.list(resolved.taskId, token)
        : (token: string) => api.editors.listForRepo(resolved.projectId, resolved.repoId, token);
    withToken(list)
      .then((result) => {
        if (!cancelled && result.supported) setEditors(result.editors);
      })
      .catch(() => {
        // Runner offline or an older build — stay hidden.
      });
    return () => {
      cancelled = true;
    };
    // resolved is derived from taskId/target; targetKey is its stable identity.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [withToken, targetKey]);

  if (!resolved || editors.length === 0) return null;

  const open = async (editorKey?: string) => {
    setIsOpening(true);
    setStatus(null);
    try {
      const result = await withToken((token) =>
        resolved.kind === "task"
          ? api.editors.openTask(resolved.taskId, token, editorKey)
          : api.editors.openRepo(resolved.projectId, resolved.repoId, token, editorKey)
      );
      setStatus(t("opened", { editor: result.openedWith }));
    } catch (error) {
      setStatus(extractErrorMessage(error, t("failed")));
    } finally {
      setIsOpening(false);
    }
  };

  const buttonClasses = cn(
    "rounded-lg bg-white/[0.04] px-3 py-1.5 text-xs font-medium text-gray-300",
    "transition-colors hover:bg-white/[0.08] hover:text-white disabled:opacity-50"
  );

  return (
    <div className={cn("flex items-center gap-2", className)}>
      {editors.length === 1 ? (
        <button
          type="button"
          disabled={isOpening}
          onClick={() => void open(editors[0].key)}
          className={buttonClasses}
        >
          {isOpening ? t("opening") : t("openWith", { editor: editors[0].displayName })}
        </button>
      ) : (
        <Dropdown
          trigger={
            <span className={buttonClasses}>{isOpening ? t("opening") : t("open")}</span>
          }
          items={editors.map((editor) => ({ key: editor.key, label: editor.displayName }))}
          onSelect={(key) => void open(key)}
        />
      )}

      {status ? (
        <span className="text-xs text-gray-400">{status}</span>
      ) : compact ? null : (
        <span className="hidden text-xs text-gray-400 sm:inline">
          {resolved.kind === "repo" ? t("repoHint") : t("hint")}
        </span>
      )}
    </div>
  );
}

export { OpenInEditorButton };
export type { OpenInEditorButtonProps, EditorTarget };
