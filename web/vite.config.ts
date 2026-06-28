import { defineConfig } from 'vite'
import { svelte } from '@sveltejs/vite-plugin-svelte'

// Single-origin model:
//  - dev:  this Vite dev server serves the browser (native HMR) and proxies
//          /hub + /api to the ASP.NET server. Under `aspire run`, the server URL
//          is injected as VITE_SERVER_URL; standalone falls back to the launch URL.
//  - prod: `vite build` emits into Lantern.Server/wwwroot, served by the server,
//          and the client uses relative /hub + /api (same origin).
// https://vite.dev/config/
const serverUrl = process.env.VITE_SERVER_URL ?? 'http://localhost:5234'

export default defineConfig({
  plugins: [svelte()],
  server: {
    proxy: {
      '/hub': { target: serverUrl, ws: true, changeOrigin: true },
      '/api': { target: serverUrl, changeOrigin: true },
    },
  },
  build: {
    outDir: '../src/Lantern.Server/wwwroot',
    emptyOutDir: true,
  },
})
