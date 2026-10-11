import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// The API runs on :5290 (src/ClinicReport.Api). Proxying keeps the browser on one origin, so the
// auth cookie stays SameSite=Strict and the API needs no CORS policy.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5291,
    strictPort: true,
    proxy: {
      // The browser's Host is kept, so a sign-in through ClinicHub returns to :5291 (registered there) and lands on the page.
      '/api': { target: 'http://localhost:5290', changeOrigin: false },
      '/hubs': { target: 'http://localhost:5290', ws: true },
      '/signin-oidc': { target: 'http://localhost:5290', changeOrigin: false },
      '/signout-callback-oidc': { target: 'http://localhost:5290', changeOrigin: false },
    },
  },
})
