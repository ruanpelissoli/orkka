import { useTranslations } from "use-intl";
import { ApiKeysPanel } from "@/components/settings/ApiKeysPanel";
import { DashboardSkeleton } from "@/components/page";
import { useApiQuery } from "@/hooks/use-api-query";
import { useDocumentTitle } from "@/hooks/use-document-title";
import { api } from "@/lib/api";

export default function ApiKeysSettingsPage() {
  const t = useTranslations("dashboard");
  useDocumentTitle(t("meta.mcpKeys"));

  const { data: projects } = useApiQuery(() => api.projects.list("").catch(() => []), []);

  if (!projects) return <DashboardSkeleton />;

  // The MCP endpoint is on the local host — the origin this page was served from. Shown as an
  // absolute URL for the AI CLI configs.
  return <ApiKeysPanel apiBaseUrl={typeof window === "undefined" ? "" : window.location.origin} projects={projects} />;
}
