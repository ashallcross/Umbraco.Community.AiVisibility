import { manifests as entrypoints } from "./entrypoints/manifest.js";
import { manifests as dashboardSettings } from "./manifests/dashboard-settings.manifest.js";
import { manifests as dashboardAiTraffic } from "./manifests/dashboard-ai-traffic.manifest.js";

// Job of the bundle is to collate all the manifests from different parts of the extension and load other manifests
// We load this bundle from umbraco-package.json
export const manifests: Array<UmbExtensionManifest> = [
  ...entrypoints,
  ...dashboardSettings,
  ...dashboardAiTraffic,
];
