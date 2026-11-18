import { useState, useTransition } from "react";
import { GlassCard, Badge, ConfirmDialog } from "@/components/ui";
import { DIAGRAM_TYPE_MAP } from "@/lib/constants";
import { deleteDiagram } from "@/lib/actions";
import type { ArchitectureDiagram } from "@/lib/types";
import { useTranslations } from "use-intl";

const DIAGRAM_TYPE_COLORS: Record<string, string> = {
  SystemArchitecture: "#8b5cf6",
  DataFlow: "#3b82f6",
  NetworkTopology: "#06b6d4",
  SequenceDiagram: "#f59e0b",
  ERDiagram: "#10b981",
  DeploymentDiagram: "#f59e0b",
  ComponentDiagram: "#a78bfa",
  Other: "#6b7280",
};

const DIAGRAM_TYPE_ICONS: Record<string, React.ReactNode> = {
  SystemArchitecture: (
    <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5">
      <rect x="3" y="3" width="7" height="7" rx="1" strokeLinecap="round" />
      <rect x="14" y="3" width="7" height="7" rx="1" strokeLinecap="round" />
      <rect x="3" y="14" width="7" height="7" rx="1" strokeLinecap="round" />
      <rect x="14" y="14" width="7" height="7" rx="1" strokeLinecap="round" />
      <path d="M10 6.5h4M6.5 10v4M17.5 10v4M10 17.5h4" strokeLinecap="round" />
    </svg>
  ),
  DataFlow: (
    <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5">
      <path d="M3 12h4m4 0h2m4 0h4M7 8l4 4-4 4M13 8l4 4-4 4" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  ),
  NetworkTopology: (
    <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5">
      <circle cx="12" cy="5" r="2.5" />
      <circle cx="5" cy="19" r="2.5" />
      <circle cx="19" cy="19" r="2.5" />
      <path d="M12 7.5v4M9.5 14l-3 3.5M14.5 14l3 3.5" strokeLinecap="round" />
    </svg>
  ),
  SequenceDiagram: (
    <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5">
      <path d="M4 4v16M20 4v16M4 8h16M4 14h16" strokeLinecap="round" />
      <path d="M12 8v6" strokeLinecap="round" strokeDasharray="2 2" />
    </svg>
  ),
  ERDiagram: (
    <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5">
      <rect x="2" y="3" width="8" height="6" rx="1" strokeLinecap="round" />
      <rect x="14" y="3" width="8" height="6" rx="1" strokeLinecap="round" />
      <rect x="8" y="15" width="8" height="6" rx="1" strokeLinecap="round" />
      <path d="M10 6h4M6 9v6l6 0M18 9v6l-6 0" strokeLinecap="round" />
    </svg>
  ),
  DeploymentDiagram: (
    <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5">
      <path d="M22 12a10 10 0 11-20 0 10 10 0 0120 0z" strokeLinecap="round" />
      <path d="M12 2v20M2 12h20" strokeLinecap="round" />
      <path d="M12 2a15 15 0 014 10 15 15 0 01-4 10M12 2a15 15 0 00-4 10 15 15 0 004 10" strokeLinecap="round" />
    </svg>
  ),
  ComponentDiagram: (
    <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5">
      <rect x="5" y="4" width="14" height="16" rx="2" strokeLinecap="round" />
      <rect x="2" y="8" width="6" height="3" rx="1" strokeLinecap="round" />
      <rect x="2" y="13" width="6" height="3" rx="1" strokeLinecap="round" />
    </svg>
  ),
  Other: (
    <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5">
      <path d="M14 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V8z" strokeLinecap="round" strokeLinejoin="round" />
      <path d="M14 2v6h6M16 13H8M16 17H8M10 9H8" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  ),
};

interface DiagramCardProps {
  diagram: ArchitectureDiagram;
  projectId: string;
  onClick: () => void;
  /** Opens the move-to-folder dialog for this diagram. */
  onMove?: () => void;
}

