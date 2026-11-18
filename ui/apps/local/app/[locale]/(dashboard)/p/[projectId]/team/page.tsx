import { useParams, useSearchParams } from "@/lib/navigation";
import { useTranslations } from "use-intl";
import { ProviderMismatchBanner } from "@/components/runner/ProviderMismatchBanner";
import { PageError, TeamSkeleton } from "@/components/page";
import { useApiQuery } from "@/hooks/use-api-query";
import { useDocumentTitle } from "@/hooks/use-document-title";
import { api } from "@/lib/api";
import { AgentGrid } from "./AgentGrid";

export default function TeamPage() {
  const { projectId } = useParams<{ projectId: string }>();
  const searchParams = useSearchParams();
  const initialAgentId = searchParams.get("agent") ?? undefined;
  const tab = searchParams.get("tab");
  const initialAssetId = searchParams.get("asset") ?? undefined;
  const initialConversationId = searchParams.get("c") ?? undefined;
  const highlightMessageId = searchParams.get("m") ?? undefined;
  const t = useTranslations("dashboard");
  useDocumentTitle(t("meta.team"));

  const { data, error, reload } = useApiQuery(async () => {
    const [agents, repos, models, initialStats] = await Promise.all([
      api.agents.list(projectId, ""),
      api.repos.list(projectId, ""),
      // A catalog outage must not break the Team page — the config form degrades to "catalog
      // unavailable" and leaves each agent's saved model untouched.
      api.models.list("").catch(() => []),
      // Same for the windowed run counters: the cards render without them and the client
      // hook retries on its own.
      api.agents.stats(projectId, "7d", "").catch(() => null),
    ]);
    return { agents, repos, models, initialStats };
  }, [projectId]);

  if (error) return <PageError error={error} onRetry={reload} />;
  if (!data) return <TeamSkeleton />;

  const { agents, repos, models, initialStats } = data;
  // The whole chat catalog, runner-only rows included: whether a Copilot or Ollama model is
  // pickable depends on what THIS user's runner has signed in, which only the client knows
  // (AgentConfigTab annotates and disables per row).
  const chatModels = models.filter((m) => m.kind === "Chat" && m.isEnabled);

  return (
    <div className="mx-auto max-w-6xl p-4 lg:p-8">
      <ProviderMismatchBanner agents={agents} projectId={projectId} catalog={models} />
      <AgentGrid
        agents={agents}
        repos={repos}
        models={chatModels}
        projectId={projectId}
        initialStats={initialStats}
        initialAgentId={initialAgentId}
        initialTab={tab === "chat" ? "chat" : undefined}
        initialAssetId={initialAssetId}
        initialConversationId={initialConversationId}
        highlightMessageId={highlightMessageId}
      />
    </div>
  );
}
