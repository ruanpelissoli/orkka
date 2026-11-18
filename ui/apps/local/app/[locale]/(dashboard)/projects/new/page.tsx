import { useTranslations } from "use-intl";
import { GlassCard } from "@/components/ui";
import { useDocumentTitle } from "@/hooks/use-document-title";
import { NewProjectClient } from "./NewProjectClient";

export default function NewProjectPage() {
  const t = useTranslations("dashboard.newProjectPage");
  useDocumentTitle(t("title"));
  return (
    <div className="mx-auto w-full max-w-2xl p-4 lg:p-8">
      <h1 className="font-display text-2xl font-bold text-white">{t("title")}</h1>
      <p className="mt-1 mb-6 text-sm text-gray-400">{t("subtitle")}</p>
      <GlassCard padding="lg">
        <NewProjectClient />
      </GlassCard>
    </div>
  );
}
