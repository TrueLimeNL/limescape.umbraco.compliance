// Content app "Retention and audit" on documents and media: retention per item. Audit history follows with the audit API.
const view = (alias: string, name: string, workspace: string): UmbExtensionManifest => ({
  type: "workspaceView",
  alias,
  name,
  element: () => import("./compliance-workspace-view.element.js"),
  weight: 50,
  meta: {
    label: "#lsCompliance_workspaceViewLabel",
    pathname: "compliance",
    icon: "icon-shield",
  },
  conditions: [
    {
      alias: "Umb.Condition.WorkspaceAlias",
      match: workspace,
    },
  ],
});

export const manifests: Array<UmbExtensionManifest> = [
  view("Limescape.Umbraco.Compliance.WorkspaceView.Document", "Limescape Compliance Document Workspace View", "Umb.Workspace.Document"),
  view("Limescape.Umbraco.Compliance.WorkspaceView.Media", "Limescape Compliance Media Workspace View", "Umb.Workspace.Media"),
];
