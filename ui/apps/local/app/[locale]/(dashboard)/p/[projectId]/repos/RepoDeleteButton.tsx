import { useState, useTransition } from "react";
import { useTranslations } from "use-intl";
import { Button, ConfirmDialog } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { removeRepo } from "@/lib/actions";

export function RepoDeleteButton({
  repoId,
}: {
  repoId: string;
}) {
  const t = useTranslations("dashboard.reposPage");
  const tActions = useTranslations("common.actions");
  const [isPending, startTransition] = useTransition();
  const [confirmingDelete, setConfirmingDelete] = useState(false);

  function handleDelete() {
    setConfirmingDelete(false);
    startTransition(() => removeRepo(repoId));
  }

  return (
    <RequirePermission permission="ReposManage">
    <Button
      variant="ghost"
      size="sm"
      onClick={() => setConfirmingDelete(true)}
      disabled={isPending}
      aria-label={t("removeRepo")}
    >
      {isPending ? (
        <span className="h-3.5 w-3.5 animate-spin rounded-full border-2 border-gray-500/20 border-t-gray-400" />
      ) : (
        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" className="text-gray-400 hover:text-red-400">
          <path d="M3 6h18M19 6v14a2 2 0 01-2 2H7a2 2 0 01-2-2V6m3 0V4a2 2 0 012-2h4a2 2 0 012 2v2" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      )}
    </Button>
    <ConfirmDialog
      open={confirmingDelete}
      title={t("removeRepo")}
      message={t("removeConfirm")}
      confirmLabel={t("remove")}
      cancelLabel={tActions("cancel")}
      danger
      pending={isPending}
      onConfirm={handleDelete}
      onCancel={() => setConfirmingDelete(false)}
    />
    </RequirePermission>
  );
}
