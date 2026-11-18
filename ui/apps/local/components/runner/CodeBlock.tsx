import { useState } from "react";
import { useTranslations } from "use-intl";
import { cn } from "@/lib/utils";

/**
 * Copyable one-line command block. Long commands scroll horizontally inside
 * the block so the page never overflows on narrow (~380px) screens.
 */
export function CodeBlock({ code, className }: { code: string; className?: string }) {
  const t = useTranslations("dashboard.runnerPage");
  const [copied, setCopied] = useState(false);

  async function handleCopy() {
    try {
      await navigator.clipboard.writeText(code);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch {
      // Clipboard unavailable — the command stays visible for manual copy
    }
  }

  return (
    <div className={cn("flex items-stretch gap-2", className)}>
      <pre className="min-w-0 flex-1 overflow-x-auto rounded-xl border border-white/[0.06] bg-black/40 px-3 py-2.5 font-mono text-xs leading-relaxed text-gray-200 scrollbar-none">
        <code>{code}</code>
      </pre>
      <button
        type="button"
        onClick={handleCopy}
        className={cn(
          "shrink-0 self-center rounded-lg border border-white/[0.06] bg-white/[0.03] px-2.5 py-1.5 text-xs font-medium transition-colors",
          copied
            ? "text-emerald-400"
            : "text-gray-400 hover:bg-white/[0.06] hover:text-gray-200"
        )}
      >
        {copied ? t("copied") : t("copy")}
      </button>
    </div>
  );
}
