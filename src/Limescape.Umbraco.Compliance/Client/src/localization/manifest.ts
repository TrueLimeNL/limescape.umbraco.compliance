export const manifests: Array<UmbExtensionManifest> = [
  {
    type: "localization",
    alias: "Limescape.Umbraco.Compliance.Localization.En",
    name: "Limescape Compliance English",
    meta: { culture: "en" },
    js: () => import("./en.js"),
  },
  {
    type: "localization",
    alias: "Limescape.Umbraco.Compliance.Localization.Nl",
    name: "Limescape Compliance Dutch",
    meta: { culture: "nl" },
    js: () => import("./nl.js"),
  },
];
