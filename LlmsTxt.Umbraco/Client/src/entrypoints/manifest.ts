export const manifests: Array<UmbExtensionManifest> = [
  {
    name: "Llms Txt Umbraco Entrypoint",
    alias: "LlmsTxt.Umbraco.Entrypoint",
    type: "backofficeEntryPoint",
    js: () => import("./entrypoint.js"),
  },
];
