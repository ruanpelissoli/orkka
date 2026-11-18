import { useState, useRef, useTransition } from "react";
import { useTranslations } from "use-intl";
import { Button, GlassCard } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { deleteAttachment } from "@/lib/actions";
import { useAttachmentUpload } from "./use-attachment-upload";
import type { TaskAttachment } from "@/lib/types";

interface AttachmentGalleryProps {
  attachments: TaskAttachment[];
  taskId: string;
  /** Called after an upload completes so the parent can refetch; falls back to a full reload */
  onUploaded?: () => void;
}

function isImage(contentType: string) {
  return contentType.startsWith("image/");
}

function formatFileSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function AttachmentGallery({ attachments: initialAttachments, taskId, onUploaded }: AttachmentGalleryProps) {
  const t = useTranslations("dashboard.taskMisc");
  const [attachments, setAttachments] = useState(initialAttachments);
  const [isPending, startTransition] = useTransition();
  const fileInputRef = useRef<HTMLInputElement>(null);
  const { uploading, uploadFiles } = useAttachmentUpload(
    taskId,
    onUploaded ?? (() => window.location.reload())
  );

  // Parent refetches (refreshData / modal reload) arrive as new props.
  const [prevInitialAttachments, setPrevInitialAttachments] = useState(initialAttachments);
  if (initialAttachments !== prevInitialAttachments) {
    setPrevInitialAttachments(initialAttachments);
    setAttachments(initialAttachments);
  }

  function handleDelete(attachmentId: string) {
    startTransition(async () => {
      try {
        await deleteAttachment(attachmentId);
        setAttachments((prev) => prev.filter((a) => a.id !== attachmentId));
      } catch {
        // Error handled
      }
    });
  }

  return (
    <GlassCard padding="md">
      <div className="flex items-center justify-between mb-3">
        <h4 className="text-label">
          {t("attachmentsTitle", { count: attachments.length })}
        </h4>
        <RequirePermission permission="TasksEdit">
          <Button
            variant="ghost"
            size="sm"
            disabled={uploading}
            loading={uploading}
            onClick={() => fileInputRef.current?.click()}
          >
            <svg width="12" height="12" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2">
              <path d="M8 3v10M3 8h10" strokeLinecap="round" />
            </svg>
            {t("upload")}
          </Button>
        </RequirePermission>
        <input
          ref={fileInputRef}
          type="file"
          multiple
          className="hidden"
          onChange={(e) => {
            if (e.target.files?.length) {
              uploadFiles(e.target.files);
              e.target.value = "";
            }
          }}
        />
      </div>

      {attachments.length === 0 ? (
        <p className="text-xs text-gray-400 text-center py-4">
          {t("noAttachments")}
        </p>
      ) : (
        <div className="space-y-2">
          {attachments.map((att) => (
            <div
              key={att.id}
              className="group flex items-center gap-3 rounded-lg p-2 transition-colors hover:bg-white/[0.03]"
            >
              {isImage(att.fileType) ? (
                <img
                  src={att.fileUrl}
                  alt={att.fileName}
                  className="h-10 w-10 rounded-lg object-cover border border-white/[0.06]"
                />
              ) : (
                <div className="flex h-10 w-10 items-center justify-center rounded-lg bg-white/[0.04] border border-white/[0.06]">
                  <svg width="16" height="16" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" className="text-gray-400">
                    <path d="M9 1H4a1 1 0 00-1 1v12a1 1 0 001 1h8a1 1 0 001-1V5l-4-4z" strokeLinejoin="round" />
                    <path d="M9 1v4h4" strokeLinejoin="round" />
                  </svg>
                </div>
              )}
              <div className="flex-1 min-w-0">
                <p className="text-xs text-gray-300 truncate">{att.fileName}</p>
                <p className="text-xs text-gray-400">{formatFileSize(att.fileSize)}</p>
              </div>
              <RequirePermission permission="TasksEdit">
                <button
                  onClick={() => handleDelete(att.id)}
                  disabled={isPending}
                  className="shrink-0 rounded-lg p-1.5 text-gray-400 opacity-0 transition-all group-hover:opacity-100 hover:text-red-400 hover:bg-red-500/10"
                >
                  <svg width="12" height="12" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5">
                    <path d="M4.5 4.5l7 7M11.5 4.5l-7 7" strokeLinecap="round" />
                  </svg>
                </button>
              </RequirePermission>
            </div>
          ))}
        </div>
      )}
    </GlassCard>
  );
}

export { AttachmentGallery };
