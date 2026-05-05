const t = [
  {
    name: "Llms Txt Umbraco Entrypoint",
    alias: "LlmsTxt.Umbraco.Entrypoint",
    type: "backofficeEntryPoint",
    js: () => import("./entrypoint-BSlTz4-p.js")
  }
], a = [
  {
    type: "dashboard",
    alias: "Llms.Dashboard.Settings",
    name: "LlmsTxt Settings Dashboard",
    element: () => import("./llms-settings-dashboard.element-BkfV2ssp.js"),
    weight: 100,
    meta: {
      label: "LlmsTxt",
      pathname: "llmstxt-settings"
    },
    conditions: [
      {
        alias: "Umb.Condition.SectionAlias",
        match: "Umb.Section.Settings"
      }
    ]
  }
], i = [
  {
    type: "dashboard",
    alias: "Llms.Dashboard.AiTraffic",
    name: "LlmsTxt AI Traffic Dashboard",
    element: () => import("./llms-ai-traffic-dashboard.element-C3obrJpd.js"),
    weight: 90,
    meta: {
      label: "AI Traffic",
      pathname: "llmstxt-ai-traffic"
    },
    conditions: [
      {
        alias: "Umb.Condition.SectionAlias",
        match: "Umb.Section.Settings"
      }
    ]
  }
], s = [
  ...t,
  ...a,
  ...i
];
export {
  s as manifests
};
//# sourceMappingURL=llms-txt-umbraco.js.map
