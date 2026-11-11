import react from '@vitejs/plugin-react'
import { defineConfig } from 'vitest/config'

const role = process.env.VITE_SITE_ROLE ?? 'public'
if (!['public', 'admin'].includes(role)) throw new Error('VITE_SITE_ROLE must be public or admin. Questions use the Blazor Server submission app.')
if (role === 'admin' && process.env.VITE_API_BASE_URL) throw new Error('The admin app must use its same-origin API.')

// https://vite.dev/config/
export default defineConfig({
  base: process.env.VITE_BASE_PATH ?? '/',
  define: { 'import.meta.env.VITE_SITE_ROLE': JSON.stringify(role) },
  plugins: [react(), {
    name: 'deployment-role',
    transformIndexHtml: {
      order: 'pre',
      handler: html => html.replace('</head>', `<meta name="eventwall-role" content="${role}"></head>`),
    },
  }],
  server: {
    host: '127.0.0.1',
    strictPort: true,
    proxy: {
      '/api': {
        target: process.env.API_PROXY_TARGET ?? (role === 'admin' ? 'http://127.0.0.1:5081' : 'http://127.0.0.1:5080'),
        changeOrigin: true,
      },
    },
  },
  test: {
    environment: 'jsdom',
  },
})
