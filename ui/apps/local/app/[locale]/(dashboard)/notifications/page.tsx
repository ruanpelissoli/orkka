import { useTranslations } from "use-intl";
import { useDocumentTitle } from "@/hooks/use-document-title";
import { NotificationsPageClient } from "./NotificationsPageClient";

export default function NotificationsPage() {
  const t = useTranslations("dashboard");
  useDocumentTitle(t("meta.notifications"));
  return <NotificationsPageClient />;
}
