using System.Net.Http.Headers;
using System.Text.Json;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;

namespace Limescape.Umbraco.Compliance.Tests.Infrastructure;

/// <summary>
/// Shared setup for the publication gate tests in the one test site: a gated and an open document type, a reviewer
/// group, two API users with client credentials (editor, and editor plus reviewer) and a backoffice editor.
/// Created once per test run; tests create their own documents.
/// </summary>
internal sealed class GateTestBed
{
    /// <summary>Gated document type, configured in <see cref="UmbracoTestSite"/>.</summary>
    public const string GatedType = "lsGateTestGated";

    /// <summary>Document type that is not gated; allows <see cref="GatedType"/> below it.</summary>
    public const string OpenType = "lsGateTestOpen";

    /// <summary>Composition configured as gated in <see cref="UmbracoTestSite"/>.</summary>
    public const string GatedComposition = "lsGateTestBase";

    /// <summary>Document type that is not listed itself but is composed of <see cref="GatedComposition"/>.</summary>
    public const string ComposedType = "lsGateTestComposed";

    /// <summary>Reviewer group, configured in <see cref="UmbracoTestSite"/>.</summary>
    public const string ReviewerGroup = "lsGateTestReviewers";

    public const string ManagementApi = "/umbraco/management/api/v1";

    private static readonly SemaphoreSlim Lock = new(1, 1);
    private static GateTestBed? _instance;

    private GateTestBed(UmbracoTestSite site) => Site = site;

    public UmbracoTestSite Site { get; }

    public ApiClient Editor { get; private set; } = null!;

    public ApiClient Reviewer { get; private set; } = null!;

    /// <summary>A backoffice person (user kind Default) in the Editors group.</summary>
    public Guid BackofficeEditorKey { get; private set; }

    public IContentService ContentService => Site.Services.GetRequiredService<IContentService>();

