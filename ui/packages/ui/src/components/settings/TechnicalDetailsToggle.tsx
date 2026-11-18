import { useTranslations } from "use-intl";
import { Toggle } from "@/components/ui/Toggle";
import { useTechnicalDetails } from "@/hooks/use-technical-details";

/** Simple vs. advanced mode switch, persisted per member on the backend. */
function TechnicalDetailsToggle() {
  const t = useTranslations("dashboard");
  const { showTechnicalDetails, setShowTechnicalDetails } = useTechnicalDetails();

  return (
    <div className="flex items-start justify-between gap-3">
      <div className="min-w-0">
        <p className="text-sm font-medium text-white">
          {t("preferencesPage.technicalDetails.label")}
        </p>
        <p className="mt-0.5 text-xs text-gray-400">
          {t("preferencesPage.technicalDetails.description")}
        </p>
      </div>
      <Toggle
        checked={showTechnicalDetails}
        onChange={(next) => void setShowTechnicalDetails(next)}
      />
    </div>
  );
}

export { TechnicalDetailsToggle };
