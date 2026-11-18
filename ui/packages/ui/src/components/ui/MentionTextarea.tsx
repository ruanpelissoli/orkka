import {
  useState,
  useRef,
  useCallback,
  useEffect,
  type KeyboardEvent,
  type ChangeEvent,
} from "react";
import { motion, AnimatePresence } from "framer-motion";
import { useTranslations } from "use-intl";
import { cn } from "@/lib/utils";
import { api } from "@/lib/api";
import { useClientApi } from "@/hooks/use-client-api";

interface MentionTextareaProps {
  value: string;
  onChange: (value: string) => void;
  contextFiles: string[];
  onContextFilesChange: (files: string[]) => void;
  projectId: string;
  repoId: string | undefined;
  placeholder?: string;
  maxLength?: number;
  showCount?: boolean;
  label?: string;
  className?: string;
  autoFocus?: boolean;
  onBlur?: () => void;
}

function MentionTextarea({
  value,
  onChange,
  contextFiles,
  onContextFilesChange,
  projectId,
  repoId,
  placeholder,
  maxLength,
  showCount,
  label,
  className,
  autoFocus,
  onBlur,
}: MentionTextareaProps) {
  const t = useTranslations("dashboard.mentionTextarea");
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  const popupRef = useRef<HTMLDivElement>(null);
  const debounceRef = useRef<ReturnType<typeof setTimeout>>(null);
  const requestIdRef = useRef(0);
  const withToken = useClientApi();

  const [mentionQuery, setMentionQuery] = useState<string | null>(null);
  const [results, setResults] = useState<string[]>([]);
  const [activeIndex, setActiveIndex] = useState(0);
  // What the current `results` answered; "searching" is derived as "no answer
  // for the current query yet".
  const [searchedKey, setSearchedKey] = useState<string | null>(null);

  const isOpen = mentionQuery !== null;
  const searchKey = mentionQuery && repoId ? `${projectId}/${repoId}/${mentionQuery}` : null;
  const isSearching = searchKey !== null && searchedKey !== searchKey;

  // Closing the popup, an empty query or no repo clears the results.
  const [prevSearchKey, setPrevSearchKey] = useState(searchKey);
  if (searchKey !== prevSearchKey) {
    setPrevSearchKey(searchKey);
    if (searchKey === null) {
      setResults([]);
      setSearchedKey(null);
    }
  }

  // Auto-resize textarea
  useEffect(() => {
    const el = textareaRef.current;
    if (el) {
      el.style.height = "auto";
      el.style.height = el.scrollHeight + "px";
    }
  }, [value]);

  // Debounced search
  useEffect(() => {
    if (!mentionQuery || !repoId) return;

    const key = `${projectId}/${repoId}/${mentionQuery}`;
    const currentId = ++requestIdRef.current;

    if (debounceRef.current) clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(async () => {
      try {
        const files = await withToken((token) =>
          api.repos.searchFiles(projectId, repoId, mentionQuery, token)
        );
        if (requestIdRef.current === currentId) {
          setResults(files);
          setActiveIndex(0);
          setSearchedKey(key);
        }
      } catch {
        if (requestIdRef.current === currentId) {
          setResults([]);
          setSearchedKey(key);
        }
      }
    }, 250);

    return () => {
      if (debounceRef.current) clearTimeout(debounceRef.current);
    };
  }, [mentionQuery, projectId, repoId, withToken]);

  // Detect @ mention trigger from cursor position
  function detectMention(text: string, cursorPos: number) {
    const textBeforeCursor = text.slice(0, cursorPos);
    const match = textBeforeCursor.match(/@([\w./-]*)$/);
    if (match) {
      setMentionQuery(match[1]);
    } else {
      setMentionQuery(null);
    }
  }

  function handleChange(e: ChangeEvent<HTMLTextAreaElement>) {
    const newValue = e.target.value;
    onChange(newValue);
    detectMention(newValue, e.target.selectionStart);
  }

  function handleClick() {
    const el = textareaRef.current;
    if (el) {
      detectMention(value, el.selectionStart);
    }
  }

  const selectFile = useCallback(
    (filePath: string) => {
      const el = textareaRef.current;
      if (!el) return;

      const cursorPos = el.selectionStart;
      const textBeforeCursor = value.slice(0, cursorPos);
      const match = textBeforeCursor.match(/@([\w./-]*)$/);
      if (!match) return;

      const mentionStart = cursorPos - match[0].length;
      const before = value.slice(0, mentionStart);
      const after = value.slice(cursorPos);
      const insertion = `@${filePath} `;
      const newValue = before + insertion + after;

      onChange(newValue);
      setMentionQuery(null);

      // Add to contextFiles (deduplicated)
      if (!contextFiles.includes(filePath)) {
        onContextFilesChange([...contextFiles, filePath]);
      }

      // Restore focus and cursor position
      requestAnimationFrame(() => {
        el.focus();
        const newPos = mentionStart + insertion.length;
        el.setSelectionRange(newPos, newPos);
      });
    },
    [value, onChange, contextFiles, onContextFilesChange]
  );

  function handleKeyDown(e: KeyboardEvent<HTMLTextAreaElement>) {
    if (!isOpen || results.length === 0) return;

    if (e.key === "ArrowDown") {
      e.preventDefault();
      setActiveIndex((i) => (i + 1) % results.length);
    } else if (e.key === "ArrowUp") {
      e.preventDefault();
      setActiveIndex((i) => (i - 1 + results.length) % results.length);
    } else if (e.key === "Enter" || e.key === "Tab") {
      e.preventDefault();
      selectFile(results[activeIndex]);
    } else if (e.key === "Escape") {
      e.preventDefault();
      setMentionQuery(null);
    }
  }

  function removeContextFile(filePath: string) {
    onContextFilesChange(contextFiles.filter((f) => f !== filePath));
  }

  function handleBlur(e: React.FocusEvent) {
    // Don't trigger blur if clicking inside the popup
    if (popupRef.current?.contains(e.relatedTarget as Node)) return;
    setMentionQuery(null);
    onBlur?.();
  }

  const inputId = label?.toLowerCase().replace(/\s+/g, "-");

  return (
    <div className="space-y-1.5">
      {label && (
        <label
          htmlFor={inputId}
          className="block text-sm font-medium text-gray-300"
        >
          {label}
        </label>
      )}

      <div className="relative">
        <textarea
          ref={textareaRef}
          id={inputId}
          value={value}
          onChange={handleChange}
          onClick={handleClick}
          onKeyDown={handleKeyDown}
          onBlur={handleBlur}
          placeholder={placeholder}
          maxLength={maxLength}
          autoFocus={autoFocus}
          className={cn(
            "w-full min-h-[100px] rounded-xl px-4 py-3 text-sm text-gray-100 resize-none",
            "bg-white/[0.03] backdrop-blur-sm",
            "border border-white/[0.06]",
            "placeholder:text-gray-500",
            "transition-all duration-200",
            "focus:bg-white/[0.06] focus:border-primary-500/50",
            "focus:shadow-[0_0_0_3px_rgba(59,130,246,0.1),0_0_20px_rgba(59,130,246,0.1)]",
            "focus:outline-none",
            className
          )}
        />

        {/* Mention popup */}
        <AnimatePresence>
          {isOpen && (
            <motion.div
              ref={popupRef}
              initial={{ opacity: 0, y: -4, scale: 0.97 }}
              animate={{ opacity: 1, y: 0, scale: 1 }}
              exit={{ opacity: 0, y: -4, scale: 0.97 }}
              transition={{ duration: 0.15 }}
              className={cn(
                "absolute left-0 right-0 z-50 mt-1",
                "rounded-xl bg-gray-900/95 backdrop-blur-2xl",
                "border border-white/[0.08]",
                "shadow-[0_8px_40px_rgba(0,0,0,0.5)]",
                "max-h-[200px] overflow-y-auto"
              )}
              tabIndex={-1}
            >
              {!repoId ? (
                <div className="px-3 py-4 text-center text-xs text-gray-400">
                  {t("selectRepoFirst")}
                </div>
              ) : mentionQuery === "" ? (
                <div className="px-3 py-4 text-center text-xs text-gray-400">
                  {t("typeToSearch")}
                </div>
              ) : isSearching ? (
                <div className="px-3 py-4 text-center text-xs text-gray-400">
                  <span className="inline-block animate-pulse">{t("searching")}</span>
                </div>
              ) : results.length === 0 ? (
                <div className="px-3 py-4 text-center text-xs text-gray-400">
                  {t("noMatches")}
                </div>
              ) : (
                <div className="py-1">
                  {results.map((file, i) => (
                    <button
                      key={file}
                      type="button"
                      onMouseDown={(e) => {
                        e.preventDefault();
                        selectFile(file);
                      }}
                      onMouseEnter={() => setActiveIndex(i)}
                      className={cn(
                        "flex w-full items-center gap-2 px-3 py-1.5 text-left transition-colors",
                        i === activeIndex
                          ? "bg-white/[0.06] text-gray-100"
                          : "text-gray-400 hover:bg-white/[0.03]"
                      )}
                    >
                      <FileIcon className="shrink-0 text-gray-400" />
                      <span className="truncate text-xs">
                        {file}
                      </span>
                    </button>
                  ))}
                </div>
              )}
            </motion.div>
          )}
        </AnimatePresence>
      </div>

      {/* Context file chips */}
      {contextFiles.length > 0 && (
        <div className="flex flex-wrap gap-1.5">
          {contextFiles.map((file) => (
            <span
              key={file}
              className="inline-flex items-center gap-1 rounded-full border border-primary-500/20 bg-primary-500/10 px-2 py-0.5 text-xs text-primary-400"
              title={file}
            >
              <FileIcon className="h-2.5 w-2.5" />
              {file.split("/").pop()}
              <button
                type="button"
                onClick={() => removeContextFile(file)}
                className="ml-0.5 rounded-full p-0.5 transition-colors hover:bg-red-500/20 hover:text-red-400"
              >
                <svg
                  width="8"
                  height="8"
                  viewBox="0 0 16 16"
                  fill="none"
                  stroke="currentColor"
                  strokeWidth="2"
                >
                  <path
                    d="M4.5 4.5l7 7M11.5 4.5l-7 7"
                    strokeLinecap="round"
                  />
                </svg>
              </button>
            </span>
          ))}
        </div>
      )}

      {/* Character count */}
      {showCount && maxLength && (
        <div className="flex justify-end">
          <p
            className={cn(
              "text-xs transition-colors",
              value.length > maxLength * 0.9 ? "text-warning" : "text-gray-400"
            )}
          >
            {value.length}/{maxLength}
          </p>
        </div>
      )}
    </div>
  );
}

function FileIcon({ className }: { className?: string }) {
  return (
    <svg
      width="12"
      height="12"
      viewBox="0 0 16 16"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.5"
      className={className}
    >
      <path
        d="M9 1H4a1 1 0 00-1 1v12a1 1 0 001 1h8a1 1 0 001-1V5l-4-4z"
        strokeLinejoin="round"
      />
      <path d="M9 1v4h4" strokeLinejoin="round" />
    </svg>
  );
}

export { MentionTextarea };
