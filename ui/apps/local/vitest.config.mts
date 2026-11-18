import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";
import { appAlias } from "./vite.aliases";

export default defineConfig({
  plugins: [appAlias(), react()],
  test: {
    environment: "happy-dom",
    setupFiles: ["./__tests__/setup.ts"],
    include: ["__tests__/**/*.test.{ts,tsx}"],
    globals: true,
    // Vitest's 5s default is not a realistic budget for this suite. Many tests render a whole
    // page section, and several pull their component in with `await import()` inside the test
    // body — so the module graph (React, framer-motion, use-intl, the shared UI package) is
    // imported against the TEST's clock, not collection's. Suite-wide that import cost runs to
    // several seconds per file, and on a busy machine the fastest workers finish while the
    // unlucky ones time out. The result was a suite that failed on a rotating cast of files
    // with no assertion ever being wrong — the worst kind of red, because it trains people to
    // ignore it. These numbers bound a genuinely hung test without failing a merely slow one.
    testTimeout: 30_000,
    hookTimeout: 30_000,
  },
});
