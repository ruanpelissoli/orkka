import { useRef, useEffect, useState } from "react";
import { useFormStatus } from "react-dom";
import { AnimatePresence, motion } from "framer-motion";
import { cn } from "@/lib/utils";
import { fadeUpFromBelow } from "@/lib/motion";
import { Button } from "@/components/ui";
import { OrcaStatus } from "@/components/animations";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { usePermissions } from "@/hooks/use-permissions";
import { MessageBubble } from "./MessageBubble";
import { sendMessage, continueTaskSpend, stopTaskSpend } from "@/lib/actions";
import type { TaskMessage } from "@/lib/types";
import { useTranslations } from "use-intl";

interface ConversationThreadProps {
  messages: TaskMessage[];
  taskId: string;
  /** True while an agent is running (or queued) on this task — keeps the thinking
   * indicator up across phase boundaries instead of only after a user send. */
  agentWorking?: boolean;
  /** Route ref for building in-app links inside message content (PR links). */
  projectId?: string;
  /** Friendly task number for the in-app PR review route. */
  taskNumber?: number;
  /**
   * The history is still on the wire. Distinct from an empty history: without it a task
   * whose messages have not landed yet claims "no messages yet", which is the one thing
   * a conversation must never say about a conversation that exists.
   */
  loading?: boolean;
}

function SubmitButton() {
  const { pending } = useFormStatus();
  return (
    <Button
      type="submit"
      disabled={pending}
      loading={pending}
      className="shrink-0 h-10 w-10 !p-0 !rounded-xl"
    >
      <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
        <path d="M22 2L11 13M22 2l-7 20-4-9-9-4 20-7z" strokeLinecap="round" strokeLinejoin="round" />
      </svg>
    </Button>
  );
}

