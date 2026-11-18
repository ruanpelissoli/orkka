import { useState, useRef, useEffect, useCallback, useMemo } from "react";
import { useAuth } from "@/lib/local-auth-client";
import { Link } from "@/i18n/navigation";
import { AnimatePresence, motion } from "framer-motion";
import { cn } from "@/lib/utils";
import { fadeUpFromBelow } from "@/lib/motion";
import { Button, Avatar, Markdown } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { SkillSlashMenu, type SkillSlashMenuHandle } from "@/components/skills";
import { usePermissions } from "@/hooks/use-permissions";
import { streamAgentMessage } from "@/lib/sse";
import {
  findJsonBlocks,
  parseQuestionsPayload,
  QuestionsCard,
  QuestionsGroup,
  QuestionsInProgress,
  QUESTIONS_MARKER,
  parseQuestionAnswers,
  QuestionAnswers,
} from "@/components/chat/QuestionBlocks";
import type { AgentQuestion } from "@/components/chat/QuestionBlocks";
import { useAgentChatSignalR } from "@/hooks/use-agent-chat-signalr";
import {
  ChatContextButtons,
  ChatContextChips,
  contextRefMarkers,
  type ChatContextRef,
} from "@/components/chat/ChatContextControls";
import { api } from "@/lib/api";
import { useClientApi } from "@/hooks/use-client-api";
import type { AgentConversation, AgentMessage, AgentMessageDesignAsset, AgentMessageDiagram, AgentMessageDocument, AgentMessageTask, DesignAsset, ProjectAgent } from "@/lib/types";
import { useTranslations } from "use-intl";
import { formatMessageTime, formatRelativeTime, latestDocumentVersions } from "@/lib/utils";
import { refreshData } from "@/lib/data-refresh";

interface AgentChatTabProps {
  agent: ProjectAgent;
  projectId: string;
  /** Deep link from the Design gallery: pre-attaches this asset to the next message. */
  initialAssetId?: string | null;
  /** History deep-link: open this conversation instead of the most recent one. */
  initialConversationId?: string | null;
  /** History deep-link: scroll to and briefly highlight this message once loaded. */
  highlightMessageId?: string | null;
  /** Gallery folder open behind the drawer — design assets the agent creates are filed
   * there rather than at the root. */
  designFolderPath?: string | null;
}

/** True when a completed assistant turn created artifacts (diagrams, tasks, documents,
 * design assets) that live on the page rendered behind the chat drawer. */
function messageHasArtifacts(message: AgentMessage | undefined): boolean {
  return (
    !!message &&
    message.role === "assistant" &&
    !!(
      message.diagrams?.length ||
      message.tasks?.length ||
      message.documents?.length ||
      message.designAssets?.length
    )
  );
}

interface StreamExtras {
  diagrams: AgentMessageDiagram[];
  tasks: AgentMessageTask[];
  documents: AgentMessageDocument[];
  designAssets: AgentMessageDesignAsset[];
}

const EMPTY_STREAM_EXTRAS: StreamExtras = { diagrams: [], tasks: [], documents: [], designAssets: [] };

