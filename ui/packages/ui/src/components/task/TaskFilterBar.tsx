import { useCallback, useEffect, useRef, useState } from "react";
import { useTranslations } from "use-intl";
import { useRouter } from "@/i18n/navigation";
import { useSearchParams } from "@/lib/navigation";
import { cn } from "@/lib/utils";
import { useLabels } from "@/hooks/use-labels";
import { TASK_STATUSES } from "@/lib/constants";
import type { ProjectRepo } from "@/lib/types";
import { Select } from "@/components/ui";

interface TaskFilterBarProps {
  repos: ProjectRepo[];
  projectId: string;
}

function TaskFilterBar({ repos, projectId }: TaskFilterBarProps) {
  const t = useTranslations("dashboard.taskFilterBar");
  const labels = useLabels();
  const router = useRouter();
  const searchParams = useSearchParams();
  const [search, setSearch] = useState(searchParams.get("search") ?? "");
  const debounceRef = useRef<ReturnType<typeof setTimeout>>(undefined);

  const updateParams = useCallback(
    (key: string, value: string) => {
      const params = new URLSearchParams(searchParams.toString());

      // No-op when the value hasn't changed. Without this, the debounced search
      // effect pushes on mount, which mints a new searchParams identity, which
      // re-arms the effect — an endless self-navigation loop that also stomps
      // any attempt to navigate away.
      if ((params.get(key) ?? "") === value) return;

      if (value) {
        params.set(key, value);
      } else {
        params.delete(key);
      }
      params.delete("page"); // Reset page on filter change
      const qs = params.toString();
      router.push(`/p/${projectId}/tasks${qs ? `?${qs}` : ""}`);
    },
    [router, searchParams, projectId]
  );

  // Debounced search
  useEffect(() => {
    if (debounceRef.current) clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(() => {
      updateParams("search", search);
    }, 300);
    return () => {
      if (debounceRef.current) clearTimeout(debounceRef.current);
    };
  }, [search, updateParams]);

  return (
    <div className="flex flex-wrap items-center gap-3">
      {/* Search */}
      <div className="relative flex-1 min-w-[200px]">
        <svg
          width="14"
          height="14"
          viewBox="0 0 16 16"
          fill="none"
          stroke="currentColor"
          strokeWidth="1.5"
          className="absolute left-3 top-1/2 -translate-y-1/2 text-gray-400"
        >
          <circle cx="7" cy="7" r="4.5" />
          <path d="M10.5 10.5L14 14" strokeLinecap="round" />
        </svg>
        <input
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          placeholder={t("searchPlaceholder")}
          className={cn(
            "w-full rounded-lg pl-9 pr-3 py-1.5 text-xs text-gray-300",
            "bg-white/[0.04] border border-white/[0.06]",
            "placeholder:text-gray-500",
            "focus:border-primary-500/50 focus:outline-none"
          )}
        />
      </div>

      {/* Repo filter */}
      <Select
        size="sm"
        aria-label={t("repoFilterLabel")}
        value={searchParams.get("repoId") ?? ""}
        onChange={(e) => updateParams("repoId", e.target.value)}
      >
        <option value="">{t("allRepos")}</option>
        {repos.map((repo) => (
          <option key={repo.id} value={repo.id}>
            {repo.name}
          </option>
        ))}
      </Select>

      {/* Status filter */}
      <Select
        size="sm"
        aria-label={t("statusFilterLabel")}
        value={searchParams.get("status") ?? ""}
        onChange={(e) => updateParams("status", e.target.value)}
      >
        <option value="">{t("allStatuses")}</option>
        {TASK_STATUSES.map((s) => (
          <option key={s.key} value={s.key}>
            {labels.taskStatus(s.key)}
          </option>
        ))}
      </Select>

      {/* Archive toggle */}
      <label className="flex items-center gap-1.5 cursor-pointer text-xs text-gray-400">
        <input
          type="checkbox"
          checked={searchParams.get("isArchived") === "true"}
          onChange={(e) =>
            updateParams("isArchived", e.target.checked ? "true" : "")
          }
          className="h-3.5 w-3.5 rounded border-white/[0.1] bg-white/[0.04] accent-primary-500"
        />
        {t("archived")}
      </label>
    </div>
  );
}

export { TaskFilterBar };
