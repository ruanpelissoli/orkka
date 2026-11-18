import { useTranslations } from "use-intl";
import { cn } from "@/lib/utils";
import type { PrFile } from "@/lib/types";

interface PrFileListProps {
  files: PrFile[];
  activeFile: string | null;
  onFileClick: (filename: string) => void;
}

const STATUS_BADGE: Record<string, { label: string; className: string }> = {
  added: { label: "A", className: "bg-emerald-500/10 text-emerald-400" },
  modified: { label: "M", className: "bg-amber-500/10 text-amber-400" },
  removed: { label: "D", className: "bg-red-500/10 text-red-400" },
  renamed: { label: "R", className: "bg-primary-500/10 text-primary-400" },
  copied: { label: "C", className: "bg-violet-500/10 text-violet-400" },
};

function PrFileList({ files, activeFile, onFileClick }: PrFileListProps) {
  const t = useTranslations("dashboard.prReview");
  return (
    <div className="space-y-0.5">
      <h4 className="text-label mb-2 px-1">
        {t("filesHeading", { count: files.length })}
      </h4>
      <div className="max-h-[300px] overflow-y-auto space-y-0.5">
        {files.map((file) => {
          const badge = STATUS_BADGE[file.status] ?? STATUS_BADGE.modified;
          const isActive = activeFile === file.filename;

          return (
            <button
              key={file.filename}
              onClick={() => onFileClick(file.filename)}
              className={cn(
                "flex w-full items-center gap-2 rounded-lg px-3 py-2 text-left text-xs transition-colors",
                isActive
                  ? "bg-white/[0.06] text-white"
                  : "hover:bg-white/[0.03] text-gray-400"
              )}
            >
              <span
                className={cn(
                  "shrink-0 rounded px-1.5 py-0.5 font-mono text-xs font-semibold",
                  badge.className
                )}
              >
                {badge.label}
              </span>
              <span className="font-mono truncate flex-1 min-w-0">
                {file.filename}
              </span>
              <span className="shrink-0 flex items-center gap-1.5 font-mono text-xs">
                {file.additions > 0 && (
                  <span className="text-emerald-400">+{file.additions}</span>
                )}
                {file.deletions > 0 && (
                  <span className="text-red-400">-{file.deletions}</span>
                )}
              </span>
            </button>
          );
        })}
      </div>
    </div>
  );
}

export { PrFileList };
