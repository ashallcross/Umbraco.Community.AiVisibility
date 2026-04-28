export const manifests: Array<UmbExtensionManifest> = [
  {
    type: "dashboard",
    alias: "Llms.Dashboard.Spike",
    name: "LlmsTxt Spike Dashboard",
    element: () => import("../elements/llms-spike-dashboard.element.js"),
    weight: 100,
    meta: {
      label: "LlmsTxt Spike",
      pathname: "llmstxt-spike",
    },
    conditions: [
      {
        alias: "Umb.Condition.SectionAlias",
        match: "Umb.Section.Settings",
      },
    ],
  },
];
