import { useEffect, useRef, type ReactNode } from "react";
import { createPortal } from "react-dom";
import { motion, AnimatePresence } from "framer-motion";
import { cn } from "@/lib/utils";

interface ModalProps {
  open: boolean;
  onClose: () => void;
  children: ReactNode;
  title?: string;
  className?: string;
  /** Overrides the content wrapper (default: padded, scrolls vertically). Multi-pane
   * bodies pass their own overflow handling here — e.g. "overflow-hidden p-0" — so each
   * pane scrolls independently instead of the whole body. */
  bodyClassName?: string;
  fullScreenMobile?: boolean;
}

function Modal({
  open,
  onClose,
  children,
  title,
  className,
  bodyClassName,
  fullScreenMobile = true,
}: ModalProps) {
  const overlayRef = useRef<HTMLDivElement>(null);

  // Portal target. `position: fixed` is relative to the nearest TRANSFORMED ancestor, not
  // the viewport — and this modal is routinely opened from inside other modals/cards whose
  // panels animate with framer-motion transforms and backdrop-blur. Rendered in place, a
  // "full screen" modal ends up clipped inside whatever panel opened it; the portal to
  // document.body is what makes fixed mean the viewport again. (The app is client-rendered,
  // so document.body always exists here.)

  useEffect(() => {
    if (open) {
      document.body.style.overflow = "hidden";
    } else {
      document.body.style.overflow = "";
    }
    return () => {
      document.body.style.overflow = "";
    };
  }, [open]);

  useEffect(() => {
    function handleEscape(e: KeyboardEvent) {
      if (e.key === "Escape") onClose();
    }
    if (open) {
      window.addEventListener("keydown", handleEscape);
      return () => window.removeEventListener("keydown", handleEscape);
    }
  }, [open, onClose]);

  return createPortal(
    <AnimatePresence>
      {open && (
        <div className="fixed inset-0 z-50 flex items-end md:items-center md:justify-center">
          {/* Backdrop */}
          <motion.div
            ref={overlayRef}
            initial={{ opacity: 0 }}
            animate={{ opacity: 1 }}
            exit={{ opacity: 0 }}
            transition={{ duration: 0.2 }}
            className="absolute inset-0 bg-black/60 backdrop-blur-sm"
            onClick={onClose}
          />

          {/* Panel */}
          <motion.div
            initial={{ opacity: 0, y: fullScreenMobile ? 100 : 20, scale: fullScreenMobile ? 1 : 0.95 }}
            animate={{ opacity: 1, y: 0, scale: 1 }}
            exit={{ opacity: 0, y: fullScreenMobile ? 100 : 20, scale: fullScreenMobile ? 1 : 0.95 }}
            transition={{ type: "spring", damping: 30, stiffness: 400 }}
            className={cn(
              "relative z-10 w-full overflow-hidden flex flex-col",
              "bg-gray-900/90 backdrop-blur-2xl",
              "border border-white/[0.08]",
              "shadow-[0_0_60px_rgba(0,0,0,0.5)]",
              fullScreenMobile
                ? "max-h-[90vh] rounded-t-2xl md:max-w-lg md:rounded-2xl"
                : "max-w-lg rounded-2xl mx-4",
              className
            )}
          >
            {/* Top highlight */}
            <div className="absolute inset-x-0 top-0 h-px bg-gradient-to-r from-transparent via-white/[0.1] to-transparent" />

            {/* Mobile drag handle */}
            {fullScreenMobile && (
              <div className="flex justify-center py-2 md:hidden">
                <div className="h-1 w-8 rounded-full bg-white/20" />
              </div>
            )}

            {/* Header */}
            {title && (
              <div className="flex items-center justify-between border-b border-white/[0.06] px-6 py-4">
                <h2 className="font-display text-lg font-semibold text-gray-100">
                  {title}
                </h2>
                <button
                  onClick={onClose}
                  className="rounded-lg p-1.5 text-gray-400 transition-colors hover:bg-white/[0.06] hover:text-gray-200"
                >
                  <svg width="16" height="16" viewBox="0 0 16 16" fill="none">
                    <path
                      d="M12 4L4 12M4 4l8 8"
                      stroke="currentColor"
                      strokeWidth="1.5"
                      strokeLinecap="round"
                    />
                  </svg>
                </button>
              </div>
            )}

            {/* Content */}
            <div
              className={cn(
                "flex-1 min-h-0 overflow-y-auto overflow-x-hidden overscroll-contain px-6 py-4",
                bodyClassName
              )}
            >
              {children}
            </div>
          </motion.div>
        </div>
      )}
    </AnimatePresence>,
    document.body
  );
}

export { Modal };
