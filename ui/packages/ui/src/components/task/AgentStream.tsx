import { useRef, useEffect, useMemo } from "react";
import { cn } from "@/lib/utils";
import { GlassCard } from "@/components/ui";
import type { StreamEvent } from "@/hooks/use-task-signalr";
import { useTranslations } from "use-intl";

interface AgentStreamProps {
  events: StreamEvent[];
  isActive: boolean;
  /** Include raw tool-call events (project setting; off by default). */
  showToolCalls?: boolean;
}

interface GroupedBlock {
  type: StreamEvent["type"];
  content: string;
  startIndex: number;
}

const EVENT_COLORS: Record<string, string> = {
  AgentThinking: "text-violet-400",
  ToolUsage: "text-cyan-400",
  ToolUse: "text-cyan-400",
  CodeChange: "text-emerald-400",
  Error: "text-red-400",
  Progress: "text-primary-400",
};

const EVENT_LABEL_KEYS: Record<string, string> = {
  AgentThinking: "agentStream.thinking",
  ToolUsage: "agentStream.tool",
  ToolUse: "agentStream.tool",
  CodeChange: "agentStream.code",
  Error: "agentStream.error",
  Progress: "agentStream.progress",
};

/** Group consecutive events of the same type into single text blocks */
function groupEvents(events: StreamEvent[]): GroupedBlock[] {
  const blocks: GroupedBlock[] = [];
  for (let i = 0; i < events.length; i++) {
    const event = events[i];
    const last = blocks[blocks.length - 1];
    if (last && last.type === event.type) {
      last.content += (event.type === "ToolUse" || event.type === "ToolUsage") ? "\n" + event.content : event.content;
    } else {
      blocks.push({ type: event.type, content: event.content, startIndex: i });
    }
  }
  return blocks;
}

function AgentStream({ events, isActive, showToolCalls = false }: AgentStreamProps) {
  const t = useTranslations("dashboard");
  const scrollRef = useRef<HTMLDivElement>(null);
  const visibleEvents = useMemo(
    () =>
      showToolCalls
        ? events
        : events.filter((e) => e.type !== "ToolUse" && e.type !== "ToolUsage"),
    [events, showToolCalls]
  );
  const blocks = useMemo(() => groupEvents(visibleEvents), [visibleEvents]);

  useEffect(() => {
    if (scrollRef.current) {
      scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
    }
  }, [visibleEvents.length]);

  if (!isActive && visibleEvents.length === 0) return null;

  return (
    <GlassCard padding="none">
      <div className="border-b border-white/[0.04] px-5 py-3 flex items-center justify-between">
        <div className="flex items-center gap-2">
          <h3 className="font-display text-sm font-semibold text-white">
            {t("agentStream.agentActivity")}
          </h3>
          {isActive && (
            <span className="relative flex h-2 w-2">
              <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-emerald-400 opacity-75" />
              <span className="relative inline-flex h-2 w-2 rounded-full bg-emerald-400" />
            </span>
          )}
        </div>
        {visibleEvents.length > 0 && (
          <span className="text-xs text-gray-400">
            {visibleEvents.length} events
          </span>
        )}
      </div>
      <div
        ref={scrollRef}
        className="max-h-[300px] overflow-y-auto p-4 text-sm space-y-3"
        style={{ scrollbarWidth: "thin" }}
      >
        {visibleEvents.length === 0 && isActive && (
          <div className="flex items-center gap-2 text-gray-400 text-xs">
            <span className="h-1 w-1 animate-bounce rounded-full bg-gray-500 [animation-delay:0ms]" />
            <span className="h-1 w-1 animate-bounce rounded-full bg-gray-500 [animation-delay:150ms]" />
            <span className="h-1 w-1 animate-bounce rounded-full bg-gray-500 [animation-delay:300ms]" />
            <span className="ml-1">{t("agentStream.waitingForAgent")}</span>
          </div>
        )}
        {blocks.map((block) => {
          const color = EVENT_COLORS[block.type] ?? EVENT_COLORS.Progress;
          const labelKey = EVENT_LABEL_KEYS[block.type] ?? EVENT_LABEL_KEYS.Progress;
          return (
            <div key={block.startIndex}>
              <span className={cn("text-label", color)}>
                {t(labelKey)}
              </span>
              <p className="mt-1 text-gray-300 whitespace-pre-wrap break-words leading-relaxed">
                {block.content.replaceAll("\\n", "\n")}
              </p>
            </div>
          );
        })}
      </div>
    </GlassCard>
  );
}

export { AgentStream };