function ConversationThread({ messages, taskId, agentWorking = false, projectId, taskNumber, loading = false }: ConversationThreadProps) {
  const t = useTranslations("dashboard");
  const { has } = usePermissions();
  const canChat = has("AgentsChat");
  const scrollRef = useRef<HTMLDivElement>(null);
  const formRef = useRef<HTMLFormElement>(null);
  // The agent's reply can take a while (it comes back over SignalR) — keep a
  // thinking indicator up after every send so the thread never looks frozen.
  const [awaitingReply, setAwaitingReply] = useState(false);
  // Set when the user messages a task whose agent is already mid-run: the message
  // is saved and joins the agent's context at its next run, not the current one.
  const [sentWhileWorking, setSentWhileWorking] = useState(false);

  // The indicator stays up for the whole run — an interim agent message means
  // progress, not completion.
  const showThinking = agentWorking || awaitingReply;

  // Whether the viewer is pinned to the bottom of the thread. Tracked from the
  // scroll handler rather than measured in the effect, so it reflects where the
  // viewer had scrolled to before the new message arrived.
  const wasAtBottom = useRef(true);
  const hasScrolledOnce = useRef(false);

  function handleScroll() {
    const el = scrollRef.current;
    if (!el) return;
    // 64px of slack: "close enough to the bottom" survives a partially visible
    // last message and sub-pixel scroll heights.
    wasAtBottom.current = el.scrollHeight - el.scrollTop - el.clientHeight < 64;
  }

  useEffect(() => {
    const el = scrollRef.current;
    if (!el) return;
    // Only follow the conversation if the viewer was already at the bottom.
    // Scrolling up to re-read something and being yanked back down by an agent's
    // next message is the single most annoying thing a live thread can do.
    if (!wasAtBottom.current) return;

    // Smooth while the thread is live; instant on first paint, where a scroll
    // animation from the top of a long history is just a distraction.
    const first = !hasScrolledOnce.current;
    hasScrolledOnce.current = true;
    el.scrollTo({ top: el.scrollHeight, behavior: first ? "auto" : "smooth" });
  }, [messages.length, showThinking]);

  // Any non-user message (agent reply, progress, system note) proves the
  // pipeline picked the message up — drop the indicator.
  const [prevMessages, setPrevMessages] = useState(messages);
  if (messages !== prevMessages) {
    setPrevMessages(messages);
    const last = messages[messages.length - 1];
    if (last && last.role !== "user") setAwaitingReply(false);
  }

  // The run ended — any queued user message is now part of the next run's context.
  const [prevAgentWorking, setPrevAgentWorking] = useState(agentWorking);
  if (agentWorking !== prevAgentWorking) {
    setPrevAgentWorking(agentWorking);
    if (!agentWorking) setSentWhileWorking(false);
  }

  async function handleSubmit(formData: FormData) {
    const content = formData.get("content") as string;
    if (!content.trim()) return;
    await sendMessage(taskId, content);
    setAwaitingReply(true);
    if (agentWorking) setSentWhileWorking(true);
    formRef.current?.reset();
  }

  async function handleQuestionsSubmit(text: string) {
    if (!text.trim()) return;
    await sendMessage(taskId, text);
    setAwaitingReply(true);
  }

  async function handleSpendDecision(decision: "continue" | "stop") {
    if (decision === "continue") {
      await continueTaskSpend(taskId, projectId);
      setAwaitingReply(true);
    } else {
      await stopTaskSpend(taskId, projectId);
    }
  }

  return (
    <div className="flex flex-col h-full">
      {/* Messages */}
      <div
        ref={scrollRef}
        onScroll={handleScroll}
        className="flex-1 space-y-3 lg:space-y-4 overflow-y-auto p-3 lg:p-4"
      >
        {loading && messages.length === 0 ? (
          <div className="space-y-3 py-2" aria-busy="true">
            <div className="h-14 w-4/5 rounded-xl bg-white/[0.04] shimmer" />
            <div className="ml-auto h-10 w-2/3 rounded-xl bg-white/[0.04] shimmer" />
            <div className="h-16 w-4/5 rounded-xl bg-white/[0.04] shimmer" />
          </div>
        ) : messages.length === 0 ? (
          <div className="flex items-center justify-center py-8 text-sm text-gray-400">
            {t("conversationThread.noMessages")}
          </div>
        ) : (
          // `initial={false}` so opening a task does not replay the whole
          // history arriving — only messages that land while you are watching
          // animate in.
          <AnimatePresence initial={false}>
            {messages.map((msg, idx) => (
              <motion.div
                key={msg.id}
                variants={fadeUpFromBelow}
                initial="hidden"
                animate="visible"
                exit="exit"
              >
                <MessageBubble
                  message={msg}
                  interactive={canChat && idx === messages.length - 1}
                  onQuestionsSubmit={handleQuestionsSubmit}
                  onSpendDecision={handleSpendDecision}
                  projectId={projectId}
                  taskNumber={taskNumber}
                />
              </motion.div>
            ))}
          </AnimatePresence>
        )}
        {sentWhileWorking && (
          <div className="flex justify-center py-1">
            <span className="max-w-[90%] rounded-2xl bg-white/[0.04] px-3 py-1 text-xs text-gray-400">
              {t("conversationThread.messageQueuedMidRun")}
            </span>
          </div>
        )}
        <AnimatePresence initial={false}>
          {showThinking && (
            <motion.div
              key="thinking"
              variants={fadeUpFromBelow}
              initial="hidden"
              animate="visible"
              exit="exit"
              className="flex w-fit items-center gap-2 rounded-2xl bg-white/[0.03] px-3 py-2.5"
            >
              {/* The orca dives while the agent works — the same signal the board
                  card gives, so "something is happening" looks the same wherever
                  you are watching from. */}
              <OrcaStatus state="working" size="xs" showHalo={false} />
              <span className="flex items-center gap-1.5">
                <span className="h-1.5 w-1.5 animate-bounce rounded-full bg-gray-400 [animation-delay:0ms]" />
                <span className="h-1.5 w-1.5 animate-bounce rounded-full bg-gray-400 [animation-delay:150ms]" />
                <span className="h-1.5 w-1.5 animate-bounce rounded-full bg-gray-400 [animation-delay:300ms]" />
              </span>
            </motion.div>
          )}
        </AnimatePresence>
      </div>

      {/* Input bar */}
      <RequirePermission permission="AgentsChat">
      <div className="border-t border-white/[0.04] p-2.5 lg:p-3">
        <form
          ref={formRef}
          action={handleSubmit}
          className="flex items-end gap-2"
        >
          <div className="relative flex-1">
            <textarea
              name="content"
              placeholder={t("conversationThread.sendMessage")}
              rows={1}
              className={cn(
                "block min-h-10 w-full resize-none rounded-xl px-4 py-[9px] text-sm leading-5 text-gray-100",
                "bg-white/[0.03] backdrop-blur-sm",
                "border border-white/[0.06]",
                "placeholder:text-gray-500",
                "focus:bg-white/[0.06] focus:border-primary-500/50 focus:outline-none",
                "focus:shadow-[0_0_0_3px_rgba(59,130,246,0.1)]",
                "max-h-32"
              )}
              onInput={(e) => {
                const target = e.target as HTMLTextAreaElement;
                target.style.height = "auto";
                const border = target.offsetHeight - target.clientHeight;
                target.style.height = Math.min(target.scrollHeight + border, 128) + "px";
              }}
            />
          </div>
          <SubmitButton />
        </form>
      </div>
      </RequirePermission>
    </div>
  );
}

export { ConversationThread };
