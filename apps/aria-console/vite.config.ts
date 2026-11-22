import { defineConfig, loadEnv } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import { copilotManagedRuntime } from "@microsoft/cmr-apps-vite-plugin";

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), "");
  const isHostBundle = mode === "host";
  return {
    base: isHostBundle ? "/console/" : "./",
    plugins: isHostBundle
      ? [react(), tailwindcss()]
      : [react(), tailwindcss(), copilotManagedRuntime()],
    build: {
      chunkSizeWarningLimit: 600,
      rollupOptions: {
        onwarn(warning, warn) {
          if (
            warning.code === "INVALID_ANNOTATION" &&
            warning.id?.includes("node_modules/zod")
          ) {
            return;
          }
          warn(warning);
        },
        output: {
          manualChunks(id) {
            if (id.includes("node_modules/gsap")) return "gsap";
            if (
              id.includes("node_modules/react") ||
              id.includes("node_modules/scheduler")
            ) {
              return "react-vendor";
            }
            if (id.includes("node_modules/@microsoft/cmr-apps")) {
              return "cmr-sdk";
            }
          },
        },
      },
    },
    server: {
      host: "localhost",
      port: 3000,
      strictPort: true,
      proxy: {
        "/api": {
          target: env.VITE_RTBOT_URL || "http://localhost:5000",
          changeOrigin: true,
        },
      },
    },
  };
});
