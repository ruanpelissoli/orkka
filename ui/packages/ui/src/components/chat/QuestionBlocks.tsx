import {
  createContext,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from "react";
import { useTranslations } from "use-intl";
import { Button } from "@/components/ui";
import { cn } from "@/lib/utils";
import { formatQuestionAnswers, type AgentQuestion, type QuestionAnswer } from "@/lib/questions";

/**
 * Question parsing is shared with the React Native app, which cannot import from
 * `packages/ui` — it lives in core and is re-exported here so existing importers
 * (and their tests) keep a single entry point.
 */
export {
  QUESTIONS_MARKER,
  opensFence,
  findJsonBlocks,
  parseQuestionsPayload,
  formatQuestionAnswers,
  parseQuestionAnswers,
  extractQuestions,
  splitQuestionSegments,
} from "@/lib/questions";
export type { AgentQuestion, QuestionAnswer, QuestionSegment } from "@/lib/questions";

/** A user bubble's answered-questions reply, rendered as question/answer pairs. */
export function QuestionAnswers({ answers }: { answers: QuestionAnswer[] }) {
  return (
    <dl className="space-y-2.5">
      {answers.map((qa, i) => (
        <div key={i}>
          <dt className="text-xs font-medium text-primary-200/80">{qa.question}</dt>
          <dd className="mt-0.5 whitespace-pre-wrap leading-relaxed">{qa.answer}</dd>
        </div>
      ))}
    </dl>
  );
}

/** Stable comparison key for a card's answers — used to skip no-op reports. */
function serializeAnswers(answers: QuestionAnswer[] | undefined): string {
  return JSON.stringify(answers ?? []);
}

interface QuestionsGroupValue {
  /** A card publishes its current answers; the group keeps them in message order. */
  report: (key: number, answers: QuestionAnswer[]) => void;
  hasAnswers: boolean;
  submit: () => void;
}

const QuestionsGroupContext = createContext<QuestionsGroupValue | null>(null);

/**
 * Shares one submit across every ```questions block of a single message. An agent that
 * emits two blocks in the same turn otherwise renders two independent cards: answering
 * both and pressing either button sends only that card's answers, and the other card goes
 * dead as soon as the reply lands. Inside a group each card reports its answers up and
 * only the card marked `ownsSubmit` renders the button, which sends every answer of the
 * message as one reply. Cards rendered outside a group keep their own button.
 */
export function QuestionsGroup({
  onSubmit,
  children,
}: {
  onSubmit?: (text: string) => void;
  children: ReactNode;
}) {
  const [answers, setAnswers] = useState<Record<number, QuestionAnswer[]>>({});

  const value = useMemo<QuestionsGroupValue>(
    () => ({
      report: (key, next) =>
        setAnswers((prev) =>
          serializeAnswers(prev[key]) === serializeAnswers(next)
            ? prev
            : { ...prev, [key]: next }
        ),
      hasAnswers: Object.values(answers).some((a) => a.length > 0),
      submit: () => {
        const all = Object.keys(answers)
          .map(Number)
          .sort((a, b) => a - b)
          .flatMap((key) => answers[key]);
        if (all.length > 0) onSubmit?.(formatQuestionAnswers(all));
      },
    }),
    [answers, onSubmit]
  );

  return (
    <QuestionsGroupContext.Provider value={value}>{children}</QuestionsGroupContext.Provider>
  );
}

/**
 * Checkbox glyph rendered on the options of a multiSelect question. Without it a
 * question that takes several answers looks identical to one that takes exactly one,
 * so people pick a single option and submit.
 */
function OptionCheckbox({ checked }: { checked: boolean }) {
  return (
    <span
      aria-hidden
      className={cn(
        "mr-1.5 inline-flex h-3 w-3 shrink-0 items-center justify-center rounded-[3px] border",
        checked
          ? "border-primary-400/80 bg-primary-500/60 text-white"
          : "border-white/25 bg-transparent"
      )}
    >
      {checked && (
        <svg
          viewBox="0 0 10 10"
          className="h-2 w-2"
          fill="none"
          stroke="currentColor"
          strokeWidth={2}
          strokeLinecap="round"
          strokeLinejoin="round"
        >
          <path d="M1.5 5.2 3.9 7.5 8.5 2.5" />
        </svg>
      )}
    </span>
  );
}

export function QuestionsCard({
  questions,
  interactive,
  onSubmit,
  groupKey = 0,
  ownsSubmit = true,
}: {
  questions: AgentQuestion[];
  interactive: boolean;
  onSubmit?: (text: string) => void;
  /** Position of this block within its message — orders answers inside a QuestionsGroup. */
  groupKey?: number;
  /** Only the message's last questions block renders the group's submit button. */
  ownsSubmit?: boolean;
}) {
  const t = useTranslations("dashboard.agentChat");
  const group = useContext(QuestionsGroupContext);
  const [selected, setSelected] = useState<Record<number, string[]>>({});
  const [customText, setCustomText] = useState<Record<number, string>>({});
  const [customOpen, setCustomOpen] = useState<Record<number, boolean>>({});

  const toggleOption = (qi: number, option: string, multi: boolean) => {
    setSelected((prev) => {
      const current = prev[qi] ?? [];
      if (multi) {
        return {
          ...prev,
          [qi]: current.includes(option)
            ? current.filter((o) => o !== option)
            : [...current, option],
        };
      }
      return { ...prev, [qi]: current.includes(option) ? [] : [option] };
    });
    // Single-select: picking an option closes the custom input
    if (!multi) setCustomOpen((prev) => ({ ...prev, [qi]: false }));
  };

  const toggleCustom = (qi: number, multi: boolean) => {
    setCustomOpen((prev) => ({ ...prev, [qi]: !prev[qi] }));
    if (!multi) setSelected((prev) => ({ ...prev, [qi]: [] }));
  };

  const answersFor = (qi: number): string[] => {
    const options = selected[qi] ?? [];
    const custom = customOpen[qi] ? (customText[qi] ?? "").trim() : "";
    return custom ? [...options, custom] : options;
  };

  const answers = questions
    .map((q, qi) => ({ question: q.question, answer: answersFor(qi).join(", ") }))
    .filter((qa) => qa.answer.length > 0);
  const hasAnyAnswer = answers.length > 0;

  // Keep the group's copy of this card's answers current, so its single button submits
  // every block of the message and not just the one it sits under.
  const serialized = serializeAnswers(answers);
  const report = group?.report;
  useEffect(() => {
    report?.(groupKey, JSON.parse(serialized) as QuestionAnswer[]);
  }, [report, groupKey, serialized]);

  const canSubmit = group ? group.hasAnswers : hasAnyAnswer;

  const handleSubmit = () => {
    if (!canSubmit) return;
    if (group) {
      group.submit();
      return;
    }
    onSubmit?.(formatQuestionAnswers(answers));
  };

  return (
    <div className="my-1.5 space-y-3 rounded-xl border border-primary-500/20 bg-primary-500/[0.05] p-3">
      {questions.map((q, qi) => (
        <div key={qi}>
          <p className="text-xs font-medium text-gray-200">{q.question}</p>
          {q.multiSelect && (
            <p className="mt-0.5 text-label text-gray-400">{t("questionsMultiHint")}</p>
          )}
          <div className="mt-1.5 flex flex-wrap gap-1.5">
            {q.options.map((option) => {
              const isSelected = (selected[qi] ?? []).includes(option);
              const isRecommended = q.recommended === option;
              return (
                <button
                  key={option}
                  disabled={!interactive}
                  role={q.multiSelect ? "checkbox" : undefined}
                  aria-checked={q.multiSelect ? isSelected : undefined}
                  aria-pressed={q.multiSelect ? undefined : isSelected}
                  onClick={() => toggleOption(qi, option, q.multiSelect)}
                  className={cn(
                    "inline-flex items-center rounded-full border px-2.5 py-1 text-xs transition-colors",
                    isSelected
                      ? "border-primary-500/60 bg-primary-500/20 text-primary-300"
                      : "border-white/[0.08] bg-white/[0.03] text-gray-300",
                    interactive && !isSelected && "hover:bg-white/[0.08] hover:text-gray-100",
                    !interactive && "opacity-60"
                  )}
                >
                  {q.multiSelect && <OptionCheckbox checked={isSelected} />}
                  {option}
                  {isRecommended && (
                    <span className="ml-1.5 rounded-full bg-emerald-500/15 px-1.5 py-px text-label text-emerald-300">
                      {t("questionsRecommended")}
                    </span>
                  )}
                </button>
              );
            })}
            {q.allowCustom && (
              <button
                disabled={!interactive}
                role={q.multiSelect ? "checkbox" : undefined}
                aria-checked={q.multiSelect ? customOpen[qi] === true : undefined}
                aria-pressed={q.multiSelect ? undefined : customOpen[qi] === true}
                onClick={() => toggleCustom(qi, q.multiSelect)}
                className={cn(
                  "inline-flex items-center rounded-full border border-dashed px-2.5 py-1 text-xs transition-colors",
                  customOpen[qi]
                    ? "border-primary-500/60 bg-primary-500/20 text-primary-300"
                    : "border-white/[0.12] bg-transparent text-gray-400",
                  interactive && !customOpen[qi] && "hover:bg-white/[0.06] hover:text-gray-200",
                  !interactive && "opacity-60"
                )}
              >
                {q.multiSelect && <OptionCheckbox checked={customOpen[qi] === true} />}
                {t("questionsOther")}
              </button>
            )}
          </div>
          {q.allowCustom && customOpen[qi] && interactive && (
            <input
              value={customText[qi] ?? ""}
              onChange={(e) => setCustomText((prev) => ({ ...prev, [qi]: e.target.value }))}
              placeholder={t("questionsCustomPlaceholder")}
              autoFocus
              className={cn(
                "mt-1.5 w-full rounded-lg border border-white/[0.08] bg-white/[0.03] px-2.5 py-1.5",
                "text-xs text-gray-200 placeholder:text-gray-500",
                "focus:border-primary-500/50 focus:outline-none"
              )}
            />
          )}
        </div>
      ))}
      {interactive && ownsSubmit && (
        <Button size="sm" onClick={handleSubmit} disabled={!canSubmit}>
          {t("questionsSubmit")}
        </Button>
      )}
    </div>
  );
}

export function QuestionsInProgress() {
  const t = useTranslations("dashboard.agentChat");
  return (
    <div className="flex items-center gap-2 rounded-xl border border-white/[0.06] bg-white/[0.03] px-3 py-2.5 my-1.5">
      <span className="relative flex h-2 w-2 shrink-0">
        <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-primary-400 opacity-75" />
        <span className="relative inline-flex h-2 w-2 rounded-full bg-primary-400" />
      </span>
      <span className="text-xs text-gray-400">{t("preparingQuestions")}</span>
    </div>
  );
}