function DiagramCard({ diagram, projectId, onClick, onMove }: DiagramCardProps) {
  const [isPending, startTransition] = useTransition();
  const [confirmingDelete, setConfirmingDelete] = useState(false);
  const t = useTranslations("dashboard.architect");
  const tFolders = useTranslations("dashboard.folders");
  const tActions = useTranslations("common.actions");
  const typeInfo = DIAGRAM_TYPE_MAP[diagram.type];
  const color = DIAGRAM_TYPE_COLORS[diagram.type] ?? "#6b7280";
  const icon = DIAGRAM_TYPE_ICONS[diagram.type] ?? DIAGRAM_TYPE_ICONS.Other;

  const updatedAt = new Date(diagram.updatedAt);
  const timeAgo = formatTimeAgo(updatedAt);

  function handleDelete() {
    setConfirmingDelete(false);
    startTransition(() => {
      deleteDiagram(projectId, diagram.id);
    });
  }

  return (
    <GlassCard
      hover
      accentColor={color}
      padding="md"
      className={`cursor-pointer ${isPending ? "opacity-50 pointer-events-none" : ""}`}
      onClick={onClick}
    >
      {/* Header: Icon + Title + Delete */}
      <div className="flex items-start justify-between gap-3">
        <div className="flex items-center gap-3">
          <div
            className="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl"
            style={{ backgroundColor: `${color}1a`, color }}
          >
            {icon}
          </div>
          <div className="min-w-0">
            <h3 className="font-display text-sm font-semibold text-white truncate">
              {diagram.title}
            </h3>
            <div className="mt-1 flex items-center gap-1.5">
              <Badge
                size="sm"
                dot
                dotColor={color}
                style={{ borderColor: `${color}33`, color, backgroundColor: `${color}1a` }}
              >
                {typeInfo?.label ?? diagram.type}
              </Badge>
              {diagram.version > 1 && (
                <Badge variant="info" size="sm">
                  v{diagram.version}
                </Badge>
              )}
            </div>
          </div>
        </div>
        <div className="flex shrink-0 items-center gap-0.5">
          {onMove && (
            <button
              onClick={(e) => {
                e.stopPropagation();
                onMove();
              }}
              className="rounded-lg px-2 py-1.5 text-xs text-gray-400 transition-colors hover:bg-white/[0.06] hover:text-gray-300"
            >
              {tFolders("move")}
            </button>
          )}
          <button
            onClick={(e) => {
              e.stopPropagation();
              setConfirmingDelete(true);
            }}
            aria-label={t("delete")}
            className="rounded-lg p-1.5 text-gray-400 transition-colors hover:bg-red-500/10 hover:text-red-400"
          >
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
              <path d="M3 6h18M8 6V4a2 2 0 012-2h4a2 2 0 012 2v2m3 0v14a2 2 0 01-2 2H7a2 2 0 01-2-2V6h14" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
          </button>
        </div>
      </div>

      {/* Description */}
      {diagram.description && (
        <p className="mt-3 text-xs text-gray-400 line-clamp-2">
          {diagram.description}
        </p>
      )}

      {/* Footer */}
      <div className="mt-3 border-t border-white/[0.04] pt-3">
        <p className="text-xs font-medium text-gray-400">
          {t("lastUpdated", { time: timeAgo })}
        </p>
      </div>

      {/* The card itself is clickable — clicks inside the dialog must not bubble
          into onClick and open the diagram. */}
      <div onClick={(e) => e.stopPropagation()}>
        <ConfirmDialog
          open={confirmingDelete}
          title={diagram.title}
          message={t("deleteConfirm")}
          confirmLabel={t("delete")}
          cancelLabel={tActions("cancel")}
          danger
          pending={isPending}
          onConfirm={handleDelete}
          onCancel={() => setConfirmingDelete(false)}
        />
      </div>
    </GlassCard>
  );
}

function formatTimeAgo(date: Date): string {
  const now = new Date();
  const diffMs = now.getTime() - date.getTime();
  const diffMins = Math.floor(diffMs / 60000);
  const diffHours = Math.floor(diffMs / 3600000);
  const diffDays = Math.floor(diffMs / 86400000);

  if (diffMins < 1) return "just now";
  if (diffMins < 60) return `${diffMins}m ago`;
  if (diffHours < 24) return `${diffHours}h ago`;
  if (diffDays < 30) return `${diffDays}d ago`;
  return date.toLocaleDateString();
}

export { DiagramCard };
