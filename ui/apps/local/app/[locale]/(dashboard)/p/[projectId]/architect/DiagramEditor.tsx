import {
  lazy,
  Suspense,
  useState,
  useTransition,
  useCallback,
  useRef,
  type ComponentProps,
} from "react";
import { Button, Skeleton } from "@/components/ui";
import { updateDiagram } from "@/lib/actions";
import { cn } from "@/lib/utils";
import { CANVAS_BACKGROUND_INPUT } from "@/lib/excalidraw-skeleton";
import type { ArchitectureDiagram, DiagramVersionDetail } from "@/lib/types";
import { useTranslations } from "use-intl";
import { DiagramHistoryPanel } from "./DiagramHistoryPanel";

// Excalidraw is large and touches window at import time: its own chunk, loaded on first use.
const LazyExcalidrawWrapper = lazy(() =>
  import("./ExcalidrawWrapper").then((mod) => ({ default: mod.ExcalidrawWrapper }))
);

function ExcalidrawWrapper(props: ComponentProps<typeof LazyExcalidrawWrapper>) {
  return (
    <Suspense
      fallback={
        <div className="flex h-full items-center justify-center">
          <Skeleton className="h-full w-full rounded-xl" />
        </div>
      }
    >
      <LazyExcalidrawWrapper {...props} />
    </Suspense>
  );
}

interface DiagramEditorProps {
  diagram: ArchitectureDiagram;
  projectId: string;
  onBack: () => void;
}

/**
 * Full-page diagram editor — the canvas gets all available space. Mount with
 * key={diagram.id}: switching diagrams remounts with fresh state.
 */
function DiagramEditor({ diagram, projectId, onBack }: DiagramEditorProps) {
  const [title, setTitle] = useState(diagram.title);
  const [isPending, startTransition] = useTransition();
  const [saveStatus, setSaveStatus] = useState<"idle" | "saved">("idle");
  const t = useTranslations("dashboard.architect");
  const sceneRef = useRef<Record<string, unknown> | null>(null);
  const [showHistory, setShowHistory] = useState(false);
  const [previewVersion, setPreviewVersion] = useState<DiagramVersionDetail | null>(null);
  // Restore replaces the diagram content — track it locally since the prop comes from
  // the server-rendered gallery and only refreshes on the next navigation.
  const [currentDiagram, setCurrentDiagram] = useState<ArchitectureDiagram>(diagram);

  const handleRestored = useCallback((restored: ArchitectureDiagram) => {
    setCurrentDiagram(restored);
    setTitle(restored.title);
    sceneRef.current = null;
  }, []);

  const handleChange = useCallback(
    (elements: readonly Record<string, unknown>[], appState: Record<string, unknown>) => {
      sceneRef.current = {
        type: "excalidraw",
        version: 2,
        source: "orkka-archie",
        elements: elements as unknown[],
        appState: {
          viewBackgroundColor:
            (appState as { viewBackgroundColor?: string }).viewBackgroundColor ??
            CANVAS_BACKGROUND_INPUT,
          gridSize: (appState as { gridSize?: number }).gridSize ?? 20,
        },
      };
    },
    []
  );

  function handleSave() {
    const data: Record<string, unknown> = {};
    if (title !== currentDiagram.title) data.title = title;
    if (sceneRef.current) data.diagramData = sceneRef.current;

    if (Object.keys(data).length === 0) {
      setSaveStatus("saved");
      setTimeout(() => setSaveStatus("idle"), 2000);
      return;
    }

    startTransition(async () => {
      await updateDiagram(projectId, currentDiagram.id, data as { title?: string; diagramData?: Record<string, unknown> });
      // A save bumps the version — reflect it so the history panel reloads
      setCurrentDiagram((prev) => ({ ...prev, version: prev.version + 1, title }));
      setSaveStatus("saved");
      setTimeout(() => setSaveStatus("idle"), 2000);
    });
  }

  return (
    <div className="flex h-full flex-col">
      {/* Header */}
      <div className="flex items-center justify-between gap-3 border-b border-white/[0.06] px-4 py-3 lg:px-6">
        <div className="flex items-center gap-3 min-w-0 flex-1">
          <button
            onClick={onBack}
            aria-label={t("back")}
            className="shrink-0 rounded-lg p-1.5 text-gray-400 transition-colors hover:bg-white/[0.06] hover:text-gray-200"
          >
            <svg width="16" height="16" viewBox="0 0 16 16" fill="none">
              <path d="M10 12L6 8l4-4" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
          </button>
          <input
            type="text"
            value={title}
            onChange={(e) => setTitle(e.target.value)}
            className="min-w-0 flex-1 bg-transparent font-display text-sm font-semibold text-white outline-none placeholder-gray-600 focus:ring-0"
            placeholder={t("editTitle")}
          />
        </div>
        <div className="flex shrink-0 items-center gap-1.5">
          <Button
            size="sm"
            variant="ghost"
            onClick={() => {
              setShowHistory((v) => !v);
              setPreviewVersion(null);
            }}
            className={cn(showHistory && "!bg-white/[0.08] !text-white")}
          >
            {t("history")}
          </Button>
          <Button
            size="sm"
            onClick={handleSave}
            disabled={isPending}
          >
            {isPending ? t("saving") : saveStatus === "saved" ? t("saved") : t("save")}
          </Button>
        </div>
      </div>

      {/* Canvas area — side-by-side with a read-only version preview when selected */}
      <div className="flex min-h-0 flex-1">
        <div className={cn("flex min-h-0 min-w-0 flex-1", previewVersion ? "flex-col lg:flex-row" : "")}>
          <div className="flex min-h-0 min-w-0 flex-1 flex-col">
            {previewVersion && (
              <p className="border-b border-white/[0.06] px-4 py-1.5 text-label">
                {t("currentVersion")} · v{currentDiagram.version}
              </p>
            )}
            <div className="min-h-0 flex-1">
              <ExcalidrawWrapper
                key={`${currentDiagram.id}-v${currentDiagram.version}`}
                diagramData={currentDiagram.diagramData}
                onChange={handleChange}
              />
            </div>
          </div>
          {previewVersion && (
            <div className="flex min-h-0 min-w-0 flex-1 flex-col border-t border-white/[0.06] lg:border-l lg:border-t-0">
              <p className="border-b border-white/[0.06] px-4 py-1.5 text-label">
                v{previewVersion.version} · {previewVersion.title}
              </p>
              <div className="min-h-0 flex-1">
                <ExcalidrawWrapper
                  key={`${currentDiagram.id}-preview-v${previewVersion.version}`}
                  diagramData={previewVersion.diagramData as Record<string, unknown>}
                  readOnly
                />
              </div>
            </div>
          )}
        </div>
        {showHistory && (
          <DiagramHistoryPanel
            projectId={projectId}
            diagramId={currentDiagram.id}
            currentVersion={currentDiagram.version}
            previewVersion={previewVersion?.version ?? null}
            onPreview={setPreviewVersion}
            onRestored={handleRestored}
          />
        )}
      </div>
    </div>
  );
}

export { DiagramEditor };
