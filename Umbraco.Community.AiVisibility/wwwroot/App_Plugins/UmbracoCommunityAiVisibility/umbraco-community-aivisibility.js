const i = [
  {
    name: "Umbraco Community AI Visibility Entrypoint",
    alias: "Umbraco.Community.AiVisibility.Entrypoint",
    type: "backofficeEntryPoint",
    js: () => import("./entrypoint-BSlTz4-p.js")
  }
], t = [
  {
    type: "dashboard",
    alias: "AiVisibility.Dashboard.Settings",
    name: "AI Visibility Settings Dashboard",
    element: () => import("./aiv-settings-dashboard.element-D9uA2o4l.js"),
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
], a = [
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
], s = [
  ...i,
  ...t,
  ...a
];
export {
  s as manifests
};
