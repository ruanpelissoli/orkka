import { useLabels } from "@/hooks/use-labels";
import type { PriorityKey } from "@/lib/constants";
import { Badge } from "./Badge";
import type { VariantProps } from "class-variance-authority";
import type { badgeVariants } from "./Badge";

const PRIORITY_CONFIG: Record<
  PriorityKey,
  { label: string; variant: NonNullable<VariantProps<typeof badgeVariants>["variant"]> }
> = {
  low: { label: "Low", variant: "default" },
  medium: { label: "Medium", variant: "info" },
  high: { label: "High", variant: "warning" },
  critical: { label: "Critical", variant: "error" },
};

interface PriorityBadgeProps {
  priority: string;
  size?: "sm" | "md" | "lg";
  className?: string;
}

function PriorityBadge({ priority, size = "sm", className }: PriorityBadgeProps) {
  const labels = useLabels();
  const key = priority.toLowerCase() as PriorityKey;
  const config = PRIORITY_CONFIG[key];
  if (!config) return null;

  return (
    <Badge variant={config.variant} size={size} className={className}>
      {labels.priority(key)}
    </Badge>
  );
}

export { PriorityBadge, PRIORITY_CONFIG };
export type { PriorityBadgeProps };
