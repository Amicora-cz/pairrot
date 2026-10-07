import fs from 'node:fs'
import { fileURLToPath } from 'node:url'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// The SPA calls relative /v1/... paths; Vite proxies them to the API, so the browser sees a
// single HTTPS origin and the API's Secure, HttpOnly, SameSite=Lax cookies work as in production.
const certFile = fileURLToPath(new URL('./.cert/localhost.pem', import.meta.url))
const keyFile = fileURLToPath(new URL('./.cert/localhost.key', import.meta.url))

export default defineConfig(({ command }) => {
  if (command === 'serve' && !fs.existsSync(certFile)) {
    throw new Error('Missing .cert/localhost.pem. Run "npm run cert" first (exports the ASP.NET Core dev certificate).')
  }

  return {
    plugins: [react()],
    server: {
      port: 5173,
      strictPort: true,
      https: command === 'serve' ? { cert: fs.readFileSync(certFile), key: fs.readFileSync(keyFile) } : undefined,
      proxy: {
        '/v1': {
          target: process.env.PAIRROT_API_URL ?? 'https://localhost:7017',
          // The API uses the local development certificate.
          secure: false,
          // Caddy routes by host name, so a remote target needs its own Host header.
          changeOrigin: true,
        },
      },
    },
  }
})
