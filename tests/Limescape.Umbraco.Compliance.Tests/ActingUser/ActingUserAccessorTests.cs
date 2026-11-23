using Limescape.Umbraco.Compliance.ActingUser;
using Limescape.Umbraco.Compliance.Tests.Infrastructure;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Models.Membership;

namespace Limescape.Umbraco.Compliance.Tests.ActingUser;

public sealed class ActingUserAccessorTests
{
    private static readonly ActingUserToken Token = new(
        "p-42", "Pat Example", "assistant", "cid-1", "https://trusted.example.test", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(30));

    [Fact]
    public void Token_person_is_the_actor_and_the_umbraco_user_the_technical_identity()
    {
        var apiUser = User("Integration", UserKind.Api);
        var accessor = Accessor(Context(withToken: true), apiUser);

        var actor = accessor.Current;

        Assert.Equal("p-42", actor.PersonId);
        Assert.Equal("Pat Example", actor.Name);
        Assert.Equal("assistant", actor.Channel);
        Assert.Equal("https://trusted.example.test", actor.Issuer);
        Assert.True(actor.FromTrustedIntegration);
        Assert.Equal(apiUser.Key, actor.TechnicalUserKey);
        Assert.Equal("Integration", actor.TechnicalUserName);
        Assert.Equal("cid-1", actor.CorrelationId);
    }

    [Fact]
    public void Without_token_the_backoffice_user_is_the_actor()
    {
        var editor = User("Editor", UserKind.Default);
        var accessor = Accessor(Context(withToken: false), editor);

        var actor = accessor.Current;

        Assert.Equal(editor.Key.ToString(), actor.PersonId);
        Assert.Equal("Editor", actor.Name);
        Assert.Equal(ActorChannels.Backoffice, actor.Channel);
        Assert.False(actor.FromTrustedIntegration);
    }

    [Fact]
    public void Without_token_an_api_user_is_the_actor_on_the_api_channel()
    {
        var accessor = Accessor(Context(withToken: false), User("Integration", UserKind.Api));

        Assert.Equal(ActorChannels.Api, accessor.Current.Channel);
    }

    [Fact]
    public void Correlation_id_falls_back_to_the_request_header()
    {
        var context = Context(withToken: false);
        context.Request.Headers[ActingUserAccessor.CorrelationIdHeader] = "from-header";

        var actor = Accessor(context, User("Editor", UserKind.Default)).Current;

        Assert.Equal("from-header", actor.CorrelationId);
    }

    [Fact]
    public void Anonymous_request_is_attributed_to_the_system()
    {
        var actor = Accessor(Context(withToken: false), currentUser: null).Current;

        Assert.Equal(Actor.SystemId, actor.PersonId);
        Assert.Equal(ActorChannels.System, actor.Channel);
    }

    [Fact]
    public void Work_outside_a_request_is_attributed_to_the_system()
    {
        var accessor = new ActingUserAccessor(new HttpContextAccessor(), new StubBackOfficeSecurityAccessor(null));

        Assert.Equal(Actor.SystemId, accessor.Current.PersonId);
    }

    private static ActingUserAccessor Accessor(HttpContext context, IUser? currentUser) =>
        new(new HttpContextAccessor { HttpContext = context }, new StubBackOfficeSecurityAccessor(currentUser));

    private static DefaultHttpContext Context(bool withToken)
    {
        var context = new DefaultHttpContext();
        if (withToken)
        {
            context.Features.Set(new ActingUserTokenFeature(Token));
        }

        return context;
    }

    private static User User(string name, UserKind kind) =>
        new(new GlobalSettings(), name, $"{name.ToLowerInvariant()}@example.test", name.ToLowerInvariant(), "unused")
        {
            Key = Guid.NewGuid(),
            Kind = kind,
        };
}
