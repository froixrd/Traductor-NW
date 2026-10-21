import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { viteSingleFile } from "vite-plugin-singlefile";

// "npm run build"      → interfaz de la app (src-tauri la mete dentro del .exe).
// "npm run build:demo" → un solo HTML con la demo, para ver la interfaz en el navegador.
export default defineConfig(({ mode }) => ({
  plugins: [react(), ...(mode === "demo" ? [viteSingleFile()] : [])],
  base: "./",
  clearScreen: false,
  server: { port: 1420, strictPort: true },
  build: {
    outDir: mode === "demo" ? "dist-demo" : "dist",
    target: "es2022",
    assetsInlineLimit: mode === "demo" ? 100_000_000 : 4096,
    chunkSizeWarningLimit: 2000,
  },
}));
