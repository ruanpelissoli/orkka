import { useState } from "react";
import { useAuth } from "@/lib/local-auth-client";
import { useTranslations } from "use-intl";
import { Button, Modal } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { AddRepoByUrlForm } from "@/components/repos/AddRepoByUrlForm";
import { api, invalidateApiCache } from "@/lib/api";
import type { AddProjectRepoRequest } from "@/lib/types";
import { refreshData } from "@/lib/data-refresh";

/** The add-by-URL form wired to the project: adds the repo and refreshes the page. */
export function AddRepoByUrlPanel({
  projectId,
  onAdded,
  heading = true,
}: {
  projectId: string;
  onAdded?: () => void;
  /** Off inside a modal that already carries the title. */
  heading?: boolean;
}) {
  const t = useTranslations("dashboard.addRepoByUrl");
  const { getToken } = useAuth();

  async function add(request: AddProjectRepoRequest) {
    const token = await getToken();
    await api.repos.add(projectId, request, token);
    invalidateApiCache();
    refreshData();
    onAdded?.();
  }

  return (
    <RequirePermission permission="ReposManage">
      <div className="space-y-2">
        <div>
          {heading && <h3 className="text-sm font-medium text-gray-100">{t("title")}</h3>}
          <p className="mt-1 text-xs text-gray-400">{t("intro")}</p>
        </div>
        <AddRepoByUrlForm onAdd={add} />
      </div>
    </RequirePermission>
  );
}

/** A button that opens the add-by-URL form in a modal — for the empty repos page. */
export function AddRepoByUrlButton({ projectId }: { projectId: string }) {
  const t = useTranslations("dashboard.addRepoByUrl");
  const [open, setOpen] = useState(false);

  return (
    <RequirePermission permission="ReposManage">
      <Button size="sm" onClick={() => setOpen(true)}>
        {t("title")}
      </Button>
      <Modal open={open} onClose={() => setOpen(false)} title={t("title")}>
        <AddRepoByUrlPanel
          projectId={projectId}
          heading={false}
          onAdded={() => setOpen(false)}
        />
      </Modal>
    </RequirePermission>
  );
}
