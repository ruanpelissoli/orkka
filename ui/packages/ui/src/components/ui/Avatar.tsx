import { cn, getInitials } from "@/lib/utils";

interface AvatarProps {
  src?: string | null;
  name: string;
  size?: "sm" | "md" | "lg";
  status?: "online" | "busy" | "offline";
  className?: string;
}

const sizeClasses = {
  sm: "h-7 w-7 text-xs",
  md: "h-9 w-9 text-xs",
  lg: "h-11 w-11 text-sm",
};

const statusSizeClasses = {
  sm: "h-2 w-2 border",
  md: "h-2.5 w-2.5 border-[1.5px]",
  lg: "h-3 w-3 border-2",
};

const statusColorClasses = {
  online: "bg-emerald-400 shadow-[0_0_6px_rgba(52,211,153,0.6)]",
  busy: "bg-amber-400 shadow-[0_0_6px_rgba(251,191,36,0.6)]",
  offline: "bg-gray-500",
};

function Avatar({ src, name, size = "md", status, className }: AvatarProps) {
  return (
    <div className={cn("relative shrink-0", className)}>
      {src ? (
        <img
          src={src}
          alt={name}
          className={cn(
            sizeClasses[size],
            "rounded-full object-cover",
            "ring-1 ring-white/10"
          )}
        />
      ) : (
        <div
          className={cn(
            sizeClasses[size],
            "flex items-center justify-center rounded-full",
            "bg-primary-800/60 text-primary-300 font-semibold",
            "ring-1 ring-white/10"
          )}
        >
          {getInitials(name)}
        </div>
      )}
      {status && (
        <span
          className={cn(
            "absolute bottom-0 right-0 rounded-full border-gray-900",
            statusSizeClasses[size],
            statusColorClasses[status]
          )}
        />
      )}
    </div>
  );
}

interface AvatarGroupProps {
  avatars: { src?: string | null; name: string }[];
  max?: number;
  size?: "sm" | "md" | "lg";
}

function AvatarGroup({ avatars, max = 4, size = "sm" }: AvatarGroupProps) {
  const visible = avatars.slice(0, max);
  const remaining = avatars.length - max;

  return (
    <div className="flex -space-x-2">
      {visible.map((avatar, i) => (
        <Avatar
          key={i}
          src={avatar.src}
          name={avatar.name}
          size={size}
          className="ring-2 ring-gray-900 transition-transform hover:z-10 hover:-translate-y-0.5"
        />
      ))}
      {remaining > 0 && (
        <div
          className={cn(
            sizeClasses[size],
            "flex items-center justify-center rounded-full",
            "bg-gray-800 text-gray-400 text-xs font-medium",
            "ring-2 ring-gray-900"
          )}
        >
          +{remaining}
        </div>
      )}
    </div>
  );
}

export { Avatar, AvatarGroup };
