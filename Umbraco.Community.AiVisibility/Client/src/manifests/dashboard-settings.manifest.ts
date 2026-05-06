// Story 3.2 — Backoffice Settings dashboard manifest. Follows Spike 0.B locked
// decision #4: alias `AiVisibility.Dashboard.Settings`, custom element tag
// `aiv-settings-dashboard`, conditioned on the Settings section.
export const manifests: Array<UmbExtensionManifest> = [
  {
    type: "dashboard",
    alias: "AiVisibility.Dashboard.Settings",
    name: "AI Visibility Settings Dashboard",
    element: () => import("../elements/aiv-settings-dashboard.element.js"),
    weight: 100,
    meta: {
      label: "AI Visibility",
      pathname: "aivisibility-settings",
    },
    conditions: [
      {
        alias: "Umb.Condition.SectionAlias",
        match: "Umb.Section.Settings",
      },
    ],
  },
];
