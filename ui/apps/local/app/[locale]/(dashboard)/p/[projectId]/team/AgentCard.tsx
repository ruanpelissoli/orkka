import { useTranslations } from "use-intl";
import { GlassCard, Avatar, Badge } from "@/components/ui";
import { AgentOpsStats } from "@/components/team";
import { AGENT_ROLE_MAP } from "@/lib/constants";
import { formatTokenCount } from "@/lib/utils";
import type { AgentStats, AiModel, ProjectAgent, ProjectRepo } from "@/lib/types";
import { useLabels } from "@/hooks/use-labels";

interface AgentCardProps {
  agent: ProjectAgent;
  repos: ProjectRepo[];
  /** Catalog rows, for the model's display name. An id the catalog doesn't list (retired, or
   * an Ollama tag) falls back to the raw id. */
  models: AiModel[];
  /** Windowed run counters (Team page header selects the window). Undefined = no runs. */
  stats?: AgentStats;
  statsLoading?: boolean;
  onClick: () => void;
  onChat: () => void;
}

function AgentCard({ agent, repos, models, stats, statsLoading, onClick, onChat }: AgentCardProps) {
  const t = useTranslations("dashboard");
  const roleInfo = AGENT_ROLE_MAP[agent.role];
  const labels = useLabels();
  const modelInfo = models.find((m) => m.id === agent.model);
  const scopedRepoNames = agent.repoIds
    .map((id) => repos.find((r) => r.id === id)?.name)
    .filter(Boolean) as string[];

  return (
    <GlassCard
      hover
      accentColor={roleInfo?.color}
      padding="md"
      className="cursor-pointer"
      onClick={onClick}
    >
      {/* Header: Avatar + Name + Chat */}
      <div className="flex items-start justify-between gap-3">
        <div className="flex items-center gap-3">
          <Avatar name={agent.name} size="lg" status="online" />
          <div>
            <h3 className="font-display text-sm font-semibold text-white">{agent.name}</h3>
            <div className="mt-1 flex flex-wrap items-center gap-1.5">
              <Badge
                size="sm"
                dot
                dotColor={roleInfo?.color}
                style={roleInfo ? { borderColor: `${roleInfo.color}33`, color: roleInfo.color, backgroundColor: `${roleInfo.color}1a` } : undefined}
              >
                {labels.agentRole(agent.role)}
              </Badge>
              <Badge variant="info" size="sm">
                {modelInfo?.name ?? agent.model}
              </Badge>
            </div>
          </div>
        </div>
        <div className="flex items-center gap-1.5 shrink-0 pt-0.5">
          {/* Chat button */}
          <button
            onClick={(e) => {
              e.stopPropagation();
              onChat();
            }}
            className="rounded-lg p-1.5 text-gray-400 transition-colors hover:bg-white/[0.06] hover:text-primary-400"
            title={t("agentChat.chat")}
          >
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
              <path d="M21 15a2 2 0 01-2 2H7l-4 4V5a2 2 0 012-2h14a2 2 0 012 2z" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
          </button>
        </div>
      </div>

      {/* Repo scope */}
      <p className="mt-3 text-xs text-gray-400">
        {scopedRepoNames.length > 0 ? scopedRepoNames.join(", ") : t("agentCard.allRepos")}
      </p>

      {/* Stats */}
      <div className="mt-3 grid grid-cols-2 gap-2 border-t border-white/[0.04] pt-3">
        <div>
          <p className="text-label">{t("agentCard.tokens")}</p>
          <p
            className="text-xs text-gray-400"
            title={t("agentCard.tokensTitle")}
          >
            {formatTokenCount(
              agent.billed?.totalTokens ??
                agent.totalTokensIn + agent.totalTokensOut
            )}
          </p>
        </div>
        <div>
          <p className="text-label">{t("agentCard.tasks")}</p>
          <p className="text-xs text-gray-400">
            <span className="text-emerald-400">{agent.tasksCompleted}</span>
            {agent.tasksFailed > 0 && (
              <span className="text-red-400"> / {agent.tasksFailed}</span>
            )}
          </p>
        </div>
      </div>

      {/* Windowed health: runs / success / avg duration / last error */}
      <div className="mt-2 border-t border-white/[0.04] pt-1">
        <AgentOpsStats stats={stats} isLoading={statsLoading} />
      </div>
    </GlassCard>
  );
}

export { AgentCard };
