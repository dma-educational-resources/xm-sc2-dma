import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

// The SC2 radar serves this build itself (src/Web/WebRadarServer.cs serves resources/wwwroot, copied next to the exe).
// `npm run dev` proxies /api to a running radar's web server: set SC2_RADAR to its address, e.g.
// SC2_RADAR=http://localhost:54321 npm run dev
export default defineConfig({
  plugins: [react(), tailwindcss()],
  base: "./",
  build: {
    outDir: "../resources/wwwroot",
    emptyOutDir: true,
  },
  server: {
    proxy: { "/api": process.env.SC2_RADAR ?? "http://localhost:8080" },
  },
});
