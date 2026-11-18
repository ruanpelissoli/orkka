import { Badge, GlassCard, PriorityBadge } from "@/components/ui";
import { LANE_MAP } from "@/lib/constants";
import { useLabels } from "@/hooks/use-labels";
import type { Task } from "@/lib/types";

interface TaskDetailProps {
  task: Task;
}

function TaskDetail({ task }: TaskDetailProps) {
  const labels = useLabels();
  const lane = LANE_MAP[task.currentLane];
  return (
    <GlassCard padding="lg" accentColor={lane?.color}>
      <div className="space-y-4">
        {/* Title */}
        <h1 className="font-display text-xl font-bold text-white md:text-2xl">
          {task.title}
        </h1>

        {/* Meta pills */}
        <div className="flex flex-wrap gap-2">
          <Badge
            dot
            dotColor={lane?.color}
            glow
            style={{
              backgroundColor: `${lane?.color}10`,
              color: lane?.color,
              borderColor: `${lane?.color}20`,
            }}
          >
            {labels.lane(task.currentLane)}
          </Badge>
          {task.priority && (
            <PriorityBadge priority={task.priority} size="md" />
          )}
          {task.branchName && (
            <Badge variant="default">
              <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                <path d="M6 3v12M18 9a3 3 0 100-6 3 3 0 000 6zM6 21a3 3 0 100-6 3 3 0 000 6zM18 9a9 9 0 01-9 9" strokeLinecap="round" strokeLinejoin="round" />
              </svg>
              <span className="font-mono">{task.branchName}</span>
            </Badge>
          )}
        </div>

        {/* Description */}
        {task.description && (
          <div className="text-sm leading-relaxed text-gray-400 whitespace-pre-wrap">
            {task.description}
          </div>
        )}

        {/* Tags */}
        {(task.tags?.length ?? 0) > 0 && (
          <div className="flex flex-wrap gap-1.5">
            {task.tags!.map((tag) => (
              <Badge key={tag} size="sm">{tag}</Badge>
            ))}
          </div>
        )}
      </div>
    </GlassCard>
  );
}

export { TaskDetail };
