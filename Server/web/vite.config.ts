import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

// In development the API runs on its own port; the browser still sees one origin, which is
// what the session cookie and the CSRF header rule both assume (DESIGN.md §12). In production
// the API serves the built files itself, so there is nothing to proxy.
const api = process.env.KCLOCK_API ?? 'http://localhost:8080';

export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/api': api,
      '/auth': api,
    },
  },
  test: {
    environment: 'node',
  },
});
