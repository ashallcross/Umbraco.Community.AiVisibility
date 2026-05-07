import { defineConfig } from "vite";

export default defineConfig({
  build: {
    lib: {
      entry: "src/bundle.manifests.ts", // Bundle registers one or more manifests
      formats: ["es"],
      fileName: "umbraco-community-aivisibility",
    },
    outDir: "../wwwroot/App_Plugins/UmbracoCommunityAiVisibility", // your web component will be saved in this location
    emptyOutDir: true,
    // Source maps disabled in the committed/shipped bundle (Story 6.0b AC1):
    // adopters can't navigate to original .ts because the source isn't shipped,
    // and shipping ~78 KB of .map files in every .nupkg is dead weight.
    // For a local debugging session, set `VITE_INCLUDE_SOURCEMAP=true` in the
    // environment instead of editing this flag — that way prod-default is
    // enforced by absence-of-env-var rather than maintainer discipline.
    sourcemap: process.env.VITE_INCLUDE_SOURCEMAP === "true",
    rollupOptions: {
      external: [/^@umbraco/],
    },
  },
});
