import { useState, useRef, useEffect, type ReactNode } from "react";
import { motion, AnimatePresence } from "framer-motion";
import { cn } from "@/lib/utils";

interface DropdownItem {
  key: string;
  label: string;
  icon?: ReactNode;
  danger?: boolean;
  disabled?: boolean;
}

interface DropdownProps {
  trigger: ReactNode;
  items: DropdownItem[];
  onSelect: (key: string) => void;
  align?: "left" | "right";
  className?: string;
}

function Dropdown({
  trigger,
  items,
  onSelect,
  align = "left",
  className,
}: DropdownProps) {
  const [open, setOpen] = useState(false);
  const [activeIndex, setActiveIndex] = useState(-1);
  const containerRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    function handleClickOutside(e: MouseEvent) {
      if (containerRef.current && !containerRef.current.contains(e.target as Node)) {
        setOpen(false);
      }
    }
    if (open) {
      document.addEventListener("mousedown", handleClickOutside);
      return () => document.removeEventListener("mousedown", handleClickOutside);
    }
  }, [open]);

  useEffect(() => {
    function handleKeyDown(e: KeyboardEvent) {
      if (!open) return;
      if (e.key === "Escape") setOpen(false);
      if (e.key === "ArrowDown") {
        e.preventDefault();
        setActiveIndex((i) => (i + 1) % items.length);
      }
      if (e.key === "ArrowUp") {
        e.preventDefault();
        setActiveIndex((i) => (i - 1 + items.length) % items.length);
      }
      if (e.key === "Enter" && activeIndex >= 0) {
        const item = items[activeIndex];
        if (!item.disabled) {
          onSelect(item.key);
          setOpen(false);
        }
      }
    }
    window.addEventListener("keydown", handleKeyDown);
    return () => window.removeEventListener("keydown", handleKeyDown);
  }, [open, activeIndex, items, onSelect]);

  return (
    <div ref={containerRef} className={cn("relative inline-block", className)}>
      <div
        onClick={() => {
          setOpen(!open);
          setActiveIndex(-1);
        }}
      >
        {trigger}
      </div>

      <AnimatePresence>
        {open && (
          <motion.div
            initial={{ opacity: 0, y: -4, scale: 0.97 }}
            animate={{ opacity: 1, y: 0, scale: 1 }}
            exit={{ opacity: 0, y: -4, scale: 0.97 }}
            transition={{ duration: 0.15, ease: "easeOut" }}
            className={cn(
              "absolute z-50 mt-1.5 min-w-full overflow-hidden rounded-xl",
              "bg-gray-900/95 backdrop-blur-2xl",
              "border border-white/[0.08]",
              "shadow-[0_8px_40px_rgba(0,0,0,0.5)]",
              align === "right" ? "right-0" : "left-0"
            )}
          >
            {/* Top highlight */}
            <div className="absolute inset-x-0 top-0 h-px bg-gradient-to-r from-transparent via-white/[0.08] to-transparent" />

            <div className="p-1">
              {items.map((item, index) => (
                <button
                  key={item.key}
                  disabled={item.disabled}
                  onMouseEnter={() => setActiveIndex(index)}
                  onClick={() => {
                    if (!item.disabled) {
                      onSelect(item.key);
                      setOpen(false);
                    }
                  }}
                  className={cn(
                    "flex w-full items-center gap-2.5 rounded-lg px-3 py-2 text-sm whitespace-nowrap",
                    "transition-colors duration-100",
                    item.disabled && "pointer-events-none opacity-40",
                    item.danger ? "text-red-400" : "text-gray-300",
                    activeIndex === index && (
                      item.danger
                        ? "bg-red-500/10 text-red-300"
                        : "bg-white/[0.06] text-gray-100"
                    ),
                  )}
                >
                  {item.icon && (
                    <span className="shrink-0 text-current opacity-60">
                      {item.icon}
                    </span>
                  )}
                  {item.label}
                </button>
              ))}
            </div>
          </motion.div>
        )}
      </AnimatePresence>
    </div>
  );
}

export { Dropdown };
export type { DropdownItem, DropdownProps };
