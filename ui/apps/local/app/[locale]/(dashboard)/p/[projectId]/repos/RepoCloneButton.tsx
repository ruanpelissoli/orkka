import { useState, useTransition } from "react";
import { useAuth } from "@/lib/local-auth-client";
import { useTranslations } from "use-intl";
import { api } from "@/lib/api";
import { Button, Modal } from "@/components/ui";
import { CodeBlock } from "@/components/runner/CodeBlock";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import type { RepoCloneAccess } from "@/lib/types";

/**
 * Gets a repository onto the developer's own machine so they can run and test it. The server
 * hands back the plain remote — every repo lives on the customer's own forge, so their own git
 * credentials already work.
 */
export function RepoCloneButton({
  projectId,
  repoId,
  repoName,
}: {
  projectId: string;
  repoId: string;
  repoName: string;
}) {
  const t = useTranslations("dashboard.reposPage");
  const { getToken } = useAuth();
  const [isPending, startTransition] = useTransition();
  const [access, setAccess] = useState<RepoCloneAccess | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [open, setOpen] = useState(false);

  function handleOpen() {
    setOpen(true);
    setError(null);
    startTransition(async () => {
      try {
        const token = await getToken();
        setAccess(await api.repos.cloneAccess(projectId, repoId, token));
      } catch (e) {
        setAccess(null);
        setError(e instanceof Error ? e.message : t("cloneFailed"));
      }
    });
  }

  function handleClose() {
    setOpen(false);
    setAccess(null);
  }

  return (
    <RequirePermission permission="ReposManage">
      <Button
        variant="ghost"
        size="sm"
        onClick={handleOpen}
        aria-label={t("cloneRepo")}
      >
        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" className="text-gray-400 hover:text-gray-200">
          <path d="M8 17l-5-5 5-5M16 7l5 5-5 5" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      </Button>

      <Modal open={open} onClose={handleClose} title={t("cloneTitle", { name: repoName })}>
        <div className="space-y-4">
          {isPending && !access && (
            <p className="text-sm text-gray-400">{t("clonePreparing")}</p>
          )}

          {error && <p className="text-sm text-red-300">{error}</p>}

          {access && (
            <>
              <CodeBlock code={access.command} />

              <p className="text-xs text-gray-400">
                {t("cloneDefaultBranch", { branch: access.defaultBranch })}
              </p>
            </>
          )}
        </div>
      </Modal>
    </RequirePermission>
  );
}
