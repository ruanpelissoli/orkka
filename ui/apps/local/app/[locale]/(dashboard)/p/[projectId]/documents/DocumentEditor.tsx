import { useState } from "react";
import { useTranslations } from "use-intl";
import { Button, Input, Markdown } from "@/components/ui";
import { createDocument, updateDocument } from "@/lib/actions";
import { cn } from "@/lib/utils";
import type { ProjectDocumentDetail } from "@/lib/types";

interface DocumentEditorProps {
  projectId: string;
  /** null = create a new document */
  document: ProjectDocumentDetail | null;
  onSaved: (doc: ProjectDocumentDetail) => void;
  onCancel: () => void;
}

/**
 * Split-pane markdown editor: plain textarea on the left, live preview (the same
 * Markdown renderer used across the app) on the right. No editor dependency.
 */
function DocumentEditor({ projectId, document, onSaved, onCancel }: DocumentEditorProps) {
  const t = useTranslations("dashboard.documents");
  const [title, setTitle] = useState(document?.title ?? "");
  const [path, setPath] = useState(document?.path ?? "");
  const [content, setContent] = useState(document?.content ?? "");
  const [showPreview, setShowPreview] = useState(true);
  const [isSaving, setIsSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const canSave = title.trim().length > 0 && content.trim().length > 0 && !isSaving;

  const handleSave = async () => {
    if (!canSave) return;
    setIsSaving(true);
    setError(null);
    try {
      const saved = document
        ? await updateDocument(projectId, document.id, { title, path, content })
        : await createDocument(projectId, { title, path, content });
      if (!saved) {
        setError(t("conflict"));
        setIsSaving(false);
        return;
      }
      onSaved(saved);
    } catch {
      setError(t("saveFailed"));
      setIsSaving(false);
    }
  };

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      {/* Editor header */}
      <div className="flex items-center gap-2 border-b border-white/[0.04] px-4 py-3">
        <div className="flex min-w-0 flex-1 gap-2">
          <Input
            value={title}
            onChange={(e) => setTitle(e.target.value)}
            placeholder={t("titlePlaceholder")}
            className="flex-1"
          />
          <Input
            value={path}
            onChange={(e) => setPath(e.target.value)}
            placeholder={t("folderPlaceholder")}
            className="w-56"
          />
        </div>
        <div className="flex shrink-0 items-center gap-1.5">
          <Button
            size="sm"
            variant="ghost"
            onClick={() => setShowPreview((v) => !v)}
            className={cn(showPreview && "!bg-white/[0.08] !text-white")}
          >
            {t("preview")}
          </Button>
          <Button size="sm" variant="ghost" onClick={onCancel} disabled={isSaving}>
            {t("cancel")}
          </Button>
          <Button size="sm" onClick={handleSave} disabled={!canSave}>
            {isSaving ? t("saving") : t("save")}
          </Button>
        </div>
      </div>

      {error && (
        <div className="mx-4 mt-2 rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2 text-xs text-red-400">
          {error}
        </div>
      )}

      {/* Split pane */}
      <div className="flex min-h-0 flex-1">
        <textarea
          value={content}
          onChange={(e) => setContent(e.target.value)}
          placeholder={t("contentPlaceholder")}
          spellCheck={false}
          className={cn(
            "min-h-0 flex-1 resize-none bg-transparent p-4 font-mono text-sm leading-relaxed",
            "text-gray-200 placeholder:text-gray-500 focus:outline-none",
            showPreview && "border-r border-white/[0.04]"
          )}
        />
        {showPreview && (
          <div className="min-h-0 w-1/2 shrink-0 overflow-y-auto p-4">
            <Markdown>{content}</Markdown>
          </div>
        )}
      </div>
    </div>
  );
}

export { DocumentEditor };
