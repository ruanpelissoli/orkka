import { defineConfig, devices } from "@playwright/test";
import { cfg, paths } from "./config/index.js";
import fs from "node:fs";

// Offline runs (no E2E_BASE_URL) need no session; environment-bound specs skip themselves.
const hasStorageState = cfg.configured && fs.existsSync(paths.storageState);

export default defineConfig({
  testDir: "./tests",
  fullyParallel: false,
  workers: 1,
  retries: 0,
  forbidOnly: false,
  timeout: 5 * 60_000,
  expect: { timeout: 15_000 },
  globalSetup: "./tests/global.setup.ts",
  globalTeardown: "./tests/global.teardown.ts",
  outputDir: "runs/current/artifacts",
  reporter: [["list"], ["html", { open: "never", outputFolder: ".runs/html" }], ["./src/reporter/run-reporter.ts"]],
  use: {
    ...devices["Desktop Chrome"],
    baseURL: cfg.webUrl,
    storageState: hasStorageState ? paths.storageState : undefined,
    locale: "en-US",
    timezoneId: "America/Sao_Paulo",
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    video: "off",
    actionTimeout: 15_000,
    navigationTimeout: 45_000,
    viewport: { width: 1366, height: 900 },
  },
  projects: [{ name: "chromium" }],
});
