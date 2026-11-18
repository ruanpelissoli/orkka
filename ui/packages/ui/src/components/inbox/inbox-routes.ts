import type { InboxItem } from "@/lib/types";

/**
 * Where an inbox item should land. Each kind deep-links to the screen that can actually
 * resolve it — dropping the reader on a generic task page and making them find the
 * approve button is the friction the inbox exists to remove.
 */
function inboxItemHref(item: InboxItem): string {
  const base = `/p/${item.projectId}/tasks/${item.number}`;
  switch (item.kind) {
    case "merge_conflict":
    case "merge_failed":
      return `${base}/merge`;
    case "code_review":
      return `${base}/review`;
    default:
      return base;
  }
}

export { inboxItemHref };
