import { useState, useTransition } from "react";
import { useTranslations } from "use-intl";
import { Button } from "@/components/ui";
import { addPrComment } from "@/lib/actions";
import type { PrComment } from "@/lib/types";

interface InlineCommentFormProps {
  taskId: string;
  path: string;
  line: number;
  onCancel: () => void;
  onCommentAdded: (comment: PrComment) => void;
}

function InlineCommentForm({ taskId, path, line, onCancel, onCommentAdded }: InlineCommentFormProps) {
  const t = useTranslations("dashboard.prReview");
  const tc = useTranslations("common");
  const [body, setBody] = useState("");
  const [isPending, startTransition] = useTransition();

  function handleSubmit() {
    if (!body.trim()) return;
    startTransition(async () => {
      const comment = await addPrComment(taskId, path, body.trim(), line);
      if (comment) {
        onCommentAdded(comment);
        setBody("");
        onCancel();
      }
    });
  }

  return (
    <div className="bg-white/[0.02] border border-white/[0.06] rounded-lg p-3 my-1 mx-2">
      <textarea
        value={body}
        onChange={(e) => setBody(e.target.value)}
        placeholder={t("commentPlaceholder")}
        className="w-full rounded-lg bg-white/[0.03] border border-white/[0.06] px-3 py-2 text-xs text-gray-100 placeholder:text-gray-500 focus:outline-none focus:border-cyan-500/50 resize-none"
        rows={3}
        autoFocus
        onKeyDown={(e) => {
          if (e.key === "Enter" && (e.metaKey || e.ctrlKey)) {
            e.preventDefault();
            handleSubmit();
          }
        }}
      />
      <div className="flex items-center gap-2 mt-2">
        <Button
          size="sm"
          disabled={!body.trim() || isPending}
          loading={isPending}
          onClick={handleSubmit}
        >
          {t("comment")}
        </Button>
        <Button variant="ghost" size="sm" onClick={onCancel}>
          {tc("actions.cancel")}
        </Button>
        <span className="text-xs text-gray-400 ml-auto">
          {t("ctrlEnterHint")}
        </span>
      </div>
    </div>
  );
}

export { InlineCommentForm };
