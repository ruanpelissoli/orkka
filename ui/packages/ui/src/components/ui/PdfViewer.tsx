import { useEffect, useEffectEvent, useState } from "react";
import { Button } from "./Button";
import { cn } from "@/lib/utils";

interface PdfViewerProps {
  /** Fetches the PDF bytes. Called on mount and whenever the identity below changes. */
  load: () => Promise<Blob>;
  /** Changing this refetches — pass the document id so switching documents reloads. */
  sourceKey: string;
  fileName: string;
  sizeBytes?: number | null;
  labels: PdfViewerLabels;
  className?: string;
}

interface PdfViewerLabels {
  loading: string;
  failed: string;
  open: string;
  download: string;
  /** Shown instead of the frame on browsers with no built-in PDF viewer. */
  noInlineViewer: string;
}

function formatFileSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

/**
 * Renders a stored PDF with the browser's own PDF viewer, from bytes fetched with the
 * caller's token — the file is behind an authenticated endpoint, so it can't simply be a
 * frame `src`. The blob becomes an object URL that the frame, the open button and the
 * download button all share.
 *
 * Mobile Chrome and iOS Safari report `pdfViewerEnabled: false` and would render the frame
 * as a blank rectangle; there we show the file card alone, whose open/download buttons hand
 * the PDF to the platform viewer instead of pretending to display it.
 */
function PdfViewer({
  load,
  sourceKey,
  fileName,
  sizeBytes,
  labels,
  className,
}: PdfViewerProps) {
  // The result remembers which document it belongs to, so switching documents shows the
  // loading state until the new one arrives instead of the previous file.
  const [result, setResult] = useState<{ key: string; url: string | null; failed: boolean } | null>(null);
  const current = result !== null && result.key === sourceKey ? result : null;
  const objectUrl = current?.url ?? null;
  const failed = current?.failed ?? false;

  // `load` closes over a token getter and is a new function on every render, so it can't be
  // an effect dependency without refetching constantly. `sourceKey` is the real identity.
  const loadDocument = useEffectEvent(() => load());

  useEffect(() => {
    let cancelled = false;
    let created: string | null = null;

    loadDocument()
      .then((blob) => {
        if (cancelled) return;
        created = URL.createObjectURL(blob);
        setResult({ key: sourceKey, url: created, failed: false });
      })
      .catch(() => {
        if (!cancelled) setResult({ key: sourceKey, url: null, failed: true });
      });

    return () => {
      cancelled = true;
      // Revoking on unmount is what keeps a browsing session through a folder of PDFs from
      // holding every one of them in memory.
      if (created) URL.revokeObjectURL(created);
    };
  }, [sourceKey]);

  // `undefined` on browsers that never shipped the flag — those are desktop engines that do
  // render PDFs inline, so only an explicit `false` suppresses the frame.
  const canRenderInline =
    typeof navigator === "undefined" || navigator.pdfViewerEnabled !== false;

  function handleDownload() {
    if (!objectUrl) return;
    const anchor = document.createElement("a");
    anchor.href = objectUrl;
    anchor.download = fileName;
    anchor.click();
  }

  return (
    <div className={cn("flex min-h-0 flex-1 flex-col gap-3", className)}>
      <div className="flex items-center justify-between gap-3 rounded-lg border border-white/[0.06] bg-white/[0.02] px-3 py-2">
        <div className="flex min-w-0 items-center gap-2.5">
          <svg
            width="18"
            height="18"
            viewBox="0 0 16 16"
            fill="none"
            stroke="currentColor"
            strokeWidth="1.5"
            className="shrink-0 text-red-400"
            aria-hidden="true"
          >
            <path d="M9 1H4a1 1 0 00-1 1v12a1 1 0 001 1h8a1 1 0 001-1V5l-4-4z" strokeLinejoin="round" />
            <path d="M9 1v4h4" strokeLinejoin="round" />
          </svg>
          <div className="min-w-0">
            <p className="truncate text-xs text-gray-300">{fileName}</p>
            {sizeBytes ? (
              <p className="text-xs text-gray-400">{formatFileSize(sizeBytes)}</p>
            ) : null}
          </div>
        </div>
        <div className="flex shrink-0 items-center gap-1.5">
          <Button
            size="sm"
            variant="ghost"
            disabled={!objectUrl}
            onClick={() => objectUrl && window.open(objectUrl, "_blank", "noopener")}
          >
            {labels.open}
          </Button>
          <Button size="sm" variant="ghost" disabled={!objectUrl} onClick={handleDownload}>
            {labels.download}
          </Button>
        </div>
      </div>

      {failed ? (
        <div className="rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2 text-xs text-red-400">
          {labels.failed}
        </div>
      ) : !objectUrl ? (
        <div className="flex flex-1 items-center justify-center py-10 text-xs text-gray-400">
          {labels.loading}
        </div>
      ) : canRenderInline ? (
        <iframe
          src={objectUrl}
          title={fileName}
          className="min-h-[60vh] w-full flex-1 rounded-lg border border-white/[0.06] bg-white/[0.02]"
        />
      ) : (
        <div className="flex flex-1 items-center justify-center px-4 py-10 text-center text-xs text-gray-400">
          {labels.noInlineViewer}
        </div>
      )}
    </div>
  );
}

export { PdfViewer };
export type { PdfViewerLabels, PdfViewerProps };
