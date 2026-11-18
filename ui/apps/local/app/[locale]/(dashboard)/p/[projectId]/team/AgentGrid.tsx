import { useState, useCallback } from "react";
import { useTranslations } from "use-intl";
import { AgentCard } from "./AgentCard";
import { AgentDetailPanel, type TabKey } from "./AgentDetailPanel";
import { TeamModelControl } from "./TeamModelControl";
import { StatsWindowSelector } from "@/components/team";
import { useAgentStats } from "@/hooks/use-agent-stats";
import type { AgentStatsEnvelope, AgentStatsWindow, AiModel, ProjectAgent, ProjectRepo } from "@/lib/types";

interface AgentGridProps {
  agents: ProjectAgent[];
  repos: ProjectRepo[];
  /** Enabled chat models from the catalog — the agent model picker's options. */
  models: AiModel[];
  projectId: string;
  /** Server-rendered 7d stats so the cards paint with numbers; null if the fetch failed. */
  initialStats?: AgentStatsEnvelope | null;
  /** Deep link (?agent=&tab=&asset=&c=&m=): auto-open this agent's panel on mount. */
  initialAgentId?: string;
  initialTab?: TabKey;
  initialAssetId?: string;
  initialConversationId?: string;
  highlightMessageId?: string;
}

function AgentGrid({
  agents,
  repos,
  models,
  projectId,
  initialStats,
  initialAgentId,
  initialTab,
  initialAssetId,
  initialConversationId,
  highlightMessageId,
}: AgentGridProps) {
  const t = useTranslations("dashboard");
  const [selectedAgent, setSelectedAgent] = useState<ProjectAgent | null>(
    () => agents.find((a) => a.id === initialAgentId) ?? null
  );
  const [panelTab, setPanelTab] = useState<TabKey>(
    initialAgentId ? (initialTab ?? "chat") : "config"
  );
  const [statsWindow, setStatsWindow] = useState<AgentStatsWindow>(initialStats?.window ?? "7d");
  const { byAgent: statsByAgent, isLoading: statsLoading } = useAgentStats({
    projectId,
    window: statsWindow,
    initial: initialStats,
  });

  const openAgent = useCallback((agent: ProjectAgent, tab: TabKey = "config") => {
    setSelectedAgent(agent);
    setPanelTab(tab);
  }, []);

  return (
    <>
      {/* Header */}
      <div className="flex items-center justify-between mb-6">
        <h1 className="font-display text-xl font-bold text-white">{t("meta.team")}</h1>
        <StatsWindowSelector value={statsWindow} onChange={setStatsWindow} />
      </div>

      {/* One step to move the whole team onto a model (e.g. Copilot Auto). */}
      <TeamModelControl agents={agents} models={models} projectId={projectId} />

      {/* Grid — every available agent is seeded with the project */}
      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
        {agents.map((agent) => (
          <AgentCard
            key={agent.id}
            agent={agent}
            repos={repos}
            models={models}
            stats={statsByAgent.get(agent.id)}
            statsLoading={statsLoading}
            onClick={() => openAgent(agent, "config")}
            onChat={() => openAgent(agent, "chat")}
          />
        ))}
      </div>

      {/* Agent Detail Panel */}
      <AgentDetailPanel
        agent={selectedAgent}
        repos={repos}
        models={models}
        projectId={projectId}
        initialTab={panelTab}
        initialAssetId={initialAssetId}
        initialConversationId={initialConversationId}
        highlightMessageId={highlightMessageId}
        onClose={() => setSelectedAgent(null)}
      />
    </>
  );
}

export { AgentGrid };
