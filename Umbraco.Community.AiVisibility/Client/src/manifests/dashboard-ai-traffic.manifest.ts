// Story 5.2 — AI Traffic dashboard manifest. Sibling to Story 3.2's
// dashboard-settings.manifest.ts; both surface under Umb.Section.Settings.
// Weight 90 keeps Story 3.2's "AI Visibility" Settings tile (weight 100) on top.
export const manifests: Array<UmbExtensionManifest> = [
  {
    type: "dashboard",
    alias: "AiVisibility.Dashboard.AiTraffic",
    name: "AI Visibility AI Traffic Dashboard",
    element: () => import("../elements/aiv-ai-traffic-dashboard.element.js"),
    weight: 90,
    meta: {
      label: "AI Traffic",
      pathname: "aivisibility-ai-traffic",
    },
    conditions: [
      {
        alias: "Umb.Condition.SectionAlias",
        match: "Umb.Section.Settings",
      },
    ],
  },
];
