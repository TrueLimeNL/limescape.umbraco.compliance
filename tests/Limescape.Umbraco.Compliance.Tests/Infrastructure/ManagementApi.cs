using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;

namespace Limescape.Umbraco.Compliance.Tests.Infrastructure;

/// <summary>
/// Calls the Management API of the test site the way an integration does: an API user with client credentials,
/// a bearer token from the back-office token endpoint. API users are created once per user group.
/// </summary>
internal static class ManagementApi
{
    public const string Base = "/umbraco/management/api/v1";

    private static readonly ConcurrentDictionary<string, Lazy<Task<(string ClientId, string Secret)>>> Clients = new();

    /// <summary>An HTTP client authenticated as an API user in <paramref name="userGroupAlias"/> (admin, editor, writer, …).</summary>
    public static async Task<HttpClient> ClientAsync(UmbracoTestSite site, string userGroupAlias)
    {
        var (clientId, secret) = await Clients.GetOrAdd(userGroupAlias, alias => new Lazy<Task<(string, string)>>(() => CreateApiUserAsync(site, alias))).Value;
        var client = site.CreateClient();
        // OpenIddict only accepts HTTPS; TestServer takes the scheme from the base address.
        client.BaseAddress = new Uri("https://localhost/");
        using var response = await client.PostAsync(
            new Uri(Base + "/security/back-office/token", UriKind.Relative),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = secret,
            }));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"token request failed: {(int)response.StatusCode} {body}");
        var token = JsonDocument.Parse(body).RootElement.GetProperty("access_token").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string path, object body) =>
        client.PostAsJsonAsync(new Uri(Base + path, UriKind.Relative), body);

    public static Task<HttpResponseMessage> PutJsonAsync(this HttpClient client, string path, object body) =>
        client.PutAsJsonAsync(new Uri(Base + path, UriKind.Relative), body);

    public static Task<HttpResponseMessage> GetPathAsync(this HttpClient client, string path) =>
        client.GetAsync(new Uri(Base + path, UriKind.Relative));

    public static Task<HttpResponseMessage> DeletePathAsync(this HttpClient client, string path) =>
        client.DeleteAsync(new Uri(Base + path, UriKind.Relative));

    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<(string ClientId, string Secret)> CreateApiUserAsync(UmbracoTestSite site, string userGroupAlias)
    {
        var userService = site.Services.GetRequiredService<IUserService>();
        var group = await site.Services.GetRequiredService<IUserGroupService>().GetAsync(userGroupAlias)
            ?? throw new InvalidOperationException($"User group {userGroupAlias} does not exist.");
        var email = $"api-{userGroupAlias}-{Guid.NewGuid():N}@example.test";
        var created = await userService.CreateAsync(
            Constants.Security.SuperUserKey,
            new UserCreateModel { Email = email, UserName = email, Name = $"Integration ({userGroupAlias})", Kind = UserKind.Api, UserGroupKeys = new HashSet<Guid> { group.Key } },
            approveUser: true);
        Assert.True(created.Success, $"creating API user failed: {created.Status}");

        var clientId = $"umbraco-back-office-test-{userGroupAlias}-{Guid.NewGuid():N}"[..60];
        var secret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        using var services = site.Services.CreateScope();
        var saved = await services.ServiceProvider.GetRequiredService<IBackOfficeUserClientCredentialsManager>().SaveAsync(created.Result.CreatedUser!.Key, clientId, secret);
        Assert.True(saved.Success, $"saving client credentials failed: {saved.Result}");
        return (clientId, secret);
    }
}