function AgentChatTab({
  agent,
  projectId,
  initialAssetId,
  initialConversationId,
  highlightMessageId,
  designFolderPath,
}: AgentChatTabProps) {
  const t = useTranslations("dashboard.agentChat");
  const tTab = useTranslations("dashboard.agentChatTab");
  const { getToken } = useAuth();
  const { has } = usePermissions();
  const canChat = has("AgentsChat");
  const withToken = useClientApi();
  const scrollRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLTextAreaElement>(null);
  const slashMenuRef = useRef<SkillSlashMenuHandle>(null);

  const [conversations, setConversations] = useState<AgentConversation[]>([]);
  const [activeConversationId, setActiveConversationId] = useState<string | null>(null);
  const [messages, setMessages] = useState<AgentMessage[]>([]);
  const [inputValue, setInputValue] = useState("");
  const [isStreaming, setIsStreaming] = useState(false);
  const [streamingText, setStreamingText] = useState("");
  const [thinkingText, setThinkingText] = useState("");
  const [showThinking, setShowThinking] = useState(false);
  const [isLoading, setIsLoading] = useState(true);
  const [isLoadingMessages, setIsLoadingMessages] = useState(false);
  const [hasMoreMessages, setHasMoreMessages] = useState(false);
  const [isLoadingOlder, setIsLoadingOlder] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [showConversations, setShowConversations] = useState(false);
  const skipAutoScrollRef = useRef(false);
  const wasAtBottomRef = useRef(true);
  const hasAutoScrolledRef = useRef(false);
  const streamingTextRef = useRef("");
  const thinkingTextRef = useRef("");
  const streamDiagramsRef = useRef<AgentMessageDiagram[]>([]);
  const streamTasksRef = useRef<AgentMessageTask[]>([]);
  const streamDocumentsRef = useRef<AgentMessageDocument[]>([]);
  const streamDesignAssetsRef = useRef<AgentMessageDesignAsset[]>([]);
  // What the streaming bubble renders: a copy of the four refs above, taken whenever one of
  // them changes. The refs stay the source of truth for the stream callbacks (which need
  // the latest values synchronously); render never reads them.
  const [streamExtras, setStreamExtras] = useState<StreamExtras>(EMPTY_STREAM_EXTRAS);
  const syncStreamExtras = useCallback(() => {
    setStreamExtras({
      diagrams: streamDiagramsRef.current,
      tasks: streamTasksRef.current,
      documents: streamDocumentsRef.current,
      designAssets: streamDesignAssetsRef.current,
    });
  }, []);
  // Set when the turn reported a failed asset. A reported failure is not proof of one:
  // the same lost-trailing-events problem that drops assets also strands this flag, so it
  // only triggers reconciliation rather than deciding the outcome on its own.
  const designFailedRef = useRef(false);
  const [designProgress, setDesignProgress] = useState<string | null>(null);
  const [attachedAsset, setAttachedAsset] = useState<DesignAsset | null>(null);
  // Context linked to the NEXT message: uploaded files + existing documents/design assets.
  const [contextRefs, setContextRefs] = useState<ChatContextRef[]>([]);
  const [uploadingContext, setUploadingContext] = useState(false);
  // A detached run is generating a reply server-side (panel was closed and reopened
  // mid-reply, or another device sent the message) — chat runs survive disconnects.
  const [remoteRunActive, setRemoteRunActive] = useState(false);

  // History deep-link: once the target message is in the loaded page, scroll to it and
  // flash a highlight. Best-effort — an older (paginated-out) message simply isn't flagged.
  // Consumed once, during render, the first time the target is in the loaded page; the
  // effect below then scrolls to it and clears the flash.
  const [highlightConsumed, setHighlightConsumed] = useState(false);
  const [highlightActive, setHighlightActive] = useState(false);
  if (
    highlightMessageId &&
    !highlightConsumed &&
    !isLoadingMessages &&
    messages.some((m) => m.id === highlightMessageId)
  ) {
    setHighlightConsumed(true);
    setHighlightActive(true);
  }
  useEffect(() => {
    if (!highlightActive || !highlightMessageId) return;
    requestAnimationFrame(() => {
      document
        .getElementById(`chat-msg-${highlightMessageId}`)
        ?.scrollIntoView({ block: "center" });
    });
    const timer = setTimeout(() => setHighlightActive(false), 3000);
    return () => clearTimeout(timer);
  }, [highlightActive, highlightMessageId]);

  // Iterate deep-link: fetch the referenced asset and show it as an attachment chip
  useEffect(() => {
    if (!initialAssetId) return;
    let cancelled = false;
    withToken((token) => api.designAssets.get(projectId, initialAssetId, token))
      .then((asset) => {
        if (!cancelled) setAttachedAsset(asset);
      })
      .catch(() => {
        /* asset gone — chip simply doesn't appear */
      });
    return () => { cancelled = true; };
  }, [projectId, initialAssetId, withToken]);

  // SignalR: listen for messages pushed from the server
  const onSignalRMessage = useCallback((message: AgentMessage) => {
    // A pushed assistant message means the detached run delivered its reply
    if (message.role === "assistant") {
      setRemoteRunActive(false);
      setConversations((prev) =>
        prev.map((c) => (c.id === message.conversationId ? { ...c, hasActiveRun: false } : c))
      );
      // Artifacts land on the host page (Design/Documents/Architect) — re-render its
      // server data so they appear behind the drawer without a manual reload
      if (messageHasArtifacts(message)) refreshData();
    }
    // Skip if we're currently streaming (we handle that via SSE)
    if (streamingTextRef.current) return;
    setMessages((prev) => {
      // A re-push of an existing id carries updated content (e.g. a design-failure note
      // appended after generation) — replace instead of dropping it as a duplicate.
      const existing = prev.findIndex((m) => m.id === message.id);
      if (existing >= 0) {
        const next = [...prev];
        next[existing] = message;
        return next;
      }
      return [...prev, message];
    });
  }, []);

  const { isConnected } = useAgentChatSignalR({
    conversationId: activeConversationId,
    onNewMessage: onSignalRMessage,
  });

  // Load conversations on mount and auto-select the most recent one
  useEffect(() => {
    let cancelled = false;
    async function load() {
      setIsLoading(true);
      // Surface failures instead of rendering an empty chat — a 401/500 here used to be
      // indistinguishable from "no conversations yet"
      const convos = await withToken((token) =>
        api.agentChat.listConversations(projectId, agent.id, token)
      ).catch(() => {
        if (!cancelled) setError(tTab("loadConversationsFailed"));
        return [] as AgentConversation[];
      });
      if (cancelled) return;
      setConversations(convos);
      // Auto-select: a history deep-link's conversation wins over the most recent one
      if (convos.length > 0 && !activeConversationId) {
        const deepLinked =
          initialConversationId && convos.find((c) => c.id === initialConversationId);
        setActiveConversationId(deepLinked ? deepLinked.id : convos[0].id);
      }
      setIsLoading(false);
    }
    load();
    return () => { cancelled = true; };
  }, [projectId, agent.id]); // eslint-disable-line react-hooks/exhaustive-deps

  // When the active conversation changes, reset the thread during render: empty with no
  // conversation, else loading until the effect below has fetched its messages.
  const messagesKey = `${projectId}:${agent.id}:${activeConversationId ?? ""}`;
  const [prevMessagesKey, setPrevMessagesKey] = useState(messagesKey);
  if (messagesKey !== prevMessagesKey) {
    setPrevMessagesKey(messagesKey);
    if (!activeConversationId) {
      setMessages([]);
      setIsLoadingMessages(false);
    } else {
      setIsLoadingMessages(true);
    }
  }

  // Load messages when active conversation changes
  useEffect(() => {
    if (!activeConversationId) return;
    let cancelled = false;
    async function load() {
      const page = await withToken((token) =>
        api.agentChat.listMessages(projectId, agent.id, activeConversationId!, token)
      ).catch(() => {
        if (!cancelled) setError(tTab("loadMessagesFailed"));
        return { messages: [] as AgentMessage[], hasMore: false };
      });
      if (cancelled) return;
      setMessages(page.messages);
      setHasMoreMessages(page.hasMore);
      setIsLoadingMessages(false);
    }
    load();
    return () => { cancelled = true; };
    // Deps are the CONVERSATION only. withToken and tTab are new function identities on
    // every render of the providers above them, and a refreshData() after an
    // artifact-creating turn is enough to cause one — so listing them here reloaded the
    // whole thread the moment a reply finished. The list was replaced, the scroll container
    // was rebuilt from empty, and because the reloaded list has the SAME length as the
    // optimistic one the auto-scroll below never fired: the thread sat at the top after
    // every reply that created something (2026-09-08).
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId, agent.id, activeConversationId]);

  // Reopen shows a run still in progress: the server marks the conversation while a
  // detached reply is being generated (the mapper hides stale markers server-side).
  // Re-read during render whenever the list or the active conversation changes.
  const [prevRunSource, setPrevRunSource] = useState({ conversations, activeConversationId });
  if (
    prevRunSource.conversations !== conversations ||
    prevRunSource.activeConversationId !== activeConversationId
  ) {
    setPrevRunSource({ conversations, activeConversationId });
    const active = conversations.find((c) => c.id === activeConversationId);
    setRemoteRunActive(!!active?.hasActiveRun);
  }

  // Fallback for a missed SignalR push: while a detached run is shown in progress,
  // poll until the server clears the marker, then load the reply from the DB.
  useEffect(() => {
    if (!remoteRunActive || !activeConversationId) return;
    const conversationId = activeConversationId;
    const interval = setInterval(async () => {
      try {
        const convos = await withToken((token) =>
          api.agentChat.listConversations(projectId, agent.id, token)
        );
        const active = convos.find((c) => c.id === conversationId);
        if (active && !active.hasActiveRun) {
          setConversations(convos);
          const page = await withToken((token) =>
            api.agentChat.listMessages(projectId, agent.id, conversationId, token)
          );
          setMessages(page.messages);
          setHasMoreMessages(page.hasMore);
          setRemoteRunActive(false);
          if (messageHasArtifacts(page.messages[page.messages.length - 1])) refreshData();
        }
      } catch {
        // transient — the next tick retries
      }
    }, 10_000);
    return () => clearInterval(interval);
  }, [remoteRunActive, activeConversationId, projectId, agent.id, withToken]);

  // Auto-scroll on new messages / streaming / thinking — but not when older
  // history was just prepended, which must keep the viewport where it was.
  useEffect(() => {
    if (skipAutoScrollRef.current) {
      skipAutoScrollRef.current = false;
      return;
    }
    const el = scrollRef.current;
    if (!el) return;
    // Only follow the conversation if the viewer was already at the bottom.
    // Scrolling up to re-read something and being dragged back down by the
    // agent's next token is the worst thing a live thread can do.
    if (!wasAtBottomRef.current) return;
    // Smooth once the thread is live, instant on first paint — a scroll
    // animation down a long history on open is just a distraction. Streaming
    // text lands token by token, so this fires often; "smooth" coalesces.
    const first = !hasAutoScrolledRef.current;
    hasAutoScrolledRef.current = true;
    el.scrollTo({ top: el.scrollHeight, behavior: first ? "auto" : "smooth" });
    // isLoadingMessages included on purpose: whenever the thread is re-fetched the DOM is
    // rebuilt and the container returns to the top, and the message COUNT alone cannot see
    // that — the same messages arriving again leave it unchanged.
  }, [messages.length, streamingText, thinkingText, isLoadingMessages]);

  // Tracked from the scroll handler so it reflects where the viewer was before
  // the new content arrived. 64px of slack covers a partially visible last
  // message and sub-pixel scroll heights.
  function handleScroll() {
    const el = scrollRef.current;
    if (!el) return;
    wasAtBottomRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < 64;
  }

  const handleLoadOlder = useCallback(async () => {
    if (!activeConversationId || messages.length === 0 || isLoadingOlder) return;
    setIsLoadingOlder(true);
    const previousHeight = scrollRef.current?.scrollHeight ?? 0;
    try {
      const page = await withToken((token) =>
        api.agentChat.listMessages(projectId, agent.id, activeConversationId, token, {
          before: messages[0].createdAt,
        })
      );
      skipAutoScrollRef.current = true;
      setMessages((prev) => [...page.messages, ...prev]);
      setHasMoreMessages(page.hasMore);
      requestAnimationFrame(() => {
        if (scrollRef.current) {
          scrollRef.current.scrollTop += scrollRef.current.scrollHeight - previousHeight;
        }
      });
    } catch {
      setError(tTab("loadMessagesFailed"));
    } finally {
      setIsLoadingOlder(false);
    }
  }, [projectId, agent.id, activeConversationId, messages, isLoadingOlder, withToken, tTab]);

  const handleNewConversation = useCallback(async () => {
    try {
      const convo = await withToken((token) =>
        api.agentChat.createConversation(projectId, agent.id, token)
      );
      setConversations((prev) => [convo, ...prev]);
      setActiveConversationId(convo.id);
      setMessages([]);
      setShowConversations(false);
      setError(null);
      inputRef.current?.focus();
    } catch {
      setError(tTab("createConversationFailed"));
    }
  }, [projectId, agent.id, withToken, tTab]);

  const handleDeleteConversation = useCallback(async (conversationId: string) => {
    try {
      await withToken((token) =>
        api.agentChat.deleteConversation(projectId, agent.id, conversationId, token)
      );
      setConversations((prev) => prev.filter((c) => c.id !== conversationId));
      if (activeConversationId === conversationId) {
        setActiveConversationId(null);
        setMessages([]);
      }
    } catch {
      setError(tTab("deleteConversationFailed"));
    }
  }, [projectId, agent.id, activeConversationId, withToken, tTab]);

  /**
   * Re-reads a conversation after a turn that should have produced design assets but
   * didn't deliver them over SSE. Retries briefly instead of reading once: the done event
   * can beat the asset's own commit by a moment, and a single immediate read would just
   * confirm the missing state and leave a false "generation failed" banner on screen.
   */
  const reconcileDesignAssets = useCallback(
    async (conversationId: string) => {
      const delaysMs = [0, 1200, 3000];
      for (let attempt = 0; attempt < delaysMs.length; attempt++) {
        if (delaysMs[attempt] > 0) {
          await new Promise((resolve) => setTimeout(resolve, delaysMs[attempt]));
        }
        try {
          const page = await withToken((token) =>
            api.agentChat.listMessages(projectId, agent.id, conversationId, token)
          );
          const newest = page.messages[page.messages.length - 1];
          const recovered = !!newest?.designAssets?.length;
          // Only overwrite the optimistic transcript once the assets actually landed (or
          // on the last try) — a lagging read would visibly drop the message just rendered.
          if (recovered || attempt === delaysMs.length - 1) {
            setMessages(page.messages);
            setHasMoreMessages(page.hasMore);
            if (recovered) setDesignProgress(null);
            if (messageHasArtifacts(newest)) refreshData();
            return;
          }
        } catch {
          // Best-effort: if every attempt fails the banner simply stays.
        }
      }
    },
    [withToken, projectId, agent.id]
  );

  const sendMessage = useCallback(async (content: string) => {
    if (!content || isStreaming || remoteRunActive) return;

    let conversationId = activeConversationId;

    // Auto-create conversation if none active
    if (!conversationId) {
      try {
        const convo = await withToken((token) =>
          api.agentChat.createConversation(projectId, agent.id, token)
        );
        setConversations((prev) => [convo, ...prev]);
        conversationId = convo.id;
        setActiveConversationId(convo.id);
      } catch {
        setError(tTab("createConversationFailed"));
        return;
      }
    }

    setError(null);

    // Add user message optimistically
    const userMsg: AgentMessage = {
      id: `temp-user-${Date.now()}`,
      conversationId: conversationId,
      role: "user",
      content,
      createdAt: new Date().toISOString(),
    };
    setMessages((prev) => [...prev, userMsg]);

    // Start streaming
    setIsStreaming(true);
    setStreamingText("");
    setThinkingText("");
    setShowThinking(false);
    streamingTextRef.current = "";
    thinkingTextRef.current = "";
    streamDiagramsRef.current = [];
    streamTasksRef.current = [];
    streamDocumentsRef.current = [];
    streamDesignAssetsRef.current = [];
    syncStreamExtras();
    designFailedRef.current = false;
    setDesignProgress(null);

    try {
      const token = await getToken();

      await streamAgentMessage(
        projectId,
        agent.id,
        conversationId,
        content,
        token,
        (text) => {
          streamingTextRef.current += text;
          setStreamingText(streamingTextRef.current);
        },
        () => {
          const finalText = streamingTextRef.current;
          const hasExtras =
            streamDiagramsRef.current.length > 0 ||
            streamTasksRef.current.length > 0 ||
            streamDocumentsRef.current.length > 0 ||
            streamDesignAssetsRef.current.length > 0;
          // A run cancelled before any text produced nothing to show — skip the empty bubble
          if (finalText || hasExtras) {
            const assistantMsg: AgentMessage = {
              id: `temp-assistant-${Date.now()}`,
              conversationId: conversationId!,
              role: "assistant",
              content: finalText,
              createdAt: new Date().toISOString(),
              diagrams: streamDiagramsRef.current.length > 0 ? streamDiagramsRef.current : undefined,
              tasks: streamTasksRef.current.length > 0 ? streamTasksRef.current : undefined,
              documents: streamDocumentsRef.current.length > 0 ? streamDocumentsRef.current : undefined,
              designAssets: streamDesignAssetsRef.current.length > 0 ? streamDesignAssetsRef.current : undefined,
            };
            setMessages((msgs) => [...msgs, assistantMsg]);
          }
          // A design block whose assets never arrived — or arrived only as a failure —
          // usually means the trailing SSE events were lost, not that generation failed:
          // long turns stream for minutes and assets land in the final second, so the
          // connection often drops between persistence and delivery. The assets ARE in
          // the DB; reconcile from there rather than leaving a false failure banner up.
          if (
            conversationId &&
            finalText.includes("```design") &&
            (streamDesignAssetsRef.current.length === 0 || designFailedRef.current)
          ) {
            void reconcileDesignAssets(conversationId);
          }
          setStreamingText("");
          setThinkingText("");
          setShowThinking(false);
          setDesignProgress(null);
          streamingTextRef.current = "";
          thinkingTextRef.current = "";
          setIsStreaming(false);
          // Artifacts persisted server-side during the run — refresh the host page's
          // server data so they appear behind the drawer without a manual reload
          if (hasExtras) refreshData();
        },
        (err) => {
          setError(err.message);
          setIsStreaming(false);
          setStreamingText("");
          setThinkingText("");
          setShowThinking(false);
          setDesignProgress(null);
          streamingTextRef.current = "";
          thinkingTextRef.current = "";
        },
        (thinking) => {
          thinkingTextRef.current += thinking;
          setThinkingText(thinkingTextRef.current);
          setShowThinking(true);
        },
        (diagram) => {
          streamDiagramsRef.current = [...streamDiagramsRef.current, diagram];
          syncStreamExtras();
        },
        (task) => {
          streamTasksRef.current = [...streamTasksRef.current, task];
          syncStreamExtras();
        },
        (document) => {
          streamDocumentsRef.current = [...streamDocumentsRef.current, document];
          syncStreamExtras();
        },
        (title) => {
          setConversations((prev) =>
            prev.map((c) => (c.id === conversationId ? { ...c, title } : c))
          );
        },
        (asset) => {
          if (asset.status === "failed" || !asset.id) {
            designFailedRef.current = true;
            setDesignProgress(t("designAssetFailed", { name: asset.name }));
            return;
          }
          streamDesignAssetsRef.current = [
            ...streamDesignAssetsRef.current,
            {
              id: asset.id,
              name: asset.name,
              kind: asset.kind,
              version: asset.version ?? 1,
              url: asset.url ?? "",
            },
          ];
          syncStreamExtras();
          setDesignProgress(null);
        },
        (progress) => {
          setDesignProgress(
            progress.frame && progress.totalFrames
              ? t("creatingDesignFrame", {
                  name: progress.name,
                  frame: progress.frame,
                  total: progress.totalFrames,
                })
              : t("creatingDesign", { name: progress.name })
          );
        },
        // Assets the agent creates land in the folder the user has open on the Design page.
        { designFolderPath }
      );
    } catch {
      setError(tTab("sendMessageFailed"));
      setIsStreaming(false);
      setStreamingText("");
      setThinkingText("");
      setDesignProgress(null);
      streamingTextRef.current = "";
      thinkingTextRef.current = "";
    }
  }, [isStreaming, remoteRunActive, activeConversationId, projectId, agent.id, getToken, withToken, t, tTab, reconcileDesignAssets, designFolderPath, syncStreamExtras]);

  // Explicit stop for the in-flight reply. Detached runs no longer die with the SSE
  // connection, so this is the only way to abort one.
  const handleCancelRun = useCallback(async () => {
    if (!activeConversationId) return;
    const conversationId = activeConversationId;
    try {
      await withToken((token) =>
        api.agentChat.cancelRun(projectId, agent.id, conversationId, token)
      );
    } catch {
      // The run may have just finished — the done event / refresh resolves the UI anyway
    }
    // A live stream ends via its cancelled done event; a reopened panel refreshes the
    // persisted partial once the run has wound down.
    if (!isStreaming) {
      setTimeout(async () => {
        try {
          const page = await withToken((token) =>
            api.agentChat.listMessages(projectId, agent.id, conversationId, token)
          );
          setMessages(page.messages);
          setHasMoreMessages(page.hasMore);
          // A cancelled run may still have persisted artifacts before it stopped
          if (messageHasArtifacts(page.messages[page.messages.length - 1])) refreshData();
        } catch {
          // keep the current view — the poll fallback catches up
        }
        setRemoteRunActive(false);
      }, 1000);
    }
  }, [activeConversationId, isStreaming, projectId, agent.id, withToken]);

  // Uploads picked files as chat attachments; each becomes a chip → marker on send.
  const handleContextFiles = useCallback(async (files: File[]) => {
    setUploadingContext(true);
    try {
      for (const file of files) {
        try {
          const uploaded = await withToken((token) =>
            api.chatAttachments.upload(projectId, file, token)
          );
          setContextRefs((prev) =>
            prev.some((r) => r.type === "attachment" && r.id === uploaded.id)
              ? prev
              : [...prev, { type: "attachment", id: uploaded.id, label: uploaded.fileName }]
          );
        } catch {
          setError(t("attachmentUploadFailed", { name: file.name }));
        }
      }
    } finally {
      setUploadingContext(false);
    }
  }, [projectId, withToken, t]);

  // Linkable project context for the picker (fetched on first open).
  const loadContextOptions = useCallback(async () => {
    const [documents, assets] = await Promise.all([
      withToken((token) => api.documents.list(projectId, token)),
      withToken((token) => api.designAssets.list(projectId, token)),
    ]);
    // Design gallery semantics: one entry per lineage, newest version.
    const latest = new Map<string, (typeof assets)[number]>();
    for (const asset of assets) {
      const current = latest.get(asset.rootAssetId);
      if (!current || asset.version > current.version) latest.set(asset.rootAssetId, asset);
    }
    return {
      // Same idea for documents: agents sometimes re-save instead of revising, so only
      // the newest copy of each folder+title is offered.
      documents: latestDocumentVersions(documents).map((d) => ({ id: d.id, title: d.title })),
      designAssets: [...latest.values()].map((a) => ({ id: a.id, name: a.name, version: a.version })),
    };
  }, [projectId, withToken]);

  const handleSend = useCallback(() => {
    const content = inputValue.trim();
    if (!content) return;
    setInputValue("");
    // Explicit, visible marker lines — the server resolves them into agent context and
    // they survive in history so later turns keep the reference.
    let markers = contextRefMarkers(contextRefs);
    if (attachedAsset) {
      markers = `[design-asset:${attachedAsset.id} "${attachedAsset.name}" v${attachedAsset.version}]\n\n` + markers;
      setAttachedAsset(null);
    }
    setContextRefs([]);
    sendMessage(markers + content);
  }, [inputValue, sendMessage, attachedAsset, contextRefs]);

  const handleKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    // "/" skill autocomplete owns arrows/Tab/Enter while it is open.
    if (slashMenuRef.current?.handleKeyDown(e)) return;
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      handleSend();
    }
  };

  // Show conversation list view
  if (showConversations) {
    return (
      <div className="flex h-full flex-col">
        <div className="flex items-center justify-between border-b border-white/[0.04] px-4 py-3">
          <button
            onClick={() => setShowConversations(false)}
            className="flex items-center gap-1.5 text-xs text-gray-400 transition-colors hover:text-white"
          >
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
              <path d="M19 12H5m7-7l-7 7 7 7" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
            {t("back")}
          </button>
          <RequirePermission permission="AgentsChat">
            <Button size="sm" onClick={handleNewConversation}>
              {t("newConversation")}
            </Button>
          </RequirePermission>
        </div>
        <div className="flex-1 overflow-y-auto p-3">
          {conversations.length === 0 ? (
            <div className="flex flex-col items-center justify-center py-12 text-center">
              <p className="text-sm text-gray-400">{t("noConversations")}</p>
            </div>
          ) : (
            <div className="space-y-1">
              {conversations.map((convo) => (
                <div
                  key={convo.id}
                  className={cn(
                    "group flex items-center justify-between rounded-xl px-3 py-2.5 transition-colors cursor-pointer",
                    convo.id === activeConversationId
                      ? "bg-white/[0.06] text-white"
                      : "text-gray-400 hover:bg-white/[0.03] hover:text-gray-200"
                  )}
                  onClick={() => {
                    setActiveConversationId(convo.id);
                    setShowConversations(false);
                  }}
                >
                  <div className="min-w-0 flex-1">
                    <p className="truncate text-sm font-medium">
                      {convo.title || t("untitled")}
                    </p>
                    <p className="mt-0.5 text-xs text-gray-400">
                      {convo.messageCount} {t("messages")} · {formatRelativeTime(convo.lastMessageAt || convo.createdAt)}
                    </p>
                  </div>
                  <RequirePermission permission="AgentsChat">
                    <button
                      onClick={(e) => {
                        e.stopPropagation();
                        handleDeleteConversation(convo.id);
                      }}
                      className="ml-2 shrink-0 rounded-lg p-1.5 text-gray-400 opacity-0 transition-all hover:bg-red-500/10 hover:text-red-400 group-hover:opacity-100"
                    >
                      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                        <path d="M3 6h18M8 6V4a2 2 0 012-2h4a2 2 0 012 2v2m3 0v14a2 2 0 01-2 2H7a2 2 0 01-2-2V6h14" strokeLinecap="round" strokeLinejoin="round" />
                      </svg>
                    </button>
                  </RequirePermission>
                </div>
              ))}
            </div>
          )}
        </div>
      </div>
    );
  }

  // Main chat view
  return (
    <div className="flex h-full flex-col">
      {/* Conversation header */}
      <div className="flex items-center justify-between border-b border-white/[0.04] px-4 py-2.5">
        <div className="flex items-center gap-2">
          <button
            onClick={() => setShowConversations(true)}
            className="flex items-center gap-1.5 text-xs text-gray-400 transition-colors hover:text-white"
          >
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
              <path d="M21 15a2 2 0 01-2 2H7l-4 4V5a2 2 0 012-2h14a2 2 0 012 2z" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
            {conversations.length > 0
              ? `${conversations.length} ${t("conversations")}`
              : t("noConversations")}
          </button>
          {isConnected && (
            <span className="relative flex h-1.5 w-1.5" title={tTab("live")}>
              <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-emerald-400 opacity-75" />
              <span className="relative inline-flex h-1.5 w-1.5 rounded-full bg-emerald-400" />
            </span>
          )}
        </div>
        <RequirePermission permission="AgentsChat">
          <Button size="sm" variant="ghost" onClick={handleNewConversation}>
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
              <path d="M12 5v14m-7-7h14" strokeLinecap="round" />
            </svg>
            {t("newConversation")}
          </Button>
        </RequirePermission>
      </div>

      {/* Messages area */}
      <div
        ref={scrollRef}
            onScroll={handleScroll}
        className="flex-1 space-y-4 overflow-y-auto p-4"
      >
        {isLoading || isLoadingMessages ? (
          <div className="flex items-center justify-center py-8">
            <div className="flex items-center gap-1">
              <span className="h-1.5 w-1.5 animate-bounce rounded-full bg-gray-400 [animation-delay:0ms]" />
              <span className="h-1.5 w-1.5 animate-bounce rounded-full bg-gray-400 [animation-delay:150ms]" />
              <span className="h-1.5 w-1.5 animate-bounce rounded-full bg-gray-400 [animation-delay:300ms]" />
            </div>
          </div>
        ) : messages.length === 0 && !activeConversationId ? (
          <div className="flex flex-col items-center justify-center py-12 text-center">
            <Avatar name={agent.name} size="lg" className="mb-4" />
            <p className="text-sm text-gray-400">
              {t("noMessages", { agentName: agent.name })}
            </p>
            <p className="mt-1 text-xs text-gray-400">
              {t("startTyping")}
            </p>
          </div>
        ) : messages.length === 0 ? (
          <div className="flex flex-col items-center justify-center py-12 text-center">
            <p className="text-sm text-gray-400">
              {t("emptyConversation", { agentName: agent.name })}
            </p>
          </div>
        ) : (
          <>
            {hasMoreMessages && (
              <div className="flex justify-center pb-2">
                <button
                  type="button"
                  onClick={handleLoadOlder}
                  disabled={isLoadingOlder}
                  className="rounded-full border border-gray-700 px-3 py-1 text-xs text-gray-400 hover:bg-gray-800 hover:text-gray-200 disabled:opacity-50"
                >
                  {isLoadingOlder ? "…" : t("loadEarlier")}
                </button>
              </div>
            )}
            {/* initial={false} so opening a conversation does not replay its
                whole history arriving — only messages that land while you are
                watching animate in. */}
            <AnimatePresence initial={false}>
              {messages.map((msg, idx) => (
                <motion.div
                  key={msg.id}
                  id={`chat-msg-${msg.id}`}
                  variants={fadeUpFromBelow}
                  initial="hidden"
                  animate="visible"
                  exit="exit"
                  className={cn(
                    "rounded-2xl transition-shadow duration-700",
                    highlightActive && msg.id === highlightMessageId &&
                      "ring-1 ring-primary-500/50 shadow-[0_0_0_4px_rgba(59,130,246,0.08)]"
                  )}
                >
                  <ChatBubble
                    message={msg}
                    agentName={agent.name}
                    projectId={projectId}
                    interactive={
                      canChat && !isStreaming && msg.role === "assistant" && idx === messages.length - 1
                    }
                    onQuestionsSubmit={sendMessage}
                  />
                </motion.div>
              ))}
            </AnimatePresence>
          </>
        )}

        {/* Thinking indicator */}
        {isStreaming && showThinking && thinkingText && !streamingText && (
          <ThinkingBlock text={thinkingText} agentName={agent.name} />
        )}

        {/* Streaming bubble (with collapsed thinking above) */}
        {isStreaming && streamingText && (
          <>
            {thinkingText && (
              <ThinkingBlock text={thinkingText} agentName={agent.name} collapsed />
            )}
            <ChatBubble
              message={{
                id: "streaming",
                conversationId: activeConversationId ?? "",
                role: "assistant",
                content: streamingText,
                createdAt: new Date().toISOString(),
                diagrams: streamExtras.diagrams.length > 0 ? streamExtras.diagrams : undefined,
                tasks: streamExtras.tasks.length > 0 ? streamExtras.tasks : undefined,
                documents: streamExtras.documents.length > 0 ? streamExtras.documents : undefined,
                designAssets: streamExtras.designAssets.length > 0 ? streamExtras.designAssets : undefined,
              }}
              agentName={agent.name}
              projectId={projectId}
              designProgress={designProgress}
              streaming
            />
            {/* The agent keeps working silently between text chunks (repo exploration via
                tools emits no text) — keep an explicit signal until the stream completes */}
            <div className="ml-11 flex items-center gap-2">
              <span className="flex items-center gap-1">
                <span className="h-1 w-1 animate-bounce rounded-full bg-gray-500 [animation-delay:0ms]" />
                <span className="h-1 w-1 animate-bounce rounded-full bg-gray-500 [animation-delay:150ms]" />
                <span className="h-1 w-1 animate-bounce rounded-full bg-gray-500 [animation-delay:300ms]" />
              </span>
              <span className="text-xs text-gray-400">
                {designProgress ?? t("working", { agentName: agent.name })}
              </span>
            </div>
          </>
        )}

        {/* Typing indicator (no thinking and no text yet, or a detached run in progress) */}
        {((isStreaming && !streamingText && !thinkingText) || remoteRunActive) && (
          <div className="flex gap-3">
            <Avatar name={agent.name} size="sm" className="mt-1 shrink-0" />
            <div className="rounded-2xl rounded-tl-md bg-white/[0.04] px-4 py-3">
              <div className="flex items-center gap-1">
                <span className="h-1.5 w-1.5 animate-bounce rounded-full bg-gray-400 [animation-delay:0ms]" />
                <span className="h-1.5 w-1.5 animate-bounce rounded-full bg-gray-400 [animation-delay:150ms]" />
                <span className="h-1.5 w-1.5 animate-bounce rounded-full bg-gray-400 [animation-delay:300ms]" />
              </div>
            </div>
          </div>
        )}
      </div>

      {/* Error */}
      {error && (
        <div className="mx-4 mb-2 rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2 text-xs text-red-400">
          {error}
        </div>
      )}

      {/* Input bar */}
      <RequirePermission permission="AgentsChat">
      <div className="border-t border-white/[0.04] p-3">
        {/* Attached design asset chip (iterate deep-link) */}
        {attachedAsset && (
          <div className="mb-2 flex items-center gap-2 rounded-xl border border-white/[0.06] bg-white/[0.03] px-2.5 py-1.5">
            <img
              src={attachedAsset.url}
              alt={attachedAsset.name}
              className="h-8 w-8 shrink-0 rounded-lg object-contain bg-white/[0.04]"
            />
            <span className="min-w-0 flex-1 truncate text-xs text-gray-300">
              {attachedAsset.name}{" "}
              <span className="text-gray-400">v{attachedAsset.version}</span>
            </span>
            <button
              onClick={() => setAttachedAsset(null)}
              className="shrink-0 rounded-lg p-1 text-gray-400 transition-colors hover:bg-white/[0.06] hover:text-gray-300"
              aria-label={t("removeAttachment")}
            >
              <svg width="12" height="12" viewBox="0 0 16 16" fill="none">
                <path d="M12 4L4 12M4 4l8 8" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
              </svg>
            </button>
          </div>
        )}
        {/* Context linked to the next message (uploads + documents + design assets) */}
        <ChatContextChips
          refs={contextRefs}
          onRemove={(ref) =>
            setContextRefs((prev) => prev.filter((r) => !(r.type === ref.type && r.id === ref.id)))
          }
        />
        <div className="flex items-end gap-2">
          <ChatContextButtons
            disabled={isStreaming || remoteRunActive}
            uploading={uploadingContext}
            onFiles={handleContextFiles}
            loadOptions={loadContextOptions}
            onAdd={(ref) =>
              setContextRefs((prev) =>
                prev.some((r) => r.type === ref.type && r.id === ref.id) ? prev : [...prev, ref]
              )
            }
            refs={contextRefs}
          />
          <div className="relative flex-1">
            <SkillSlashMenu
              ref={slashMenuRef}
              projectId={projectId}
              agentId={agent.id}
              inputValue={inputValue}
              disabled={isStreaming || remoteRunActive}
              onPick={(text) => {
                setInputValue(text);
                inputRef.current?.focus();
              }}
            />
            <textarea
              ref={inputRef}
              value={inputValue}
              onChange={(e) => setInputValue(e.target.value)}
              onKeyDown={handleKeyDown}
              placeholder={t("sendMessage", { agentName: agent.name })}
              rows={1}
              disabled={isStreaming || remoteRunActive}
              className={cn(
                "block min-h-10 w-full resize-none rounded-xl px-4 py-[9px] text-sm leading-5 text-gray-100",
                "bg-white/[0.03] backdrop-blur-sm",
                "border border-white/[0.06]",
                "placeholder:text-gray-500",
                "focus:bg-white/[0.06] focus:border-primary-500/50 focus:outline-none",
                "focus:shadow-[0_0_0_3px_rgba(59,130,246,0.1)]",
                "disabled:opacity-50",
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
          {isStreaming || remoteRunActive ? (
            <Button
              onClick={handleCancelRun}
              aria-label={t("stopGenerating")}
              title={t("stopGenerating")}
              className="shrink-0 h-10 w-10 !p-0 !rounded-xl"
            >
              <svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor">
                <rect x="6" y="6" width="12" height="12" rx="2" />
              </svg>
            </Button>
          ) : (
            <Button
              onClick={handleSend}
              disabled={!inputValue.trim()}
              className="shrink-0 h-10 w-10 !p-0 !rounded-xl"
            >
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                <path d="M22 2L11 13M22 2l-7 20-4-9-9-4 20-7z" strokeLinecap="round" strokeLinejoin="round" />
              </svg>
            </Button>
          )}
        </div>
      </div>
      </RequirePermission>
    </div>
  );
}

function ThinkingBlock({
  text,
  agentName,
  collapsed = false,
}: {
  text: string;
  agentName: string;
  collapsed?: boolean;
}) {
  const [isExpanded, setIsExpanded] = useState(!collapsed);

  return (
    <div className="flex gap-3 max-w-[85%]">
      {!collapsed && (
        <Avatar name={agentName} size="sm" className="mt-1 shrink-0" />
      )}
      <div className={cn("flex-1", collapsed && "ml-11")}>
        <button
          onClick={() => setIsExpanded(!isExpanded)}
          className="flex items-center gap-1.5 text-label text-violet-400 transition-colors hover:text-violet-300"
        >
          <svg
            width="10"
            height="10"
            viewBox="0 0 12 12"
            fill="none"
            className={cn("transition-transform", isExpanded && "rotate-90")}
          >
            <path d="M4 2l4 4-4 4" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
          {collapsed ? "Thought" : "Thinking..."}
          {!collapsed && (
            <span className="relative flex h-1.5 w-1.5">
              <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-violet-400 opacity-75" />
              <span className="relative inline-flex h-1.5 w-1.5 rounded-full bg-violet-400" />
            </span>
          )}
        </button>
        {isExpanded && (
          <div className="mt-1.5 max-h-40 overflow-y-auto rounded-xl bg-violet-500/[0.06] border border-violet-500/[0.1] px-3 py-2">
            <p className="whitespace-pre-wrap text-xs leading-relaxed text-violet-300/70">
              {text}
            </p>
          </div>
        )}
      </div>
    </div>
  );
}

type MessageSegment =
  | { type: "text"; content: string }
  | { type: "diagram"; index: number }
  | { type: "diagram-in-progress" }
  | { type: "tasks"; index: number }
  | { type: "tasks-in-progress" }
  | { type: "document"; index: number }
  | { type: "document-in-progress" }
  | { type: "design"; index: number }
  | { type: "design-in-progress" }
  | { type: "questions"; questions: AgentQuestion[] }
  | { type: "questions-in-progress" };

// Anchored to a line start: a fence mentioned mid-sentence (or inside a JSON string of a
// document block) is prose, not a block.
const EXTRACTED_BLOCK_REGEX = /(?<=^|\n)```(excalidraw|json|tasks)[^\S\n]*\n([\s\S]*?)```/g;
const OPEN_FENCE_REGEX = /(?<=^|\n)```(excalidraw|json|tasks)[^\S\n]*\n?([\s\S]*)$/;
const DOCUMENT_MARKER = "```document";
const DESIGN_MARKER = "```design";

function isExcalidrawJson(body: string): boolean {
  try {
    const parsed = JSON.parse(body);
    return parsed !== null && typeof parsed === "object" && parsed.type === "excalidraw";
  } catch {
    return false;
  }
}

/**
 * Splits message content into plain-text segments and extracted diagram/tasks/document
 * blocks. An unterminated fence (mid-stream) becomes an in-progress segment so raw JSON
 * never renders in the chat.
 */
function splitMessageSegments(content: string): MessageSegment[] {
  const segments: MessageSegment[] = [];
  let diagramIndex = 0;
  let tasksIndex = 0;
  let documentIndex = 0;
  let designIndex = 0;

  const pushText = (text: string) => {
    if (text.trim()) segments.push({ type: "text", content: text.trim() });
  };

  // Extracts diagram/tasks blocks from a block-free stretch of text. isTail means
  // this stretch ends the message, so an unterminated fence is a block mid-stream.
  const processPlainText = (text: string, isTail: boolean) => {
    let lastIndex = 0;
    for (const match of text.matchAll(EXTRACTED_BLOCK_REGEX)) {
      const [full, lang, body] = match;
      if (lang === "tasks") {
        pushText(text.slice(lastIndex, match.index));
        segments.push({ type: "tasks", index: tasksIndex++ });
        lastIndex = match.index + full.length;
        continue;
      }
      if (lang !== "excalidraw" && !isExcalidrawJson(body.trim())) continue;

      pushText(text.slice(lastIndex, match.index));
      segments.push({ type: "diagram", index: diagramIndex++ });
      lastIndex = match.index + full.length;
    }

    const tail = text.slice(lastIndex);
    const openFence = isTail ? tail.match(OPEN_FENCE_REGEX) : null;
    if (openFence && openFence[1] === "tasks") {
      pushText(tail.slice(0, openFence.index));
      segments.push({ type: "tasks-in-progress" });
    } else if (openFence && (openFence[1] === "excalidraw" || openFence[2].includes('"excalidraw"'))) {
      pushText(tail.slice(0, openFence.index));
      segments.push({ type: "diagram-in-progress" });
    } else {
      pushText(tail);
    }
  };

  // Questions blocks within a document-free stretch of text. An invalid payload falls
  // back to raw text (the block stays in the surrounding slice).
  const processText = (text: string, isTail: boolean) => {
    const { ranges, inProgressStart } = findJsonBlocks(text, QUESTIONS_MARKER);
    let cursor = 0;
    for (const range of ranges) {
      const questions = parseQuestionsPayload(range.payload);
      if (!questions) continue;
      processPlainText(text.slice(cursor, range.start), false);
      segments.push({ type: "questions", questions });
      cursor = range.end;
    }
    if (isTail && inProgressStart !== null && inProgressStart >= cursor) {
      processPlainText(text.slice(cursor, inProgressStart), false);
      segments.push({ type: "questions-in-progress" });
      return;
    }
    processPlainText(text.slice(cursor), isTail);
  };

  // Document blocks within a design-free stretch of text.
  const processDocumentLevel = (text: string, isTail: boolean) => {
    const { ranges, inProgressStart } = findJsonBlocks(text, DOCUMENT_MARKER);
    let cursor = 0;
    for (const range of ranges) {
      processText(text.slice(cursor, range.start), false);
      segments.push({ type: "document", index: documentIndex++ });
      cursor = range.end;
    }
    if (isTail && inProgressStart !== null && inProgressStart >= cursor) {
      processText(text.slice(cursor, inProgressStart), false);
      segments.push({ type: "document-in-progress" });
      return;
    }
    processText(text.slice(cursor), isTail);
  };

  const { ranges, inProgressStart } = findJsonBlocks(content, DESIGN_MARKER);
  let cursor = 0;
  for (const range of ranges) {
    processDocumentLevel(content.slice(cursor, range.start), false);
    segments.push({ type: "design", index: designIndex++ });
    cursor = range.end;
  }
  if (inProgressStart !== null) {
    processDocumentLevel(content.slice(cursor, inProgressStart), false);
    segments.push({ type: "design-in-progress" });
    return segments;
  }
  processDocumentLevel(content.slice(cursor), true);
  return segments;
}

function DiagramReadyCard({
  projectId,
  diagram,
}: {
  projectId: string;
  diagram?: AgentMessageDiagram;
}) {
  const t = useTranslations("dashboard.agentChat");
  const href = diagram
    ? `/p/${projectId}/architect?d=${diagram.id}`
    : `/p/${projectId}/architect`;

  return (
    <Link
      href={href}
      className={cn(
        "flex items-center gap-3 rounded-xl px-3 py-2.5 my-1.5",
        "border border-primary-500/20 bg-primary-500/[0.08]",
        "transition-colors hover:border-primary-500/40 hover:bg-primary-500/[0.14]"
      )}
    >
      <span className="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg bg-primary-500/15 text-primary-400">
        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5">
          <rect x="3" y="3" width="7" height="7" rx="1" strokeLinecap="round" />
          <rect x="14" y="14" width="7" height="7" rx="1" strokeLinecap="round" />
          <path d="M6.5 10v7.5a1 1 0 001 1H14M10 6.5h7.5a1 1 0 011 1V14" strokeLinecap="round" />
        </svg>
      </span>
      <span className="min-w-0 flex-1">
        <span className="block truncate text-sm font-medium text-gray-100">
          {diagram?.title || t("diagramReady")}
        </span>
        <span className="block text-xs text-primary-400">{t("viewOnArchitectBoard")}</span>
      </span>
      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" className="shrink-0 text-gray-400">
        <path d="M9 18l6-6-6-6" strokeLinecap="round" strokeLinejoin="round" />
      </svg>
    </Link>
  );
}

function DiagramInProgress() {
  const t = useTranslations("dashboard.agentChat");
  return (
    <div className="flex items-center gap-2 rounded-xl border border-white/[0.06] bg-white/[0.03] px-3 py-2.5 my-1.5">
      <span className="relative flex h-2 w-2 shrink-0">
        <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-primary-400 opacity-75" />
        <span className="relative inline-flex h-2 w-2 rounded-full bg-primary-400" />
      </span>
      <span className="text-xs text-gray-400">{t("drawingDiagram")}</span>
    </div>
  );
}

function TaskCreatedCard({
  projectId,
  task,
}: {
  projectId: string;
  task?: AgentMessageTask;
}) {
  const t = useTranslations("dashboard.agentChat");
  const href = task
    ? `/p/${projectId}/tasks/${task.id}`
    : `/p/${projectId}/board`;

  return (
    <Link
      href={href}
      className={cn(
        "flex items-center gap-3 rounded-xl px-3 py-2.5 my-1.5",
        "border border-emerald-500/20 bg-emerald-500/[0.08]",
        "transition-colors hover:border-emerald-500/40 hover:bg-emerald-500/[0.14]"
      )}
    >
      <span className="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg bg-emerald-500/15 text-emerald-400">
        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5">
          <path d="M9 11l3 3L22 4" strokeLinecap="round" strokeLinejoin="round" />
          <path d="M21 12v7a2 2 0 01-2 2H5a2 2 0 01-2-2V5a2 2 0 012-2h11" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      </span>
      <span className="min-w-0 flex-1">
        <span className="block truncate text-sm font-medium text-gray-100">
          {task ? `#${task.number} ${task.title}` : t("tasksReady")}
        </span>
        <span className="block text-xs text-emerald-400">
          {task ? t("viewTask") : t("viewOnBoard")}
        </span>
      </span>
      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" className="shrink-0 text-gray-400">
        <path d="M9 18l6-6-6-6" strokeLinecap="round" strokeLinejoin="round" />
      </svg>
    </Link>
  );
}

function TaskCreatedCards({
  projectId,
  tasks,
}: {
  projectId: string;
  tasks?: AgentMessageTask[] | null;
}) {
  // Extraction failed server-side (or tasks not loaded) — degrade to a generic board link
  if (!tasks || tasks.length === 0) {
    return <TaskCreatedCard projectId={projectId} />;
  }
  return (
    <div>
      {tasks.map((task) => (
        <TaskCreatedCard key={task.id} projectId={projectId} task={task} />
      ))}
    </div>
  );
}

function DocumentCreatedCard({
  projectId,
  document,
}: {
  projectId: string;
  document?: AgentMessageDocument;
}) {
  const t = useTranslations("dashboard.agentChat");
  const href = document
    ? `/p/${projectId}/documents?d=${document.id}`
    : `/p/${projectId}/documents`;

  return (
    <Link
      href={href}
      className={cn(
        "flex items-center gap-3 rounded-xl px-3 py-2.5 my-1.5",
        "border border-amber-500/20 bg-amber-500/[0.08]",
        "transition-colors hover:border-amber-500/40 hover:bg-amber-500/[0.14]"
      )}
    >
      <span className="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg bg-amber-500/15 text-amber-400">
        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5">
          <path d="M14 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V8z" strokeLinecap="round" strokeLinejoin="round" />
          <path d="M14 2v6h6M16 13H8M16 17H8M10 9H8" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      </span>
      <span className="min-w-0 flex-1">
        <span className="block truncate text-sm font-medium text-gray-100">
          {document?.title || t("documentReady")}
        </span>
        <span className="block text-xs text-amber-400">{t("viewInDocuments")}</span>
      </span>
      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" className="shrink-0 text-gray-400">
        <path d="M9 18l6-6-6-6" strokeLinecap="round" strokeLinejoin="round" />
      </svg>
    </Link>
  );
}

/** A block that never terminated on a finished message: the reply was cut off (usually at
 * the model's output limit) and nothing in the block was saved. Never a perpetual spinner. */
function BlockCutOff() {
  const t = useTranslations("dashboard.agentChat");
  return (
    <div className="my-1.5 flex items-center gap-2 rounded-xl border border-amber-500/20 bg-amber-500/[0.06] px-3 py-2.5">
      <span className="h-2 w-2 shrink-0 rounded-full bg-amber-400" />
      <span className="text-xs text-amber-200">{t("blockCutOff")}</span>
    </div>
  );
}

function DocumentInProgress() {
  const t = useTranslations("dashboard.agentChat");
  return (
    <div className="flex items-center gap-2 rounded-xl border border-white/[0.06] bg-white/[0.03] px-3 py-2.5 my-1.5">
      <span className="relative flex h-2 w-2 shrink-0">
        <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-amber-400 opacity-75" />
        <span className="relative inline-flex h-2 w-2 rounded-full bg-amber-400" />
      </span>
      <span className="text-xs text-gray-400">{t("savingDocument")}</span>
    </div>
  );
}

function DesignAssetReadyCard({
  projectId,
  asset,
}: {
  projectId: string;
  asset?: AgentMessageDesignAsset;
}) {
  const t = useTranslations("dashboard.agentChat");
  const href = asset
    ? `/p/${projectId}/design?a=${asset.id}`
    : `/p/${projectId}/design`;

  return (
    <Link
      href={href}
      className={cn(
        "flex items-center gap-3 rounded-xl px-3 py-2.5 my-1.5",
        "border border-violet-500/20 bg-violet-500/[0.08]",
        "transition-colors hover:border-violet-500/40 hover:bg-violet-500/[0.14]"
      )}
    >
      {asset?.url ? (
        <img
          src={asset.url}
          alt={asset.name}
          className="h-10 w-10 shrink-0 rounded-lg object-contain bg-white/[0.04]"
        />
      ) : (
        <span className="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg bg-violet-500/15 text-violet-400">
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5">
            <rect x="3" y="3" width="18" height="18" rx="2" strokeLinecap="round" />
            <circle cx="9" cy="9" r="2" />
            <path d="M21 15l-3.5-3.5a1.5 1.5 0 00-2.1 0L7 20" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
        </span>
      )}
      <span className="min-w-0 flex-1">
        <span className="block truncate text-sm font-medium text-gray-100">
          {asset ? `${asset.name}${asset.version > 1 ? ` v${asset.version}` : ""}` : t("designReady")}
        </span>
        <span className="block text-xs text-violet-400">{t("viewInDesign")}</span>
      </span>
      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" className="shrink-0 text-gray-400">
        <path d="M9 18l6-6-6-6" strokeLinecap="round" strokeLinejoin="round" />
      </svg>
    </Link>
  );
}

function DesignAssetCards({
  projectId,
  assets,
  inProgressText,
}: {
  projectId: string;
  assets?: AgentMessageDesignAsset[] | null;
  /** Set while the server is still generating (streaming bubble). */
  inProgressText?: string | null;
}) {
  // Streaming context (prop present, even if null): generation hasn't finished — show
  // progress. A PERSISTED message whose design block saved no assets means generation
  // failed (or the assets were deleted) — never render a "Design ready" card for it;
  // the reply text carries the persisted failure details.
  if (!assets || assets.length === 0) {
    if (inProgressText !== undefined) {
      return <DesignInProgress text={inProgressText} />;
    }
    return <DesignGenerationFailedNotice />;
  }
  return (
    <div>
      {assets.map((asset) => (
        <DesignAssetReadyCard key={asset.id} projectId={projectId} asset={asset} />
      ))}
    </div>
  );
}

function DesignGenerationFailedNotice() {
  const t = useTranslations("dashboard.agentChat");
  return (
    <div className="flex items-center gap-2 rounded-xl border border-amber-500/20 bg-amber-500/[0.06] px-3 py-2.5 my-1.5">
      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" className="shrink-0 text-amber-400">
        <path d="M10.29 3.86L1.82 18a2 2 0 001.71 3h16.94a2 2 0 001.71-3L13.71 3.86a2 2 0 00-3.42 0z" strokeLinecap="round" strokeLinejoin="round" />
        <path d="M12 9v4m0 4h.01" strokeLinecap="round" />
      </svg>
      <span className="text-xs text-amber-300/90">{t("designGenerationFailed")}</span>
    </div>
  );
}

function DesignInProgress({ text }: { text?: string | null }) {
  const t = useTranslations("dashboard.agentChat");
  return (
    <div className="flex items-center gap-2 rounded-xl border border-white/[0.06] bg-white/[0.03] px-3 py-2.5 my-1.5">
      <span className="relative flex h-2 w-2 shrink-0">
        <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-violet-400 opacity-75" />
        <span className="relative inline-flex h-2 w-2 rounded-full bg-violet-400" />
      </span>
      <span className="text-xs text-gray-400">{text || t("creatingDesigns")}</span>
    </div>
  );
}

function TasksInProgress() {
  const t = useTranslations("dashboard.agentChat");
  return (
    <div className="flex items-center gap-2 rounded-xl border border-white/[0.06] bg-white/[0.03] px-3 py-2.5 my-1.5">
      <span className="relative flex h-2 w-2 shrink-0">
        <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-emerald-400 opacity-75" />
        <span className="relative inline-flex h-2 w-2 rounded-full bg-emerald-400" />
      </span>
      <span className="text-xs text-gray-400">{t("creatingTasks")}</span>
    </div>
  );
}

// Matches the visible marker lines the iterate deep-link prepends to user messages.
const DESIGN_ASSET_MARKER_REGEX = /^\[design-asset:[0-9A-Za-z]{26}(?: "([^"\n]*)" v\d+)?\][^\S\n]*\n?/gm;

function ChatBubble({
  message,
  agentName,
  projectId,
  interactive = false,
  onQuestionsSubmit,
  designProgress,
  streaming = false,
}: {
  message: AgentMessage;
  agentName: string;
  projectId: string;
  /** True only for the latest assistant message while idle — question cards accept input */
  interactive?: boolean;
  onQuestionsSubmit?: (text: string) => void;
  /** Set on the streaming bubble while design assets are being generated. */
  designProgress?: string | null;
  /** The bubble is the live stream. An unterminated block is "in progress" only while this
   * is true; on a finished message it means the reply was cut off and nothing was saved. */
  streaming?: boolean;
}) {
  const isUser = message.role === "user";
  const segments = useMemo(
    () => (isUser ? null : splitMessageSegments(message.content)),
    [isUser, message.content]
  );
  // The message's last questions block carries the shared submit button.
  const lastQuestionsIndex =
    segments?.reduce((last, s, i) => (s.type === "questions" ? i : last), -1) ?? -1;
  // Raw markers would read as noise — show them as attachment chips instead
  const { userText, attachmentNames } = useMemo(() => {
    if (!isUser) return { userText: "", attachmentNames: [] as string[] };
    const names: string[] = [];
    for (const match of message.content.matchAll(DESIGN_ASSET_MARKER_REGEX)) {
      if (match[1]) names.push(match[1]);
    }
    return {
      userText: message.content.replace(DESIGN_ASSET_MARKER_REGEX, "").trim(),
      attachmentNames: names,
    };
  }, [isUser, message.content]);
  // A reply submitted from a QuestionsCard — show it as question/answer pairs, not markdown source
  const userQuestionAnswers = useMemo(
    () => (isUser ? parseQuestionAnswers(userText) : null),
    [isUser, userText]
  );

  return (
    <div
      className={cn(
        "flex gap-3 max-w-[85%]",
        isUser ? "ml-auto flex-row-reverse" : ""
      )}
    >
      <Avatar
        name={isUser ? "You" : agentName}
        size="sm"
        className="mt-1 shrink-0"
      />
      <div className={cn("flex min-w-0 flex-col gap-0.5", isUser ? "items-end" : "items-start")}>
      <div
        className={cn(
          // min-w-0 + max-w-full: a code block with long lines (a streaming JSON payload)
          // scrolls inside the bubble instead of widening it past the panel — before this
          // the whole thread grew a horizontal scrollbar and the text ran off the right edge.
          "min-w-0 max-w-full overflow-hidden rounded-2xl px-4 py-2.5 text-sm",
          isUser
            ? "rounded-tr-md bg-primary-600/20 text-gray-200"
            : "rounded-tl-md bg-white/[0.04] text-gray-300"
        )}
      >
        {isUser || !segments ? (
          <>
            {attachmentNames.length > 0 && (
              <span className="mb-1.5 flex flex-wrap gap-1">
                {attachmentNames.map((name, i) => (
                  <span
                    key={i}
                    className="inline-flex items-center gap-1 rounded-md bg-white/[0.08] px-1.5 py-0.5 text-xs text-gray-400"
                  >
                    <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                      <rect x="3" y="3" width="18" height="18" rx="2" />
                      <circle cx="9" cy="9" r="2" />
                      <path d="M21 15l-3.5-3.5a1.5 1.5 0 00-2.1 0L7 20" />
                    </svg>
                    {name}
                  </span>
                ))}
              </span>
            )}
            {userQuestionAnswers ? (
              <QuestionAnswers answers={userQuestionAnswers} />
            ) : (
              <p className="whitespace-pre-wrap leading-relaxed">{isUser ? userText : message.content}</p>
            )}
          </>
        ) : (
          // Every questions block of the message shares one submit — a turn that asks two
          // batches must answer both in a single reply, not silently drop one.
          <QuestionsGroup onSubmit={onQuestionsSubmit}>
          {segments.map((segment, i) =>
            segment.type === "text" ? (
              <Markdown key={i} className="text-inherit">{segment.content}</Markdown>
            ) : segment.type === "diagram" ? (
              <DiagramReadyCard
                key={i}
                projectId={projectId}
                diagram={message.diagrams?.[segment.index]}
              />
            ) : segment.type === "tasks" ? (
              // All created tasks attach to the message; render them at the first block only
              segment.index === 0 ? (
                <TaskCreatedCards key={i} projectId={projectId} tasks={message.tasks} />
              ) : null
            ) : segment.type === "tasks-in-progress" ? (
              <TasksInProgress key={i} />
            ) : segment.type === "document" ? (
              <DocumentCreatedCard
                key={i}
                projectId={projectId}
                document={message.documents?.[segment.index]}
              />
            ) : segment.type === "document-in-progress" ? (
              streaming ? <DocumentInProgress key={i} /> : <BlockCutOff key={i} />
            ) : segment.type === "design" ? (
              // All generated assets attach to the message; render them at the first block only
              segment.index === 0 ? (
                <DesignAssetCards
                  key={i}
                  projectId={projectId}
                  assets={message.designAssets}
                  inProgressText={designProgress}
                />
              ) : null
            ) : segment.type === "design-in-progress" ? (
              streaming ? <DesignInProgress key={i} text={designProgress} /> : <BlockCutOff key={i} />
            ) : segment.type === "questions" ? (
              <QuestionsCard
                key={i}
                questions={segment.questions}
                interactive={interactive}
                groupKey={i}
                ownsSubmit={i === lastQuestionsIndex}
              />
            ) : segment.type === "questions-in-progress" ? (
              <QuestionsInProgress key={i} />
            ) : (
              <DiagramInProgress key={i} />
            )
          )}
          </QuestionsGroup>
        )}
      </div>
      {/* The streaming placeholder's time isn't final until the message persists */}
      {message.id !== "streaming" && (
        <span
          className="block px-1 font-mono text-xs text-gray-400"
          title={new Date(message.createdAt).toLocaleString()}
        >
          {formatMessageTime(message.createdAt)}
        </span>
      )}
      </div>
    </div>
  );
}

export { AgentChatTab };
