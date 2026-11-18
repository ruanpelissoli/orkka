import { useParams } from "@/lib/navigation";
import { ClientRedirect } from "@/components/page";

/** Old `/projects/…` URL: forwards to its `/p/…` page. */
export default function OldReposPage() {
  const { projectId } = useParams<{ projectId: string }>();
  return <ClientRedirect to={`/p/${projectId}/repos`} />;
}
