import { formatRelativeTime } from "@/lib/utils";
import type { PrComment } from "@/lib/types";

interface PrCommentThreadProps {
  comments: PrComment[];
}

function PrCommentThread({ comments }: PrCommentThreadProps) {
  if (comments.length === 0) return null;

  return (
    <div className="border-l-2 border-cyan-500/20 ml-4 pl-3 py-2 space-y-3">
      {comments.map((comment) => (
        <div key={comment.id} className="space-y-1">
          <div className="flex items-center gap-2">
            {comment.authorAvatarUrl ? (
              <img
                src={comment.authorAvatarUrl}
                alt={comment.authorLogin}
                className="w-5 h-5 rounded-full"
              />
            ) : (
              <div className="w-5 h-5 rounded-full bg-white/[0.06] flex items-center justify-center text-xs font-semibold text-gray-400">
                {comment.authorLogin[0]?.toUpperCase()}
              </div>
            )}
            <span className="text-xs font-semibold text-gray-300">
              {comment.authorLogin}
            </span>
            <span className="text-xs text-gray-400">
              {formatRelativeTime(comment.createdAt)}
            </span>
          </div>
          <p className="text-xs text-gray-400 leading-relaxed whitespace-pre-wrap">
            {comment.body}
          </p>
        </div>
      ))}
    </div>
  );
}

export { PrCommentThread };
