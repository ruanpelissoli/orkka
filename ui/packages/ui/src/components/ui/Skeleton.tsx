import { cn } from "@/lib/utils";

interface SkeletonProps {
  className?: string;
  variant?: "text" | "circular" | "card";
}

function Skeleton({ className, variant = "text" }: SkeletonProps) {
  const baseClasses = {
    text: "h-4 w-full rounded-lg",
    circular: "h-10 w-10 rounded-full",
    card: "h-32 w-full rounded-2xl",
  };

  return (
    <div
      className={cn(
        "relative overflow-hidden",
        "bg-white/[0.04]",
        baseClasses[variant],
        className
      )}
    >
      <div className="absolute inset-0 shimmer" />
    </div>
  );
}

function SkeletonCard() {
  return (
    <div className="glass rounded-2xl p-5 space-y-4">
      <div className="flex items-center gap-3">
        <Skeleton variant="circular" className="h-8 w-8" />
        <Skeleton className="h-4 w-32" />
      </div>
      <Skeleton className="h-4 w-full" />
      <Skeleton className="h-4 w-3/4" />
      <div className="flex gap-2">
        <Skeleton className="h-6 w-16 rounded-full" />
        <Skeleton className="h-6 w-20 rounded-full" />
      </div>
    </div>
  );
}

export { Skeleton, SkeletonCard };
