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
    alias: "Llms.Dashboard.Spike",
    name: "LlmsTxt Spike Dashboard",
    element: () => import("./llms-spike-dashboard.element-DkguFUaq.js"),
    weight: 100,
    meta: {
      label: "LlmsTxt Spike",
      pathname: "llmstxt-spike"
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
  ...a
];
export {
  s as manifests
};
//# sourceMappingURL=llms-txt-umbraco.js.map
