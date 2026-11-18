import { useTranslations } from "use-intl";
import { DashboardSkeleton, PageError } from "@/components/page";
import { useApiQuery } from "@/hooks/use-api-query";
import { useDocumentTitle } from "@/hooks/use-document-title";
import { api } from "@/lib/api";
import { MachineClient } from "./MachineClient";

export default function MachineSettingsPage() {
  const t = useTranslations("dashboard");
  useDocumentTitle(t("meta.machine"));

  const { data, error, reload } = useApiQuery(async () => {
    const [status, tokens] = await Promise.all([
      api.runner.status(""),
      api.runner.tokens.list(""),
    ]);
    return { status, tokens };
  }, []);

  if (error) return <PageError error={error} onRetry={reload} />;
  if (!data) return <DashboardSkeleton />;

  return <MachineClient initialStatus={data.status} initialTokens={data.tokens} />;
}
