import { useState, type ReactNode } from "react";
import { motion, AnimatePresence } from "framer-motion";
import { cn } from "@/lib/utils";

interface TooltipProps {
  content: string;
  children: ReactNode;
  side?: "top" | "bottom";
  className?: string;
}

function Tooltip({ content, children, side = "top", className }: TooltipProps) {
  const [show, setShow] = useState(false);

  return (
    <div
      className="relative inline-flex"
      onMouseEnter={() => setShow(true)}
      onMouseLeave={() => setShow(false)}
    >
      {children}
      <AnimatePresence>
        {show && (
          <motion.div
            initial={{ opacity: 0, scale: 0.95, y: side === "top" ? 4 : -4 }}
            animate={{ opacity: 1, scale: 1, y: 0 }}
            exit={{ opacity: 0, scale: 0.95 }}
            transition={{ duration: 0.12 }}
            className={cn(
              "absolute z-50 whitespace-nowrap rounded-lg px-2.5 py-1.5",
              "bg-gray-800/95 backdrop-blur-lg text-xs text-gray-200",
              "border border-white/[0.06]",
              "shadow-[0_4px_20px_rgba(0,0,0,0.4)]",
              "pointer-events-none",
              side === "top" && "bottom-full mb-2 left-1/2 -translate-x-1/2",
              side === "bottom" && "top-full mt-2 left-1/2 -translate-x-1/2",
              className
            )}
          >
            {content}
          </motion.div>
        )}
      </AnimatePresence>
    </div>
  );
}

export { Tooltip };
