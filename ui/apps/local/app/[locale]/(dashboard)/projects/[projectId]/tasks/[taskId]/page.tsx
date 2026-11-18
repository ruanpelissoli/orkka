import { useParams } from "@/lib/navigation";
import { ClientRedirect } from "@/components/page";

/** Old `/projects/…` URL: forwards to its `/p/…` page. */
export default function OldTaskDetailPage() {
  const { projectId, taskId } = useParams<{ projectId: string; taskId: string }>();
  return <ClientRedirect to={`/p/${projectId}/tasks/${taskId}`} />;
}
