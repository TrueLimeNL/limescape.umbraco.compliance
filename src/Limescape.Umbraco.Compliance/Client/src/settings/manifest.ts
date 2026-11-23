const MANAGER_CONDITION_ALIAS = "Limescape.Umbraco.Compliance.Condition.RetentionManager";


// Settings dashboard "Retention": classes, assignments and ended items. Shown to retention managers only (the API enforces it).
export const manifests: Array<UmbExtensionManifest> = [
  {
    type: "condition",
    alias: MANAGER_CONDITION_ALIAS,
    name: "Limescape Compliance Retention Manager Condition",
    api: () => import("./manager-condition.js"),
  },
  {
    type: "dashboard",
    alias: "Limescape.Umbraco.Compliance.Dashboard.Retention",
    name: "Limescape Compliance Retention Dashboard",
    element: () => import("./retention-dashboard.element.js"),
    weight: 50,
    meta: {
      label: "#lsCompliance_dashboardLabel",
      pathname: "retention",
    },
    conditions: [
      {
        alias: "Umb.Condition.SectionAlias",
        match: "Umb.Section.Settings",
      },
      {
        alias: MANAGER_CONDITION_ALIAS,
      },
    ],
  },
];
