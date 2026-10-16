import { defineConfig } from '@playwright/test';

// Runs against `tools/run_local.sh` (SWA CLI emulator on :4280 + in-memory SAMPLE API).
export default defineConfig({
  testDir: 'e2e',
  timeout: 60_000,
  retries: 0,
  reporter: [['list']],
  use: {
    baseURL: process.env.E2E_BASE_URL ?? 'http://localhost:4280',
    launchOptions: { executablePath: process.env.PW_CHROMIUM ?? '/opt/pw-browsers/chromium' },
  },
});
