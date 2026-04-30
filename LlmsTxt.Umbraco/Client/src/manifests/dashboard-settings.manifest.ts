// Story 3.2 — Backoffice Settings dashboard manifest. Follows Spike 0.B locked
// decision #4: alias `Llms.Dashboard.Settings`, custom element tag
// `llms-settings-dashboard`, conditioned on the Settings section.
export const manifests: Array<UmbExtensionManifest> = [
  {
    type: "dashboard",
    alias: "Llms.Dashboard.Settings",
    name: "LlmsTxt Settings Dashboard",
    element: () => import("../elements/llms-settings-dashboard.element.js"),
    weight: 100,
    meta: {
      label: "LlmsTxt",
      pathname: "llmstxt-settings",
    },
    conditions: [
      {
        alias: "Umb.Condition.SectionAlias",
        match: "Umb.Section.Settings",
      },
    ],
  },
];
