import { useEffect, useRef } from "react";
import { Terminal } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import "@xterm/xterm/css/xterm.css";

function bytesToBase64(bytes: Uint8Array): string {
  let binary = "";
  for (let i = 0; i < bytes.length; i++) binary += String.fromCharCode(bytes[i]);
  return btoa(binary);
}

function base64ToBytes(data: string): Uint8Array {
  const binary = atob(data);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}

interface RunLogViewProps {
  runId: string;
  /** Everything printed before this view existed, replayed on mount. */
  scrollback: string | null;
  /** True while the process is alive — a finished run's pane is read-only. */
  interactive: boolean;
  active: boolean;
  subscribeOutput: (runId: string, handler: (base64: string) => void) => () => void;
  onInput: (runId: string, dataBase64: string) => void;
  onResize: (runId: string, cols: number, rows: number) => void;
}

/**
 * One app's output, rendered as a real terminal rather than a log tail.
 *
 * A dev server's output is VT, not text: colour, progress bars, and cleared lines. Rendering
 * it in a <pre> turns a Vite banner into a wall of escape codes. Keystrokes go back the same
 * way — Expo reloads on `r`, Vite opens the browser on `o` — so the pane accepts input while
 * the process lives.
 */
export function RunLogView({
  runId,
  scrollback,
  interactive,
  active,
  subscribeOutput,
  onInput,
  onResize,
}: RunLogViewProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const termRef = useRef<Terminal | null>(null);
  const fitRef = useRef<FitAddon | null>(null);
  // Latest callbacks without retriggering the boot effect — a rerender must never rebuild
  // the terminal and lose its scrollback.
  const callbacksRef = useRef({ subscribeOutput, onInput, onResize, interactive });
  useEffect(() => {
    callbacksRef.current = { subscribeOutput, onInput, onResize, interactive };
  });

  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    let disposed = false;
    const term = new Terminal({
      cursorBlink: false,
      fontSize: 12,
      fontFamily:
        'ui-monospace, SFMono-Regular, Menlo, Monaco, "Cascadia Mono", "Segoe UI Mono", monospace',
      scrollback: 8000,
      theme: {
        background: "#0a0a12",
        foreground: "#d1d5db",
        cursor: "#34d399",
        cursorAccent: "#0a0a12",
        selectionBackground: "rgba(52, 211, 153, 0.3)",
      },
    });
    const fit = new FitAddon();
    term.loadAddon(fit);
    termRef.current = term;
    fitRef.current = fit;

    const dataHandler = term.onData((data) => {
      if (!callbacksRef.current.interactive) return;
      const bytes = new TextEncoder().encode(data);
      callbacksRef.current.onInput(runId, bytesToBase64(bytes));
    });

    const unsubscribe = callbacksRef.current.subscribeOutput(runId, (base64) => {
      if (!disposed) term.write(base64ToBytes(base64));
    });

    let resizeTimer: ReturnType<typeof setTimeout> | null = null;
    const observer = new ResizeObserver(() => {
      // A zero-size pass (mid-transition) would collapse the grid to 2 cols.
      if (container.clientWidth === 0 || container.clientHeight === 0) return;
      if (resizeTimer) clearTimeout(resizeTimer);
      resizeTimer = setTimeout(() => {
        fit.fit();
        if (callbacksRef.current.interactive)
          callbacksRef.current.onResize(runId, term.cols, term.rows);
      }, 120);
    });
    observer.observe(container);

    // xterm's Viewport schedules a bare setTimeout at open(); deferring by one task means a
    // mount React throws away in the same tick never touches the DOM at all.
    const openTimer = setTimeout(() => {
      if (disposed) return;
      term.open(container);
      fit.fit();
      if (scrollback) term.write(base64ToBytes(scrollback));
    }, 0);

    return () => {
      disposed = true;
      clearTimeout(openTimer);
      if (resizeTimer) clearTimeout(resizeTimer);
      observer.disconnect();
      unsubscribe();
      dataHandler.dispose();
      term.dispose();
      termRef.current = null;
      fitRef.current = null;
    };
    // scrollback is the mount-time replay only: re-running on every snapshot would print the
    // whole log again on top of itself.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [runId]);

  // Coming back from hidden: the pane kept its size while hidden, but a viewport resize
  // meanwhile won't have refit.
  useEffect(() => {
    if (!active) return;
    const timer = setTimeout(() => fitRef.current?.fit(), 320);
    return () => clearTimeout(timer);
  }, [active]);

  return <div ref={containerRef} className="h-full w-full" />;
}
