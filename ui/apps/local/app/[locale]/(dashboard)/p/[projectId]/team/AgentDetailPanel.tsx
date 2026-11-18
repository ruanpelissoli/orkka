import { useState, useEffect } from "react";
import { motion, AnimatePresence } from "framer-motion";
import { cn } from "@/lib/utils";
import { Avatar, Badge } from "@/components/ui";
import { AGENT_ROLE_MAP } from "@/lib/constants";
import { AgentConfigTab } from "./AgentConfigTab";
import { AgentChatTab } from "./AgentChatTab";
import type { AiModel, ProjectAgent, ProjectRepo } from "@/lib/types";
import { useTranslations } from "use-intl";
import { useLabels } from "@/hooks/use-labels";

type TabKey = "config" | "chat";

interface AgentDetailPanelProps {
  agent: ProjectAgent | null;
  repos: ProjectRepo[];
  models: AiModel[];
  projectId: string;
  initialTab?: TabKey;
  /** Design-gallery iterate deep-link: pre-attach this asset in the chat tab. */
  initialAssetId?: string | null;
  /** History deep-link: open this conversation instead of the most recent one. */
  initialConversationId?: string | null;
  /** History deep-link: scroll to and briefly highlight this message. */
  highlightMessageId?: string | null;
  onClose: () => void;
}

function AgentDetailPanel({
  agent,
  repos,
  models,
  projectId,
  initialTab = "config",
  initialAssetId,
  initialConversationId,
  highlightMessageId,
  onClose,
}: AgentDetailPanelProps) {
  const t = useTranslations("dashboard.agentChat");
  const [activeTab, setActiveTab] = useState<TabKey>(initialTab);

  // Reset the tab when the agent or the requested tab changes. Adjusted during render
  // rather than in an effect: React re-renders this component before touching the DOM,
  // so the stale tab is never painted and there is no second commit.
  const tabResetKey = `${agent?.id ?? ""}:${initialTab}`;
  const [lastTabResetKey, setLastTabResetKey] = useState(tabResetKey);
  if (lastTabResetKey !== tabResetKey) {
    setLastTabResetKey(tabResetKey);
    setActiveTab(initialTab);
  }

  // Escape key to close
  useEffect(() => {
    function handleEscape(e: KeyboardEvent) {
      if (e.key === "Escape") onClose();
    }
    if (agent) {
      window.addEventListener("keydown", handleEscape);
      return () => window.removeEventListener("keydown", handleEscape);
    }
  }, [agent, onClose]);

  // Lock body scroll when open
  useEffect(() => {
    if (agent) {
      document.body.style.overflow = "hidden";
    } else {
      document.body.style.overflow = "";
    }
    return () => {
      document.body.style.overflow = "";
    };
  }, [agent]);

  const roleInfo = agent ? AGENT_ROLE_MAP[agent.role] : null;

  const labels = useLabels();

  return (
    <AnimatePresence>
      {agent && (
        <div className="fixed inset-0 z-50 flex justify-end">
          {/* Backdrop */}
          <motion.div
            initial={{ opacity: 0 }}
            animate={{ opacity: 1 }}
            exit={{ opacity: 0 }}
            transition={{ duration: 0.2 }}
            className="absolute inset-0 bg-black/60 backdrop-blur-sm"
            onClick={onClose}
          />

          {/* Panel - slides in from right */}
          <motion.div
            initial={{ x: "100%" }}
            animate={{ x: 0 }}
            exit={{ x: "100%" }}
            transition={{ type: "spring", damping: 30, stiffness: 400 }}
            className={cn(
              "relative z-10 flex h-full w-full flex-col",
              "bg-gray-900/95 backdrop-blur-2xl",
              "border-l border-white/[0.08]",
              "shadow-[0_0_60px_rgba(0,0,0,0.5)]",
              "md:max-w-2xl"
            )}
          >
            {/* Top highlight */}
            <div className="absolute inset-y-0 left-0 w-px bg-gradient-to-b from-transparent via-white/[0.1] to-transparent" />

            {/* Header */}
            <div className="flex items-center justify-between border-b border-white/[0.06] px-6 py-4">
              <div className="flex items-center gap-3">
                <Avatar name={agent.name} size="lg" status="online" />
                <div>
                  <h2 className="font-display text-lg font-semibold text-gray-100">
                    {agent.name}
                  </h2>
                  <Badge
                    size="sm"
                    dot
                    dotColor={roleInfo?.color}
                    style={roleInfo ? { borderColor: `${roleInfo.color}33`, color: roleInfo.color, backgroundColor: `${roleInfo.color}1a` } : undefined}
                  >
                    {labels.agentRole(agent.role)}
                  </Badge>
                </div>
              </div>
              <button
                onClick={onClose}
                className="rounded-lg p-1.5 text-gray-400 transition-colors hover:bg-white/[0.06] hover:text-gray-200"
              >
                <svg width="16" height="16" viewBox="0 0 16 16" fill="none">
                  <path
                    d="M12 4L4 12M4 4l8 8"
                    stroke="currentColor"
                    strokeWidth="1.5"
                    strokeLinecap="round"
                  />
                </svg>
              </button>
            </div>

            {/* Tab bar */}
            <div className="flex border-b border-white/[0.06] px-6">
              <TabButton
                active={activeTab === "config"}
                onClick={() => setActiveTab("config")}
              >
                {t("configure")}
              </TabButton>
              <TabButton
                active={activeTab === "chat"}
                onClick={() => setActiveTab("chat")}
              >
                {t("chat")}
              </TabButton>
            </div>

            {/* Tab content */}
            <div className="flex-1 min-h-0 overflow-hidden">
              {activeTab === "config" ? (
                <div className="h-full overflow-y-auto overscroll-contain px-6 py-4">
                  <AgentConfigTab
                    agent={agent}
                    repos={repos}
                    models={models}
                    projectId={projectId}
                    onClose={onClose}
                  />
                </div>
              ) : (
                <AgentChatTab
                  agent={agent}
                  projectId={projectId}
                  initialAssetId={initialAssetId}
                  initialConversationId={initialConversationId}
                  highlightMessageId={highlightMessageId}
                />
              )}
            </div>
          </motion.div>
        </div>
      )}
    </AnimatePresence>
  );
}

function TabButton({
  active,
  onClick,
  children,
}: {
  active: boolean;
  onClick: () => void;
  children: React.ReactNode;
}) {
  return (
    <button
      onClick={onClick}
      className={cn(
        "relative px-4 py-3 text-sm font-medium transition-colors",
        active
          ? "text-white"
          : "text-gray-400 hover:text-gray-300"
      )}
    >
      {children}
      {active && (
        <motion.div
          layoutId="agent-panel-tab"
          className="absolute inset-x-0 bottom-0 h-0.5 bg-primary-500"
          transition={{ type: "spring", damping: 30, stiffness: 400 }}
        />
      )}
    </button>
  );
}

export { AgentDetailPanel };
export type { TabKey };
