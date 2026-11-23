using System.Net;
using Limescape.Umbraco.Compliance.Composers;
using Limescape.Umbraco.Compliance.Tests.Infrastructure;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.Manifest;
using Umbraco.Cms.Core.Services;

namespace Limescape.Umbraco.Compliance.Tests;

public sealed class PackageSmokeTests(UmbracoTestSite site)
{
    private const string BundlePath = CompliancePackage.AppPluginsPath + "/limescape-umbraco-compliance.js";

    [Fact]
    public void Site_with_package_installed_reaches_run_level()
    {
        var runtimeState = site.Services.GetRequiredService<IRuntimeState>();

        Assert.Equal(RuntimeLevel.Run, runtimeState.Level);
    }

    [Fact]
    public void Umbraco_discovers_the_package_composer()
    {
        var typeLoader = site.Services.GetRequiredService<TypeLoader>();

        Assert.Contains(typeof(ComplianceComposer), typeLoader.GetTypes<IComposer>());
    }

    [Fact]
    public async Task Package_manifest_registers_the_backoffice_bundle()
    {
        var manifests = await site.Services.GetRequiredService<IPackageManifestService>().GetAllPackageManifestsAsync();

        var manifest = Assert.Single(manifests, m => m.Id == CompliancePackage.Id);
        Assert.Contains(manifest.Extensions, e => e.ToString()!.Contains(BundlePath, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Backoffice_bundle_with_document_and_media_workspace_views_is_served()
    {
        using var client = site.CreateClient();

        using var response = await client.GetAsync(new Uri(BundlePath, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Limescape.Umbraco.Compliance.WorkspaceView.Document", body, StringComparison.Ordinal);
        Assert.Contains("Limescape.Umbraco.Compliance.WorkspaceView.Media", body, StringComparison.Ordinal);
    }
}
