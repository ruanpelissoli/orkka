import { useEffect, useRef, useState } from "react";
import { useTranslations } from "use-intl";
import { cn } from "@/lib/utils";

/** One piece of context linked to the next chat message. Rendered as a removable chip;
 * on send each ref becomes a visible marker line the server resolves into agent context. */
export interface ChatContextRef {
  type: "attachment" | "document" | "design";
  id: string;
  label: string;
  version?: number;
}

/** Marker lines for the message body — the server-side resolvers parse these. */
export function contextRefMarkers(refs: ChatContextRef[]): string {
  if (refs.length === 0) return "";
  const lines = refs.map((ref) => {
    const name = ref.label.replace(/"/g, "'");
    switch (ref.type) {
      case "attachment":
        return `[attachment:${ref.id} "${name}"]`;
      case "document":
        return `[document:${ref.id} "${name}"]`;
      case "design":
        return `[design-asset:${ref.id} "${name}" v${ref.version ?? 1}]`;
    }
  });
  return lines.join("\n") + "\n\n";
}

const TYPE_ICONS: Record<ChatContextRef["type"], React.ReactNode> = {
  attachment: (
    <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
      <path d="M21.44 11.05l-9.19 9.19a6 6 0 01-8.49-8.49l9.19-9.19a4 4 0 015.66 5.66l-9.2 9.19a2 2 0 01-2.83-2.83l8.49-8.48" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  ),
  document: (
    <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
      <path d="M14 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V8z" strokeLinecap="round" strokeLinejoin="round" />
      <path d="M14 2v6h6" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  ),
  design: (
    <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
      <rect x="3" y="3" width="18" height="18" rx="2" />
      <circle cx="9" cy="9" r="2" />
      <path d="M21 15l-3.5-3.5a1.5 1.5 0 00-2.1 0L7 20" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  ),
};

/** Removable chips for the context linked to the next message. */
export function ChatContextChips({
  refs,
  onRemove,
}: {
  refs: ChatContextRef[];
  onRemove: (ref: ChatContextRef) => void;
}) {
  const t = useTranslations("dashboard.agentChat");
  if (refs.length === 0) return null;
  return (
    <div className="mb-2 flex flex-wrap gap-1.5">
      {refs.map((ref) => (
        <span
          key={`${ref.type}:${ref.id}`}
          className="inline-flex max-w-[220px] items-center gap-1.5 rounded-lg border border-white/[0.06] bg-white/[0.03] px-2 py-1 text-xs text-gray-300"
        >
          <span className="shrink-0 text-gray-400">{TYPE_ICONS[ref.type]}</span>
          <span className="min-w-0 truncate">
            {ref.label}
            {ref.type === "design" && ref.version ? (
              <span className="text-gray-400"> v{ref.version}</span>
            ) : null}
          </span>
          <button
            onClick={() => onRemove(ref)}
            className="shrink-0 rounded p-0.5 text-gray-400 transition-colors hover:bg-white/[0.06] hover:text-gray-300"
            aria-label={t("removeAttachment")}
          >
            <svg width="10" height="10" viewBox="0 0 16 16" fill="none">
              <path d="M12 4L4 12M4 4l8 8" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
            </svg>
          </button>
        </span>
      ))}
    </div>
  );
}

export interface ChatContextOptions {
  documents: { id: string; title: string }[];
  designAssets: { id: string; name: string; version: number }[];
}

interface ChatContextButtonsProps {
  disabled?: boolean;
  uploading?: boolean;
  /** Uploads the picked files; caller adds the resulting refs itself. */
  onFiles: (files: File[]) => void;
  /** Fetches the project's linkable documents and design assets (called on first open). */
  loadOptions: () => Promise<ChatContextOptions>;
  onAdd: (ref: ChatContextRef) => void;
  /** Already-linked refs — shown as selected/disabled in the picker. */
  refs: ChatContextRef[];
}

/** Paperclip (upload) + link (existing documents/design assets) buttons for the chat
 * composer, with the link popover anchored above them. */
export function ChatContextButtons({
  disabled,
  uploading,
  onFiles,
  loadOptions,
  onAdd,
  refs,
}: ChatContextButtonsProps) {
  const t = useTranslations("dashboard.agentChat");
  const fileInputRef = useRef<HTMLInputElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const [pickerOpen, setPickerOpen] = useState(false);
  const [options, setOptions] = useState<ChatContextOptions | null>(null);
  const [loading, setLoading] = useState(false);
  const [loadFailed, setLoadFailed] = useState(false);

  useEffect(() => {
    if (!pickerOpen) return;
    const close = (e: MouseEvent) => {
      if (!containerRef.current?.contains(e.target as Node)) setPickerOpen(false);
    };
    document.addEventListener("mousedown", close);
    return () => document.removeEventListener("mousedown", close);
  }, [pickerOpen]);

  async function openPicker() {
    setPickerOpen((open) => !open);
    if (options || loading) return;
    setLoading(true);
    setLoadFailed(false);
    try {
      setOptions(await loadOptions());
    } catch {
      setLoadFailed(true);
    } finally {
      setLoading(false);
    }
  }

  const isLinked = (type: ChatContextRef["type"], id: string) =>
    refs.some((r) => r.type === type && r.id === id);

  const buttonClass = cn(
    "flex h-10 w-9 shrink-0 items-center justify-center rounded-xl text-gray-400",
    "transition-colors hover:bg-white/[0.06] hover:text-gray-300",
    "disabled:pointer-events-none disabled:opacity-40"
  );

  return (
    <div ref={containerRef} className="relative flex items-end">
      <input
        ref={fileInputRef}
        type="file"
        multiple
        className="hidden"
        onChange={(e) => {
          const files = Array.from(e.target.files ?? []);
          e.target.value = "";
          if (files.length > 0) onFiles(files);
        }}
      />
      <button
        type="button"
        disabled={disabled || uploading}
        onClick={() => fileInputRef.current?.click()}
        className={buttonClass}
        title={t("attachFile")}
        aria-label={t("attachFile")}
      >
        {uploading ? (
          <span className="h-3.5 w-3.5 animate-spin rounded-full border-2 border-gray-500 border-t-transparent" />
        ) : (
          TYPE_ICONS.attachment
        )}
      </button>
      <button
        type="button"
        disabled={disabled}
        onClick={openPicker}
        className={cn(buttonClass, pickerOpen && "bg-white/[0.06] text-gray-300")}
        title={t("linkContext")}
        aria-label={t("linkContext")}
      >
        <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
          <path d="M10 13a5 5 0 007.54.54l3-3a5 5 0 00-7.07-7.07l-1.72 1.71" strokeLinecap="round" strokeLinejoin="round" />
          <path d="M14 11a5 5 0 00-7.54-.54l-3 3a5 5 0 007.07 7.07l1.71-1.71" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      </button>

      {pickerOpen && (
        <div className="absolute bottom-full right-0 z-30 mb-2 w-72 rounded-xl border border-white/[0.08] bg-surface-overlay p-2 shadow-xl">
          {loading && (
            <p className="px-2 py-3 text-center text-xs text-gray-400">{t("loadingContext")}</p>
          )}
          {loadFailed && (
            <p className="px-2 py-3 text-center text-xs text-red-400">{t("loadContextFailed")}</p>
          )}
          {options && (
            <div className="max-h-72 overflow-y-auto">
              <p className="px-2 pb-1 pt-1.5 text-label">
                {t("linkDocuments")}
              </p>
              {options.documents.length === 0 ? (
                <p className="px-2 pb-2 text-xs text-gray-400">{t("noLinkableItems")}</p>
              ) : (
                options.documents.map((doc) => (
                  <button
                    key={doc.id}
                    disabled={isLinked("document", doc.id)}
                    onClick={() => {
                      onAdd({ type: "document", id: doc.id, label: doc.title });
                      setPickerOpen(false);
                    }}
                    className="flex w-full items-center gap-2 rounded-lg px-2 py-1.5 text-left text-xs text-gray-300 transition-colors hover:bg-white/[0.05] disabled:opacity-40"
                  >
                    <span className="shrink-0 text-gray-400">{TYPE_ICONS.document}</span>
                    <span className="min-w-0 truncate">{doc.title}</span>
                  </button>
                ))
              )}
              <p className="px-2 pb-1 pt-2 text-label">
                {t("linkDesignAssets")}
              </p>
              {options.designAssets.length === 0 ? (
                <p className="px-2 pb-2 text-xs text-gray-400">{t("noLinkableItems")}</p>
              ) : (
                options.designAssets.map((asset) => (
                  <button
                    key={asset.id}
                    disabled={isLinked("design", asset.id)}
                    onClick={() => {
                      onAdd({ type: "design", id: asset.id, label: asset.name, version: asset.version });
                      setPickerOpen(false);
                    }}
                    className="flex w-full items-center gap-2 rounded-lg px-2 py-1.5 text-left text-xs text-gray-300 transition-colors hover:bg-white/[0.05] disabled:opacity-40"
                  >
                    <span className="shrink-0 text-gray-400">{TYPE_ICONS.design}</span>
                    <span className="min-w-0 truncate">
                      {asset.name} <span className="text-gray-400">v{asset.version}</span>
                    </span>
                  </button>
                ))
              )}
            </div>
          )}
        </div>
      )}
    </div>
  );
}
