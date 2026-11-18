import { useTranslations } from "use-intl";
import { CopyableCommand, GlassCard } from "@/components/ui";
import { WorkspaceNameCard } from "@/components/settings/WorkspaceNameCard";
import { useDocumentTitle } from "@/hooks/use-document-title";

/** The current workspace (one per job): its name, and where its git identity is set. */
export default function WorkspaceSettingsPage() {
  const tMeta = useTranslations("dashboard");
  const t = useTranslations("dashboard.workspaceSettings");
  useDocumentTitle(tMeta("meta.workspace"));

  return (
    <div className="space-y-4">
      <WorkspaceNameCard />
      <GlassCard padding="md">
        <h3 className="font-display text-sm font-semibold text-white">{t("gitIdentityTitle")}</h3>
        <p className="mt-1 max-w-xl text-xs text-gray-400">{t("gitIdentityBody")}</p>
        <div className="mt-3">
          <CopyableCommand command="./orkka workspace set-identity" />
        </div>
      </GlassCard>
    </div>
  );
}
