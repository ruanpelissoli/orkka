import { useTranslations } from "use-intl";
import { Link } from "@/i18n/navigation";

type BoardViewKey = "code" | "board" | "list";

interface BoardViewSwitchProps {
  projectId: string;
  active: BoardViewKey;
  /** Carry the open task across Code ⇄ Board so switching views keeps your place. */
  taskParam?: string | null;
}

/**
 * Code · Board · List. Code and Board are the same route (the kanban is the default;
 * `?view=code` selects the Code View); List is the tasks page. Server-compatible — plain links.
 */
function BoardViewSwitch({ projectId, active, taskParam }: BoardViewSwitchProps) {
  const t = useTranslations("dashboard.boardPage");
  const task = taskParam ? `task=${encodeURIComponent(taskParam)}` : "";
  const items: { key: BoardViewKey; label: string; href: string }[] = [
    { key: "code", label: t("code"), href: `/p/${projectId}/board?view=code${task ? `&${task}` : ""}` },
    { key: "board", label: t("board"), href: `/p/${projectId}/board${task ? `?${task}` : ""}` },
    { key: "list", label: t("list"), href: `/p/${projectId}/tasks` },
  ];
  return (
    <div className="flex items-center gap-1 rounded-lg bg-white/[0.04] p-0.5" role="group" aria-label={t("viewSwitch")}>
      {items.map((item) =>
        item.key === active ? (
          <span
            key={item.key}
            aria-current="page"
            className="rounded-md bg-white/[0.08] px-2.5 py-1 text-xs font-medium text-gray-200"
          >
            {item.label}
          </span>
        ) : (
          <Link
            key={item.key}
            href={item.href}
            className="rounded-md px-2.5 py-1 text-xs font-medium text-gray-400 transition-colors hover:text-gray-300"
          >
            {item.label}
          </Link>
        )
      )}
    </div>
  );
}

export { BoardViewSwitch };
export type { BoardViewKey };
