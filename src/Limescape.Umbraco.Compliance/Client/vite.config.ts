import { defineConfig } from "vite";

export default defineConfig({
  build: {
    lib: {
      // The bundle registers every manifest of the extension; umbraco-package.json loads it.
      entry: "src/bundle.manifests.ts",
      formats: ["es"],
      fileName: "limescape-umbraco-compliance",
    },
    // Served at /App_Plugins/Limescape.Umbraco.Compliance (StaticWebAssetBasePath is "/").
    outDir: "../wwwroot/App_Plugins/Limescape.Umbraco.Compliance",
    emptyOutDir: true,
    sourcemap: true,
    rollupOptions: {
      external: [/^@umbraco/],
    },
  },
});
