using Microsoft.AspNetCore.Http;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;

namespace Limescape.Umbraco.Compliance.ActingUser;

/// <summary>
/// Resolves the person the current operation is attributed to.
/// </summary>
public interface IActingUserAccessor
{
    /// <summary>
    /// The acting person: from a validated acting-user token if the request carried one, otherwise the signed-in
    /// Umbraco user, otherwise <see cref="Actor.System"/>. Never null.
    /// </summary>
    Actor Current { get; }
}

/// <summary>
/// Default <see cref="IActingUserAccessor"/>. Reads the token feature set by <see cref="ActingUserMiddleware"/> and the
/// Umbraco user lazily, so it works in notification handlers after authentication has run.
/// </summary>
public sealed class ActingUserAccessor(IHttpContextAccessor httpContextAccessor, IBackOfficeSecurityAccessor backOfficeSecurityAccessor)
    : IActingUserAccessor
{
    /// <summary>Request header read for the correlation id when the token does not carry one.</summary>
    public const string CorrelationIdHeader = "X-Correlation-Id";

    /// <inheritdoc />
    public Actor Current
    {
        get
        {
            var httpContext = httpContextAccessor.HttpContext;
            if (httpContext is null)
            {
                return Actor.System();
            }

            var technicalUser = backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser;
            var token = httpContext.Features.Get<ActingUserTokenFeature>()?.Token;
            var requestCorrelationId = RequestCorrelationId(httpContext);

            if (token is not null)
            {
                return new Actor(token.Subject, token.Name, token.Channel)
                {
                    Issuer = token.Issuer,
                    TechnicalUserKey = technicalUser?.Key,
                    TechnicalUserName = technicalUser?.Name,
                    CorrelationId = token.CorrelationId ?? requestCorrelationId,
                };
            }

            if (technicalUser is not null)
            {
                var channel = technicalUser.Kind == UserKind.Api ? ActorChannels.Api : ActorChannels.Backoffice;
                return new Actor(technicalUser.Key.ToString(), technicalUser.Name, channel)
                {
                    TechnicalUserKey = technicalUser.Key,
                    TechnicalUserName = technicalUser.Name,
                    CorrelationId = requestCorrelationId,
                };
            }

            return Actor.System(requestCorrelationId);
        }
    }

    private static string RequestCorrelationId(HttpContext httpContext)
    {
        var header = httpContext.Request.Headers[CorrelationIdHeader].ToString();
        return header is { Length: > 0 and <= 128 } ? header : httpContext.TraceIdentifier;
    }
}
