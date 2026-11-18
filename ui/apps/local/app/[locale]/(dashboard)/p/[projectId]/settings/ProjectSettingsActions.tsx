import { useContext, useState, useTransition } from "react";
import { useTranslations } from "use-intl";
import { useRouter } from "@/i18n/navigation";
import { GlassCard, Input, Textarea, Button, Modal } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { ProjectContext } from "@/components/providers/ProjectProvider";
import { updateProject, deleteProject } from "@/lib/actions";
import type { Project } from "@/lib/types";

export function ProjectSettingsActions({
  project,
  dangerZone,
}: {
  project: Project;
  dangerZone?: boolean;
}) {
  const t = useTranslations("dashboard");
  const tc = useTranslations("common");
  const router = useRouter();
  // Optional: the sidebar's project list, refreshed after a delete.
  const projects = useContext(ProjectContext);
  const [isPending, startTransition] = useTransition();
  // 0 = closed; steps 1 and 2 are confirmations, step 3 requires typing the name
  const [deleteStep, setDeleteStep] = useState(0);
  const [confirmName, setConfirmName] = useState("");

  const nameMatches = confirmName === project.name;

  function handleClose() {
    if (isPending) return;
    setDeleteStep(0);
    setConfirmName("");
  }

  if (dangerZone) {
    return (
      <RequirePermission permission="ProjectsDelete">
        <GlassCard padding="lg" className="border-red-500/20">
          <h2 className="font-display text-lg font-semibold text-red-400 mb-2">
            {t("settingsPage.danger.title")}
          </h2>
          <p className="text-sm text-gray-400 mb-4">
            {t("settingsPage.danger.warning")}
          </p>
          <Button
            variant="danger"
            size="md"
            onClick={() => setDeleteStep(1)}
          >
            {t("settingsPage.danger.deleteProject")}
          </Button>
        </GlassCard>

        <Modal open={deleteStep > 0} onClose={handleClose} title={t("settingsPage.danger.deleteProject")}>
          <div className="space-y-4">
            <div className="rounded-lg bg-red-500/10 border border-red-500/20 p-3">
              <p className="text-sm text-red-400">
                <span className="font-semibold">
                  {t("settingsPage.danger.permanentLead")}
                </span>{" "}
                {t("settingsPage.danger.permanent")}
              </p>
            </div>

            {deleteStep === 1 && (
              <p className="text-sm text-gray-300">
                {t.rich("settingsPage.danger.confirmFirst", {
                  name: () => (
                    <span className="font-mono font-semibold text-gray-100">
                      {project.name}
                    </span>
                  ),
                })}
              </p>
            )}

            {deleteStep === 2 && (
              <p className="text-sm text-gray-300">
                {t("settingsPage.danger.confirmSecond")}
              </p>
            )}

            {deleteStep === 3 && (
              <>
                <p className="text-sm text-gray-300">
                  {t.rich("settingsPage.danger.confirmPrompt", {
                    name: () => (
                      <span className="font-mono font-semibold text-gray-100">
                        {project.name}
                      </span>
                    ),
                  })}
                </p>

                <Input
                  placeholder={project.name}
                  value={confirmName}
                  onChange={(e) => setConfirmName(e.target.value)}
                  autoFocus
                />
              </>
            )}

            <div className="flex gap-3 justify-end pt-2">
              <Button
                variant="secondary"
                size="md"
                onClick={handleClose}
                disabled={isPending}
              >
                {tc("actions.cancel")}
              </Button>
              {deleteStep < 3 ? (
                <Button
                  variant="danger"
                  size="md"
                  onClick={() => setDeleteStep(deleteStep + 1)}
                >
                  {deleteStep === 1
                    ? t("settingsPage.danger.confirmFirstContinue")
                    : t("settingsPage.danger.confirmSecondContinue")}
                </Button>
              ) : (
                <Button
                  variant="danger"
                  size="md"
                  disabled={!nameMatches || isPending}
                  onClick={() => {
                    startTransition(async () => {
                      const next = await deleteProject(project.id);
                      // The sidebar and project switcher read this list.
                      void projects?.refreshProjects();
                      router.push(next);
                    });
                  }}
                >
                  {isPending
                    ? t("settingsPage.danger.deleting")
                    : t("settingsPage.danger.deleteProject")}
                </Button>
              )}
            </div>
          </div>
        </Modal>
      </RequirePermission>
    );
  }

  return (
    <form
      action={(formData) => {
        startTransition(() => updateProject(project.id, formData));
      }}
      className="space-y-4"
    >
      <Input
        label={t("settingsPage.form.name")}
        name="name"
        placeholder={t("settingsPage.form.namePlaceholder")}
        defaultValue={project.name}
      />
      <Textarea
        label={t("settingsPage.form.description")}
        name="description"
        placeholder={t("settingsPage.form.descriptionPlaceholder")}
        defaultValue={project.description ?? ""}
      />
      <RequirePermission permission="ProjectsManage">
        <Button size="md" disabled={isPending}>
          {isPending
            ? t("settingsPage.form.saving")
            : t("settingsPage.form.save")}
        </Button>
      </RequirePermission>
    </form>
  );
}
