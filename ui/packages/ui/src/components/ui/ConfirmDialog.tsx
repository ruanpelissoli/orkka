import { type ReactNode } from "react";
import { Modal } from "./Modal";
import { Button } from "./Button";

interface ConfirmDialogProps {
  open: boolean;
  title: string;
  /** Body of the dialog — a sentence explaining the consequence. */
  message: ReactNode;
  confirmLabel: string;
  cancelLabel: string;
  /** Renders the confirm button in the destructive style. */
  danger?: boolean;
  /** Disables both buttons while the confirmed action runs. */
  pending?: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}

/**
 * Styled replacement for window.confirm: a compact centered dialog with an explicit
 * cancel/confirm pair. Escape and backdrop click cancel — never confirm.
 */
function ConfirmDialog({
  open,
  title,
  message,
  confirmLabel,
  cancelLabel,
  danger = false,
  pending = false,
  onConfirm,
  onCancel,
}: ConfirmDialogProps) {
  return (
    <Modal open={open} onClose={onCancel} fullScreenMobile={false}>
      <div className="space-y-4 py-1">
        <div className="flex items-start gap-3">
          {danger && (
            <span className="mt-0.5 flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-red-500/10 text-red-400">
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                <path d="M12 9v4m0 4h.01M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z" strokeLinecap="round" strokeLinejoin="round" />
              </svg>
            </span>
          )}
          <div>
            <h2 className="font-display text-base font-semibold text-gray-100">{title}</h2>
            <div className="mt-1.5 text-sm text-gray-400">{message}</div>
          </div>
        </div>

        <div className="flex justify-end gap-3">
          <Button type="button" variant="ghost" size="sm" onClick={onCancel} disabled={pending}>
            {cancelLabel}
          </Button>
          <Button
            type="button"
            variant={danger ? "danger" : "primary"}
            size="sm"
            onClick={onConfirm}
            disabled={pending}
          >
            {confirmLabel}
          </Button>
        </div>
      </div>
    </Modal>
  );
}

export { ConfirmDialog };
