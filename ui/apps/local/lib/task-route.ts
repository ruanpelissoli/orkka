import { api } from "@/lib/api";
import type { Task } from "@/lib/types";

/** A task route segment is a per-project number (canonical) or a legacy ULID. */
export function isTaskNumber(taskRef: string): boolean {
  return /^\d+$/.test(taskRef);
}

/** The task a route segment names, or null when it cannot be loaded. */
export async function getTaskByRef(projectId: string, taskRef: string): Promise<Task | null> {
  try {
    return isTaskNumber(taskRef)
      ? await api.tasks.getByNumber(projectId, Number(taskRef), "")
      : await api.tasks.get(taskRef, "");
  } catch {
    return null;
  }
}
