import {
  createContext,
  useEffect,
  useState,
  useCallback,
  type ReactNode,
} from "react";
import { useRouter } from "@/i18n/navigation";
import { useParams } from "@/lib/navigation";
import { useClientApi } from "@/hooks/use-client-api";
import type { Project } from "@/lib/types";
import { api } from "@/lib/api";

export interface ProjectContextValue {
  projects: Project[];
  currentProject: Project | null;
  isLoading: boolean;
  switchProject: (projectId: string) => void;
  refreshProjects: () => Promise<void>;
  refreshProject: (projectId: string) => Promise<void>;
}

export const ProjectContext = createContext<ProjectContextValue | null>(null);

const LAST_PROJECT_COOKIE = "orkka-last-project";

function setLastProjectCookie(projectId: string) {
  document.cookie = `${LAST_PROJECT_COOKIE}=${projectId}; path=/; max-age=${60 * 60 * 24 * 365}; SameSite=Lax`;
}

export function ProjectProvider({ children }: { children: ReactNode }) {
  // App-contract hook: each app supplies the bearer token (remote: Clerk, local: constant).
  const callApi = useClientApi();
  const params = useParams();
  const router = useRouter();
  const [projects, setProjects] = useState<Project[]>([]);
  const [isLoading, setIsLoading] = useState(true);

  const projectId = params?.projectId as string | undefined;

  // A promise chain rather than async/await, so the mount effect below sets no state synchronously.
  const fetchProjects = useCallback(
    (): Promise<void> =>
      callApi((token) => api.projects.list(token))
        .then(
          (data) => {
            setProjects(data);
          },
          () => {
            // API (or auth) not available yet — leave empty
          }
        )
        .finally(() => {
          setIsLoading(false);
        }),
    [callApi]
  );

  useEffect(() => {
    fetchProjects();
  }, [fetchProjects]);

  // The route param carries either a slug (canonical) or a legacy ULID
  const currentProject =
    projects.find((p) => p.slug === projectId || p.id === projectId) ?? null;

  // Update cookie whenever currentProject changes
  useEffect(() => {
    if (currentProject) {
      setLastProjectCookie(currentProject.slug);
    }
  }, [currentProject]);

  const switchProject = useCallback(
    (id: string) => {
      router.push(`/p/${id}`);
    },
    [router]
  );

  const refreshProject = useCallback(
    async (id: string) => {
      try {
        const updated = await callApi((token) => api.projects.get(id, token));
        setProjects((prev) =>
          prev.map((p) => (p.id === updated.id ? updated : p))
        );
      } catch {
        // Ignore errors
      }
    },
    [callApi]
  );

  return (
    <ProjectContext.Provider
      value={{
        projects,
        currentProject,
        isLoading,
        switchProject,
        refreshProjects: fetchProjects,
        refreshProject,
      }}
    >
      {children}
    </ProjectContext.Provider>
  );
}
