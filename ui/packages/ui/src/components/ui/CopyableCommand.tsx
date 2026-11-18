import { useState } from "react";
import { useTranslations } from "use-intl";
import { cn } from "@/lib/utils";

/**
 * A shell command the user is meant to run, with a copy button. Long commands scroll
 * horizontally inside the block so the page never overflows on narrow screens.
 *
 * Shared by both editions — the remote app has its own `CodeBlock` for the runner-setup
 * page, but that one is scoped to that page's translations and lives outside packages/ui.
 */
export function CopyableCommand({
  command,
  className,
}: {
  command: string;
  className?: string;
}) {
  const t = useTranslations("common.actions");
  const [copied, setCopied] = useState(false);

  async function handleCopy() {
    try {
      await navigator.clipboard.writeText(command);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch {
      // Clipboard unavailable (insecure origin, denied permission) — the command stays
      // visible and selectable, which is the whole point of rendering it.
    }
  }

  return (
    <div className={cn("flex items-stretch gap-2", className)}>
      <pre className="min-w-0 flex-1 overflow-x-auto rounded-lg border border-white/[0.06] bg-black/40 px-3 py-2 font-mono text-xs leading-relaxed text-gray-200 scrollbar-none">
        <code>{command}</code>
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
