// Story 5.2 — AI Traffic dashboard manifest. Sibling to Story 3.2's
// dashboard-settings.manifest.ts; both surface under Umb.Section.Settings.
// Weight 90 keeps Story 3.2's "LlmsTxt" Settings tile (weight 100) on top.
export const manifests: Array<UmbExtensionManifest> = [
  {
    type: "dashboard",
    alias: "Llms.Dashboard.AiTraffic",
    name: "LlmsTxt AI Traffic Dashboard",
    element: () => import("../elements/llms-ai-traffic-dashboard.element.js"),
    weight: 90,
    meta: {
      label: "AI Traffic",
      pathname: "llmstxt-ai-traffic",
    },
    conditions: [
      {
        alias: "Umb.Condition.SectionAlias",
        match: "Umb.Section.Settings",
      },
    ],
  },
];
