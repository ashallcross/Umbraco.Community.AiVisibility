export const manifests: Array<UmbExtensionManifest> = [
  {
    name: "Umbraco Community AI Visibility Entrypoint",
    alias: "Umbraco.Community.AiVisibility.Entrypoint",
    type: "backofficeEntryPoint",
    js: () => import("./entrypoint.js"),
  },
];
