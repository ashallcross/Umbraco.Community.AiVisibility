const t = [
  {
    name: "Llms Txt Umbraco Entrypoint",
    alias: "LlmsTxt.Umbraco.Entrypoint",
    type: "backofficeEntryPoint",
    js: () => import("./entrypoint-BSlTz4-p.js")
  }
], s = [
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
], a = [
  ...t,
  ...s
];
export {
  a as manifests
};
//# sourceMappingURL=llms-txt-umbraco.js.map
