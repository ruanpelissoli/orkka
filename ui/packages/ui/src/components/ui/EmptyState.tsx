import { type ReactNode } from "react";
import { cn } from "@/lib/utils";
import { OrcaEmpty } from "@/components/animations";

interface EmptyStateProps {
  icon?: ReactNode;
  title: string;
  description?: string;
  action?: ReactNode;
  className?: string;
}

function EmptyState({ icon, title, description, action, className }: EmptyStateProps) {
  return (
    <div className={cn("flex flex-col items-center justify-center py-16 px-4 text-center", className)}>
      <div className="relative mb-6">
        {icon ? (
          <>
            <div className="absolute -inset-8 animate-pulse rounded-full bg-primary-500/5" />
            <div className="absolute -inset-4 rounded-full bg-primary-500/5" />
            <div className="relative flex h-16 w-16 items-center justify-center rounded-2xl bg-white/[0.04] text-gray-400">
              {icon}
            </div>
          </>
        ) : (
          <OrcaEmpty size="sm" />
        )}
      </div>
      <h3 className="font-display text-lg font-semibold text-gray-200">{title}</h3>
      {description && (
        <p className="mt-2 max-w-sm text-sm text-gray-400">{description}</p>
      )}
      {action && <div className="mt-5">{action}</div>}
    </div>
  );
}

export { EmptyState };
