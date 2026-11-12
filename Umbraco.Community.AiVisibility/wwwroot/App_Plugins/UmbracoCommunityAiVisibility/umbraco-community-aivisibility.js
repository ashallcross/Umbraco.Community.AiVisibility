const i = [
  {
    type: "dashboard",
    alias: "AiVisibility.Dashboard.Settings",
    name: "AI Visibility Settings Dashboard",
    element: () => import("./aiv-settings-dashboard.element-3pynSPeN.js"),
    weight: 100,
    meta: {
      label: "AI Visibility",
      pathname: "aivisibility-settings"
    },
    conditions: [
      {
        alias: "Umb.Condition.SectionAlias",
        match: "Umb.Section.Settings"
      }
    ]
  }
], t = [
  {
    type: "dashboard",
    alias: "AiVisibility.Dashboard.AiTraffic",
    name: "AI Visibility AI Traffic Dashboard",
    element: () => import("./aiv-ai-traffic-dashboard.element-DDDESoE1.js"),
    weight: 90,
    meta: {
      label: "AI Traffic",
      pathname: "aivisibility-ai-traffic"
    },
    conditions: [
      {
        alias: "Umb.Condition.SectionAlias",
        match: "Umb.Section.Settings"
      }
    ]
  }
], a = [
  ...i,
  ...t
];
export {
  a as manifests
};
