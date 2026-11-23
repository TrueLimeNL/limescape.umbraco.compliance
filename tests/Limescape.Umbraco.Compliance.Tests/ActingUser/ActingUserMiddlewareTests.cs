using System.Net;
using System.Text.Json;
using Limescape.Umbraco.Compliance.ActingUser;
using Limescape.Umbraco.Compliance.Tests.Infrastructure;

namespace Limescape.Umbraco.Compliance.Tests.ActingUser;

/// <summary>
/// Drives the middleware over HTTP in the real site. These tests bypass any UI.
/// </summary>
public sealed class ActingUserMiddlewareTests(UmbracoTestSite site)
{
    [Fact]
    public async Task Valid_token_attributes_the_audit_entry_to_the_person_in_the_token()
    {
        var key = Guid.NewGuid();
        var token = TestIssuer.Shared.CreateToken(subject: "p-42", name: "Pat Example", channel: "assistant", correlationId: "cid-valid");

        using var response = await PostProbe(key, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entry = Assert.Single(ComplianceLogs.AuditFor(site, key));
        Assert.Equal("p-42", entry.ActorId);
        Assert.Equal("Pat Example", entry.ActorName);
        Assert.Equal("assistant", entry.Channel);
        Assert.Equal(TestIssuer.Shared.Issuer, entry.Issuer);
        Assert.Equal("cid-valid", entry.CorrelationId);
    }

    [Fact]
    public async Task Forged_token_is_rejected_with_401_and_nothing_is_written()
    {
        var key = Guid.NewGuid();
        using var forger = new TestIssuer(TestIssuer.Shared.Issuer);

        using var response = await PostProbe(key, forger.CreateToken(subject: "p-42"));

        await AssertRejected(response);
        Assert.Empty(ComplianceLogs.AuditFor(site, key));
    }

    [Fact]
    public async Task Expired_token_is_rejected_with_401_and_nothing_is_written()
    {
        var key = Guid.NewGuid();
        var token = TestIssuer.Shared.CreateToken(issuedAt: DateTimeOffset.UtcNow.AddMinutes(-5), lifetime: TimeSpan.FromSeconds(60));

        using var response = await PostProbe(key, token);

        await AssertRejected(response);
        Assert.Empty(ComplianceLogs.AuditFor(site, key));
    }

    [Fact]
    public async Task Malformed_header_is_rejected_and_never_falls_back_to_the_umbraco_user()
    {
        var key = Guid.NewGuid();

        using var response = await PostProbe(key, "not-a-token");

        await AssertRejected(response);
        Assert.Empty(ComplianceLogs.AuditFor(site, key));
    }

    [Fact]
    public async Task Request_without_header_passes_and_is_attributed_to_the_signed_in_user_or_system()
    {
        var key = Guid.NewGuid();

        using var response = await PostProbe(key, token: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entry = Assert.Single(ComplianceLogs.AuditFor(site, key));
        // The probe call is anonymous, so there is no Umbraco user to fall back to.
        Assert.Equal(Actor.SystemId, entry.ActorId);
        Assert.Null(entry.Issuer);
    }

    [Fact]
    public async Task Forged_token_is_rejected_on_management_api_routes_before_umbraco_handles_them()
    {
        using var forger = new TestIssuer(TestIssuer.Shared.Issuer);
        using var client = site.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/umbraco/management/api/v1/server/status");
        request.Headers.Add(ActingUserOptions.DefaultHeaderName, forger.CreateToken());

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        await AssertRejected(response);
    }

    private async Task<HttpResponseMessage> PostProbe(Guid key, string? token)
    {
        using var client = site.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{UmbracoTestSite.AuditProbePath}?key={key}");
        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation(ActingUserOptions.DefaultHeaderName, token);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task AssertRejected(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(ActingUserMiddleware.InvalidTokenCode, body.GetProperty("code").GetString());
    }
}
