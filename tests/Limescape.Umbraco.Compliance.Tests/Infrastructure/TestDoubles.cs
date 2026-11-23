using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;

namespace Limescape.Umbraco.Compliance.Tests.Infrastructure;

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;

    public T Get(string? name) => value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

internal sealed class StubBackOfficeSecurityAccessor(IUser? currentUser) : IBackOfficeSecurityAccessor
{
    public IBackOfficeSecurity? BackOfficeSecurity { get; } = new StubBackOfficeSecurity(currentUser);

    private sealed class StubBackOfficeSecurity(IUser? currentUser) : IBackOfficeSecurity
    {
        public IUser? CurrentUser => currentUser;

        public Attempt<int> GetUserId() => currentUser is null ? Attempt<int>.Fail() : Attempt.Succeed(currentUser.Id);

        public bool IsAuthenticated() => currentUser is not null;

        public bool UserHasSectionAccess(string section, IUser user) => false;
    }
}
