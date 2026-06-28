import { defineConfig } from 'vite'
import { svelte } from '@sveltejs/vite-plugin-svelte'

// Single-origin hosting:
//  - prod: `vite build` emits into Lantern.Server/wwwroot, served by the server.
//  - dev:  the server (YARP) reverse-proxies to this dev server; since dev is on
//          localhost, the HMR socket connects straight to the Vite port.
// https://vite.dev/config/
export default defineConfig({
  plugins: [svelte()],
  server: {
    port: 5173,
    strictPort: true,
    hmr: { clientPort: 5173 },
  },
  build: {
    outDir: '../src/Lantern.Server/wwwroot',
    emptyOutDir: true,
  },
})
