import { useMemo } from "react";
import { Excalidraw, convertToExcalidrawElements } from "@excalidraw/excalidraw";
import "@excalidraw/excalidraw/index.css";
import {
  canvasBackgroundFor,
  normalizeSkeletonElements,
  sanitizeConvertedElements,
} from "@/lib/excalidraw-skeleton";

declare global {
  interface Window {
    EXCALIDRAW_ASSET_PATH?: string | string[];
  }
}

// Excalidraw's fonts are served by this app (vite.excalidraw-fonts.ts), never fetched from its
// CDN: set before the first canvas renders, which is when they load.
window.EXCALIDRAW_ASSET_PATH = "/excalidraw/";

interface ExcalidrawWrapperProps {
  diagramData: Record<string, unknown>;
  onChange?: (elements: readonly Record<string, unknown>[], appState: Record<string, unknown>) => void;
  /** View-only canvas — used by the history panel's version preview. */
  readOnly?: boolean;
}

interface DiagramScene {
  source?: string;
  elements?: Record<string, unknown>[];
  appState?: Record<string, unknown>;
}

function parseScene(diagramData: Record<string, unknown> | string): DiagramScene {
  // Defensive: older API versions delivered diagramData as a JSON string
  if (typeof diagramData === "string") {
    try {
      return JSON.parse(diagramData) as DiagramScene;
    } catch {
      return {};
    }
  }
  return (diagramData ?? {}) as DiagramScene;
}

function ExcalidrawWrapper({ diagramData, onChange, readOnly = false }: ExcalidrawWrapperProps) {
  const initialData = useMemo(() => {
    const scene = parseScene(diagramData);
    const rawElements = Array.isArray(scene.elements) ? scene.elements : [];

    // Agent-authored scenes are compact skeletons (no seed/versionNonce) — expand them
    // into full elements. Editor-saved scenes have complete elements and pass through.
    const isSkeleton =
      scene.source === "orkka-archie-skeleton" ||
      rawElements.some((el) => el.seed === undefined || el.versionNonce === undefined);

    let elements = rawElements;
    if (isSkeleton) {
      try {
        elements = convertToExcalidrawElements(
          normalizeSkeletonElements(rawElements) as never
        ) as unknown as Record<string, unknown>[];
      } catch {
        elements = rawElements;
      }
      elements = sanitizeConvertedElements(elements);
    }

    // Backgrounds Orkka wrote itself are replaced with one that survives the dark
    // theme's invert filter; a background the user picked is left alone. See
    // canvasBackgroundFor -- the value is deliberately light, and renders dark.
    const savedAppState = scene.appState ?? {};
    const viewBackgroundColor = canvasBackgroundFor(
      savedAppState.viewBackgroundColor as string | undefined
    );

    return {
      elements: elements as never,
      appState: {
        theme: "dark",
        ...savedAppState,
        viewBackgroundColor,
        ...(readOnly ? { viewModeEnabled: true } : {}),
      } as never,
    };
  }, [diagramData, readOnly]);

  return (
    <Excalidraw
      initialData={initialData}
      viewModeEnabled={readOnly || undefined}
      onChange={
        readOnly || !onChange
          ? undefined
          : (elements, appState) => {
              onChange(
                elements as unknown as readonly Record<string, unknown>[],
                appState as unknown as Record<string, unknown>
              );
            }
      }
      theme="dark"
    />
  );
}

export { ExcalidrawWrapper };
