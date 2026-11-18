import { useState } from "react";
import { useTranslations } from "use-intl";
import { useRouter } from "@/i18n/navigation";
import { GlassCard } from "@/components/ui";
import { useInbox } from "@/hooks/use-inbox";
import { cn } from "@/lib/utils";
import { InboxList } from "./InboxList";
import { inboxItemHref } from "./inbox-routes";
import type { InboxItem } from "@/lib/types";

interface InboxPageClientProps {
  /** Scope to a single project; omitted for the cross-project view. */
  projectId?: string;
}

/**
 * The Attention Inbox screen: a ranked queue plus two filters.
 */
function InboxPageClient({ projectId }: InboxPageClientProps) {
  const t = useTranslations("dashboard.inbox");
  const router = useRouter();

  const [scope, setScope] = useState<"all" | "mine">("all");
  const [kind, setKind] = useState<string | null>(null);

  const { items, counts, total, isLoading, error, refetch } = useInbox({
    scope,
    projectId,
    kind: kind ?? undefined,
  });

  const handleSelect = (item: InboxItem) => {
    router.push(inboxItemHref(item));
  };

  return (
    <div className="mx-auto max-w-4xl p-4 lg:p-8">
      <div className="mb-6 flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="font-display text-2xl font-bold text-white">{t("title")}</h1>
          <p className="mt-1 text-sm text-gray-400">
            {t("subtitle")}
            {total > 0 ? ` · ${t("itemCount", { count: total })}` : ""}
          </p>
        </div>

        <div className="flex items-center gap-1 rounded-full bg-white/[0.04] p-0.5">
          {(["all", "mine"] as const).map((value) => (
            <button
              key={value}
              type="button"
              onClick={() => setScope(value)}
              className={cn(
                "rounded-full px-3 py-1 text-xs font-medium transition-colors",
                scope === value
                  ? "bg-white/[0.10] text-gray-100"
                  : "text-gray-400 hover:text-gray-200"
              )}
            >
              {value === "all" ? t("scopeAll") : t("scopeMine")}
            </button>
          ))}
        </div>
      </div>

      <GlassCard padding="none">
        <InboxList
          items={items}
          counts={counts}
          isLoading={isLoading}
          error={error}
          activeKind={kind}
          onKindChange={setKind}
          onSelect={handleSelect}
          onRefresh={() => void refetch()}
        />
      </GlassCard>
    </div>
  );
}

export { InboxPageClient };
export type { InboxPageClientProps };
