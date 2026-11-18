import { useCallback, useMemo, useState } from "react";
import { useSearchParams, useUpdateSearchParams } from "@/lib/navigation";
import { Link } from "@/i18n/navigation";
import { Button, EmptyState, GlassCard } from "@/components/ui";
import { DiagramCard } from "./DiagramCard";
import { DiagramEditor } from "./DiagramEditor";
import { FolderTreePanel, MoveToFolderDialog } from "@/components/assets/FolderTreePanel";
import { moveDiagram, renameDiagramFolder } from "@/lib/actions";
import type { ArchitectureDiagram } from "@/lib/types";
import { useTranslations } from "use-intl";

interface DiagramGalleryProps {
  diagrams: ArchitectureDiagram[];
  projectId: string;
}

function DiagramGallery({ diagrams, projectId }: DiagramGalleryProps) {
  // The open diagram lives in the ?d= search param (also the deep-link target from
  // agent chat). Only the query changes, so the gallery stays mounted — see TaskBoard.
  const searchParams = useSearchParams();
  const updateSearchParams = useUpdateSearchParams();
  const selectedDiagram = diagrams.find((d) => d.id === searchParams.get("d")) ?? null;
  const t = useTranslations("dashboard.architect");
  // Folder organization: null = all items; "" = root. Folders are virtual (derived from
  // folderPath values), so freshly created empty ones live client-side until used.
  const [selectedFolder, setSelectedFolder] = useState<string | null>(null);
  const [pendingFolders, setPendingFolders] = useState<string[]>([]);
  const [moveTarget, setMoveTarget] = useState<ArchitectureDiagram | null>(null);

  const folders = useMemo(
    () => [...new Set([...diagrams.map((d) => d.folderPath).filter(Boolean), ...pendingFolders])],
    [diagrams, pendingFolders]
  );

  const folderCounts = useMemo(() => {
    const counts: Record<string, number> = {};
    for (const diagram of diagrams)
      counts[diagram.folderPath] = (counts[diagram.folderPath] ?? 0) + 1;
    return counts;
  }, [diagrams]);

  const filtered = useMemo(() => {
    if (selectedFolder === null) return diagrams;
    // A folder shows its own items plus everything nested below it.
    return diagrams.filter(
      (d) => d.folderPath === selectedFolder || (selectedFolder !== "" && d.folderPath.startsWith(selectedFolder + "/"))
    );
  }, [diagrams, selectedFolder]);

  const openDiagram = useCallback(
    (id: string) => updateSearchParams((p) => p.set("d", id)),
    [updateSearchParams]
  );

  const closeDiagram = useCallback(
    () => updateSearchParams((p) => p.delete("d")),
    [updateSearchParams]
  );

  // Full-page editor — a canvas wants every pixel it can get
  if (selectedDiagram) {
    return (
      <div className="h-[calc(100vh-4rem)]">
        <DiagramEditor
          key={selectedDiagram.id}
          diagram={selectedDiagram}
          projectId={projectId}
          onBack={closeDiagram}
        />
      </div>
    );
  }

  return (
    <div className="flex flex-col h-[calc(100vh-4rem)] p-4 lg:py-6 lg:pl-4 lg:pr-6">
      {/* Header */}
      <div className="mb-4">
        <h1 className="font-display text-xl font-bold text-white">{t("title")}</h1>
      </div>

      <div className="flex min-h-0 flex-1 flex-col gap-4 lg:flex-row">
        {/* Folder pane — compact scrollable strip above the gallery on mobile */}
        <GlassCard className="max-h-44 w-full shrink-0 overflow-y-auto !p-2 lg:max-h-none lg:w-64">
        <FolderTreePanel
          folders={folders}
          selected={selectedFolder}
          counts={folderCounts}
          totalCount={diagrams.length}
          onSelect={setSelectedFolder}
          onRename={async (from, to) => {
            try {
              await renameDiagramFolder(projectId, from, to);
              setPendingFolders((prev) => prev.map((f) => (f === from ? to : f)));
              if (selectedFolder === from) setSelectedFolder(to);
            } catch {
              /* revalidation restores server truth */
            }
          }}
          onCreateFolder={(path) =>
            setPendingFolders((prev) => (prev.includes(path) ? prev : [...prev, path]))
          }
        />
        </GlassCard>
        <div className="min-w-0 flex-1 overflow-y-auto">
      {/* Grid or Empty State */}
      {diagrams.length === 0 ? (
        <EmptyState
          title={t("noDiagrams")}
          description={t("noDiagramsDescription")}
          icon={
            <svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5">
              <rect x="3" y="3" width="7" height="7" rx="1" strokeLinecap="round" />
              <rect x="14" y="3" width="7" height="7" rx="1" strokeLinecap="round" />
              <rect x="3" y="14" width="7" height="7" rx="1" strokeLinecap="round" />
              <rect x="14" y="14" width="7" height="7" rx="1" strokeLinecap="round" />
              <path d="M10 6.5h4M6.5 10v4M17.5 10v4M10 17.5h4" strokeLinecap="round" />
            </svg>
          }
          action={
            <Link href={`/p/${projectId}/team`}>
              <Button size="sm">{t("goToTeam")}</Button>
            </Link>
          }
        />
      ) : (
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
          {filtered.map((diagram) => (
            <DiagramCard
              key={diagram.id}
              diagram={diagram}
              projectId={projectId}
              onClick={() => openDiagram(diagram.id)}
              onMove={() => setMoveTarget(diagram)}
            />
          ))}
        </div>
      )}
        </div>
      </div>

      {/* Move a diagram into a folder */}
      <MoveToFolderDialog
        open={moveTarget !== null}
        folders={folders}
        current={moveTarget?.folderPath ?? ""}
        onClose={() => setMoveTarget(null)}
        onMove={async (folderPath) => {
          const diagram = moveTarget;
          setMoveTarget(null);
          if (!diagram) return;
          try {
            await moveDiagram(projectId, diagram.id, folderPath);
          } catch {
            /* revalidation restores server truth */
          }
        }}
      />
    </div>
  );
}

export { DiagramGallery };
