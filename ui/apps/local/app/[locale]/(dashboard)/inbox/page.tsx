import { useTranslations } from "use-intl";
import { InboxPageClient } from "@/components/inbox/InboxPageClient";
import { useDocumentTitle } from "@/hooks/use-document-title";

export default function InboxPage() {
  const t = useTranslations("dashboard");
  useDocumentTitle(t("meta.inbox"));
  return <InboxPageClient />;
}
