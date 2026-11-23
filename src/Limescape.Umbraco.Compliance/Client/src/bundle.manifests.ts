import { manifests as localization } from "./localization/manifest.js";
import { manifests as settings } from "./settings/manifest.js";
import { manifests as workspaceView } from "./workspace-view/manifest.js";

// Collates every manifest of the extension; loaded from umbraco-package.json.
export const manifests: Array<UmbExtensionManifest> = [
  ...localization,
  ...settings,
  ...workspaceView,
];
