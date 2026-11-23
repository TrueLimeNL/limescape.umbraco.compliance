using System.Runtime.Loader;
using Limescape.Umbraco.Compliance.ActingUser;
using Limescape.Umbraco.Compliance.Audit;
using Limescape.Umbraco.Compliance.PublicationGate;
using Limescape.Umbraco.Compliance.Retention;
using Limescape.Umbraco.Compliance.Submission;
using Microsoft.AspNetCore.TestHost;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;

namespace Limescape.Umbraco.Compliance.Tests.Infrastructure;

/// <summary>
/// Integration base: boots a real Umbraco 17 site in-process (TestServer) with the Compliance Pack installed,
/// on a fresh SQLite database via unattended install. One site per test assembly; tests that change data
/// create their own content and do not depend on order.
/// <see cref="TestIssuer.Shared"/> is configured as trusted acting-user issuer.
/// </summary>
public sealed class UmbracoTestSite : IAsyncLifetime
{
    /// <summary>
    /// Test-only endpoint that writes one audit entry for the entity key in the query string and returns the acting
    /// person, so tests can drive the acting-user middleware over HTTP like a Management API call would.
    /// </summary>
    public const string AuditProbePath = "/compliance-tests/audit-probe";

    /// <summary>User group configured as retention manager (besides administrators); a built-in group so no setup is needed.</summary>
    public const string RetentionManagerGroup = "writer";

    private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), "ls-compliance-tests", Guid.NewGuid().ToString("N"));
    private WebApplication? _app;

    static UmbracoTestSite()
    {
        // Umbraco loads every assembly referenced from the entry assembly while scanning for composers. The xunit.v3
        // runner ships some of its assemblies next to the test binary without listing them in deps.json, so the
        // default load context cannot find them by name. Resolve those from the test output folder.
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
    }

    public IServiceProvider Services => App.Services;

    private WebApplication App => _app ?? throw new InvalidOperationException("The test site has not been started.");

    public HttpClient CreateClient() => App.GetTestClient();

    /// <summary>Client on <c>https://localhost</c>: the back-office token endpoint (OpenIddict) only accepts HTTPS.</summary>
    public HttpClient CreateHttpsClient()
    {
        var client = App.GetTestClient();
        client.BaseAddress = new Uri("https://localhost/");
        return client;
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_contentRoot, "wwwroot"));

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _contentRoot,
            WebRootPath = Path.Combine(_contentRoot, "wwwroot"),
            // Development plus the test assembly name: ASP.NET Core loads the static web assets runtime manifest,
            // which maps /App_Plugins/Limescape.Umbraco.Compliance to the package build output.
            EnvironmentName = Environments.Development,
            ApplicationName = typeof(UmbracoTestSite).Assembly.GetName().Name,
        });

        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(Settings(_contentRoot));

        builder.CreateUmbracoBuilder()
            .AddBackOffice()
            .AddWebsite()
            .AddComposers()
            .Build();

        _app = builder.Build();

        await _app.BootUmbracoAsync();

        _app.UseUmbraco()
            .WithMiddleware(u =>
            {
                u.UseBackOffice();
                u.UseWebsite();
            })
            .WithEndpoints(u =>
            {
                u.UseBackOfficeEndpoints();
                u.UseWebsiteEndpoints();
                u.EndpointRouteBuilder.MapPost(AuditProbePath, async (Guid key, IAuditWriter writer, IActingUserAccessor accessor) =>
                {
                    await writer.WriteAsync(new AuditRecord(AuditActions.Update, AuditEntityTypes.Document, key));
                    return Results.Ok(accessor.Current);
                });
            });

        await _app.StartAsync();
        await WaitForRunLevelAsync(_app.Services);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        try
        {
            Directory.Delete(_contentRoot, recursive: true);
        }
        catch (IOException)
        {
            // SQLite or Lucene may still hold a file handle; the OS temp folder is cleaned up eventually.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Since Umbraco 17.7 pending package migrations (such as this package's tables) run in a background service after
    /// the host has started; the runtime level is Upgrading until they finish. Wait for Run so tests see the installed package.
    /// </summary>
    private static async Task WaitForRunLevelAsync(IServiceProvider services)
    {
        var runtimeState = services.GetRequiredService<IRuntimeState>();
        var routingReadiness = services.GetRequiredService<IContentRoutingReadiness>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        while (runtimeState.Level != RuntimeLevel.Run || !routingReadiness.IsReady)
        {
            if (runtimeState.Level == RuntimeLevel.BootFailed)
            {
                throw new InvalidOperationException("The test site failed to boot.", runtimeState.BootFailedException);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
        }
    }

    private static Dictionary<string, string?> Settings(string contentRoot) => new()
    {
        ["ConnectionStrings:umbracoDbDSN"] = $"Data Source={Path.Combine(contentRoot, "umbraco.sqlite.db")};Cache=Shared;Foreign Keys=True;Pooling=True",
        ["ConnectionStrings:umbracoDbDSN_ProviderName"] = "Microsoft.Data.Sqlite",
        ["Umbraco:CMS:Global:InstallMissingDatabase"] = "true",
        ["Umbraco:CMS:Unattended:InstallUnattended"] = "true",
        ["Umbraco:CMS:Unattended:UnattendedUserName"] = "Test Administrator",
        ["Umbraco:CMS:Unattended:UnattendedUserEmail"] = "administrator@example.test",
        // Throwaway database per run; generated so no credential lives in the repository.
        ["Umbraco:CMS:Unattended:UnattendedUserPassword"] = $"T{Guid.NewGuid():N}!",
        ["Umbraco:CMS:ModelsBuilder:ModelsMode"] = "Nothing",
        [$"{ActingUserOptions.SectionName}:TrustedIssuers:0:Issuer"] = TestIssuer.Shared.Issuer,
        [$"{ActingUserOptions.SectionName}:TrustedIssuers:0:PublicKeys:0"] = TestIssuer.Shared.PublicKeyPem,
        [$"{RetentionOptions.SectionName}:ManagerGroups:0"] = RetentionManagerGroup,
        [$"{PublicationGateOptions.SectionName}:GatedDocumentTypes:0"] = GateTestBed.GatedType,
        [$"{PublicationGateOptions.SectionName}:GatedDocumentTypes:1"] = GateTestBed.GatedComposition,
        [$"{PublicationGateOptions.SectionName}:ReviewerGroups:0"] = GateTestBed.ReviewerGroup,
        // Submission guard: only the submission test type has the status property, so other tests are not affected.
        [$"{SubmissionOptions.SectionName}:StatusProperty"] = SubmissionTestBed.StatusProperty,
        [$"{SubmissionOptions.SectionName}:RequiredFields:{SubmissionTestBed.DocumentType}:0"] = "title",
        [$"{SubmissionOptions.SectionName}:RequiredFields:{SubmissionTestBed.DocumentType}:1"] = "licence",
        [$"{SubmissionOptions.SectionName}:SchemaSnapshotPath"] = SubmissionTestBed.SnapshotFile,
    };
}
