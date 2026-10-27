/// <reference types="vitest/config" />
import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';

// The dev server port comes from VITE_PORT (or `--port`); the API origin used by
// the dev proxy comes from VITE_API_PROXY_TARGET (falling back to VITE_API_BASE_URL). The browser
// itself always calls relative /api URLs in development. Nothing is hard-coded.
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, '.', 'VITE_');
  const apiTarget = env.VITE_API_PROXY_TARGET || env.VITE_API_BASE_URL;
  return {
    plugins: [react()],
    server: {
      port: env.VITE_PORT ? Number(env.VITE_PORT) : undefined,
      proxy: apiTarget
        ? { '/api': { target: apiTarget, changeOrigin: true } }
        : undefined,
    },
    test: {
      environment: 'jsdom',
      globals: true,
      setupFiles: ['./src/test-setup.ts'],
    },
  };
});
