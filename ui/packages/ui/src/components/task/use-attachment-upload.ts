import { useState } from "react";
import { useToast } from "@/components/ui";
import { getAttachmentUploadUrl, confirmAttachment } from "@/lib/actions";
import { CSRF_HEADERS } from "@/lib/api";

/**
 * Shared task-attachment upload flow: presigned upload URL → PUT the bytes to it (the local
 * host's file store, same origin) → confirm metadata with the API. Used by the gallery's Upload
 * button, the task page's drag-and-drop zone, and the task-create form.
 */
export async function uploadAttachment(taskId: string, file: File) {
  // Files without an extension report an empty type, which Azure rejects
  const contentType = file.type || "application/octet-stream";
  const { uploadUrl, blobName } = await getAttachmentUploadUrl(
    taskId,
    file.name,
    contentType
  );

  const res = await fetch(uploadUrl, {
    method: "PUT",
    credentials: "same-origin",
    headers: {
      ...CSRF_HEADERS,
      "x-ms-blob-type": "BlockBlob",
      "Content-Type": contentType,
    },
    body: file,
  });
  if (!res.ok) throw new Error(`Blob upload failed (${res.status})`);

  await confirmAttachment(taskId, blobName, file.name, contentType, file.size);
}

export function useAttachmentUpload(taskId: string, onUploaded?: () => void) {
  const [uploading, setUploading] = useState(false);
  const { toast } = useToast();

  async function uploadFiles(files: FileList | File[]) {
    const list = Array.from(files);
    if (list.length === 0 || uploading) return;
    setUploading(true);
    try {
      for (const file of list) {
        await uploadAttachment(taskId, file);
      }
      onUploaded?.();
    } catch (err) {
      // A silent failure here looks identical to "nothing happened" — the file
      // simply never appears. Surface it so the user (and we) can see the cause.
      console.error("Attachment upload failed:", err);
      toast(
        err instanceof Error ? `Upload failed: ${err.message}` : "Attachment upload failed",
        "error"
      );
    } finally {
      setUploading(false);
    }
  }

  return { uploading, uploadFiles };
}