    public static async Task<GateTestBed> GetAsync(UmbracoTestSite site)
    {
        await Lock.WaitAsync();
        try
        {
            if (_instance is null)
            {
                var bed = new GateTestBed(site);
                await bed.SetUpAsync();
                _instance = bed;
            }

            return _instance;
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>Creates and saves a draft document of the given type.</summary>
    public IContent CreateDraft(string type = GatedType, string? title = null, int parentId = Constants.System.Root)
    {
        var content = ContentService.Create($"Gate test {Guid.NewGuid():N}", parentId, type);
        content.SetValue("title", title ?? "First draft");
        ContentService.Save(content);
        return content;
    }

    /// <summary>Acting-user token for a reviewer, as the integration sends it with an approval.</summary>
    public static string ReviewerToken(string subject = "reviewer-1", string name = "Rita Reviewer") =>
        TestIssuer.Shared.CreateToken(subject: subject, name: name, channel: "app");

    public IContent Reload(Guid key) => ContentService.GetById(key) ?? throw new InvalidOperationException($"Document {key} not found");

    private async Task SetUpAsync()
    {
        using var serviceScope = Site.Services.CreateScope();
        var services = serviceScope.ServiceProvider;
        var shortStringHelper = services.GetRequiredService<IShortStringHelper>();
        var contentTypeService = services.GetRequiredService<IContentTypeService>();
        var dataTypeService = services.GetRequiredService<IDataTypeService>();
        var userGroupService = services.GetRequiredService<IUserGroupService>();
        var userService = services.GetRequiredService<IUserService>();
        var credentials = services.GetRequiredService<IBackOfficeUserClientCredentialsManager>();
        var admin = Constants.Security.SuperUserKey;

        var textstring = await dataTypeService.GetAsync(Constants.DataTypes.Guids.TextstringGuid)
            ?? throw new InvalidOperationException("Textstring data type not found");

        var gated = DocumentType(shortStringHelper, textstring, GatedType);
        Ensure(await contentTypeService.CreateAsync(gated, admin), "gated document type");

        var open = DocumentType(shortStringHelper, textstring, OpenType);
        open.AllowedContentTypes = [new ContentTypeSort(gated.Key, 0, gated.Alias)];
        Ensure(await contentTypeService.CreateAsync(open, admin), "open document type");

        var composition = new ContentType(shortStringHelper, Constants.System.Root) { Alias = GatedComposition, Name = GatedComposition };
        composition.AddPropertyGroup("base", "Base");
        composition.AddPropertyType(new PropertyType(shortStringHelper, textstring, "summary") { Name = "Summary" }, "base", "Base");
        Ensure(await contentTypeService.CreateAsync(composition, admin), "gated composition");

        var composed = DocumentType(shortStringHelper, textstring, ComposedType);
        composed.AddContentType(composition);
        Ensure(await contentTypeService.CreateAsync(composed, admin), "composed document type");

        var reviewers = new UserGroup(shortStringHelper) { Alias = ReviewerGroup, Name = "Gate test reviewers" };
        Ensure(await userGroupService.CreateAsync(reviewers, admin), "reviewer group");
        var editors = await userGroupService.GetAsync("editor")
            ?? throw new InvalidOperationException("Editors group not found");

        Editor = await ApiUserAsync(userService, credentials, "gate-editor", [editors.Key]);
        Reviewer = await ApiUserAsync(userService, credentials, "gate-reviewer", [editors.Key, reviewers.Key]);

        var person = await userService.CreateAsync(
            admin,
            new UserCreateModel { Email = "gate-person@example.test", UserName = "gate-person@example.test", Name = "Gate Person", Kind = UserKind.Default, UserGroupKeys = new HashSet<Guid> { editors.Key } },
            approveUser: true);
        Ensure(person, "backoffice editor");
        BackofficeEditorKey = person.Result.CreatedUser!.Key;
    }

    private async Task<ApiClient> ApiUserAsync(IUserService userService, IBackOfficeUserClientCredentialsManager credentials, string name, Guid[] groups)
    {
        var created = await userService.CreateAsync(
            Constants.Security.SuperUserKey,
            new UserCreateModel { Email = $"{name}@example.test", UserName = $"{name}@example.test", Name = name, Kind = UserKind.Api, UserGroupKeys = groups.ToHashSet() },
            approveUser: true);
        Ensure(created, name);

        var clientId = $"umbraco-back-office-{name}";
        var secret = $"S{Guid.NewGuid():N}";
        var saved = await credentials.SaveAsync(created.Result.CreatedUser!.Key, clientId, secret);
        if (!saved.Success)
        {
            throw new InvalidOperationException($"Client credentials for {name}: {saved.Result}");
        }

        return new ApiClient(Site, created.Result.CreatedUser.Key, clientId, secret);
    }

    private static ContentType DocumentType(IShortStringHelper shortStringHelper, IDataType textstring, string alias)
    {
        var type = new ContentType(shortStringHelper, Constants.System.Root) { Alias = alias, Name = alias, AllowedAsRoot = true };
        type.AddPropertyGroup("content", "Content");
        type.AddPropertyType(new PropertyType(shortStringHelper, textstring, "title") { Name = "Title" }, "content", "Content");
        foreach (var propertyAlias in new[] { "status", "owners", "archivedAt" })
        {
            type.AddPropertyType(new PropertyType(shortStringHelper, textstring, propertyAlias) { Name = propertyAlias }, "content", "Content");
        }
        return type;
    }

    private static void Ensure<TStatus>(Attempt<TStatus> attempt, string what)
    {
        if (!attempt.Success)
        {
            throw new InvalidOperationException($"Could not create {what}: {attempt.Result}");
        }
    }

    private static void Ensure<TResult, TStatus>(Attempt<TResult, TStatus> attempt, string what)
    {
        if (!attempt.Success)
        {
            throw new InvalidOperationException($"Could not create {what}: {attempt.Status}");
        }
    }
}

/// <summary>
/// An Umbraco API user with client credentials that calls the Management API over HTTP, like an integration would.
/// </summary>
internal sealed class ApiClient(UmbracoTestSite site, Guid userKey, string clientId, string secret)
{
    public Guid UserKey { get; } = userKey;

    /// <summary>Sends a request with a fresh access token; adds the acting-user token when given.</summary>
    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, string? actingUserToken = null)
    {
        using var client = site.CreateHttpsClient();
        var token = await TokenAsync(client);

        using var request = new HttpRequestMessage(method, new Uri(GateTestBed.ManagementApi + path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (actingUserToken is not null)
        {
            request.Headers.Add("X-Limescape-Acting-User", actingUserToken);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Publishes an invariant document through <c>PUT /document/{id}/publish</c>.</summary>
    public Task<HttpResponseMessage> PublishAsync(Guid documentKey) =>
        SendAsync(HttpMethod.Put, $"/document/{documentKey}/publish", new { publishSchedules = new[] { new { culture = (string?)null } } });

    private async Task<string> TokenAsync(HttpClient client)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = secret,
        });
        using var response = await client.PostAsync(
            new Uri(GateTestBed.ManagementApi + "/security/back-office/token", UriKind.Relative), form, TestContext.Current.CancellationToken);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Token request for {clientId} failed: {(int)response.StatusCode} {json}");
        }

        return JsonDocument.Parse(json).RootElement.GetProperty("access_token").GetString()!;
    }
}
