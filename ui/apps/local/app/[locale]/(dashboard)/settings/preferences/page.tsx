import { useTranslations } from "use-intl";
import { GlassCard } from "@/components/ui";
import { TechnicalDetailsToggle } from "@/components/settings/TechnicalDetailsToggle";
import { ReplayTourButton } from "@/components/settings/ReplayTourButton";
import { useDocumentTitle } from "@/hooks/use-document-title";

export default function PreferencesPage() {
  const t = useTranslations("dashboard");
  useDocumentTitle(t("meta.preferences"));

  return (
    <div className="space-y-6">
      <div>
        <h2 className="font-display text-lg font-semibold text-white">
          {t("preferencesPage.title")}
        </h2>
        <p className="mt-1 text-sm text-gray-400">
          {t("preferencesPage.description")}
        </p>
      </div>

      <GlassCard padding="md">
        <div className="divide-y divide-white/[0.06]">
          <div className="pb-4">
            <TechnicalDetailsToggle />
          </div>
          <div className="pt-4">
            <ReplayTourButton />
          </div>
        </div>
      </GlassCard>
    </div>
  );
}
