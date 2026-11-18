import { AnimatePresence, motion } from "framer-motion";
import { collapse } from "@/lib/motion";

/**
 * Animates content expanding and collapsing in place.
 *
 * The app has several of these — diff files, plan sections, execution history
 * rows — and each was a bare `{open && <div>…}`, so the content below jumped by
 * however tall the section happened to be. That is the single most jarring kind
 * of instant change, because it moves things the viewer was already reading.
 *
 * The height animation is on the wrapper and the spacing belongs to `children`,
 * so the measured `auto` height includes that spacing and the layout settles
 * once rather than twice. Do not put a margin on this component.
 */
export function Collapsible({
  open,
  children,
  className,
}: {
  open: boolean;
  children: React.ReactNode;
  className?: string;
}) {
  return (
    <AnimatePresence initial={false}>
      {open && (
        <motion.div
          variants={collapse}
          initial="hidden"
          animate="visible"
          exit="hidden"
          className={className ? `overflow-hidden ${className}` : "overflow-hidden"}
        >
          {children}
        </motion.div>
      )}
    </AnimatePresence>
  );
}
