using Limescape.Umbraco.Compliance.Persistence;
using Umbraco.Cms.Infrastructure.Scoping;

namespace Limescape.Umbraco.Compliance.Tests.Infrastructure;

/// <summary>
/// Reads the package log tables directly from the test site's database.
/// </summary>
internal static class ComplianceLogs
{
    public static List<AuditEntryDto> AuditFor(UmbracoTestSite site, Guid entityKey)
    {
        using var scope = site.Services.GetRequiredService<IScopeProvider>().CreateScope(autoComplete: true);
        var sql = scope.SqlContext.Sql()
            .Select<AuditEntryDto>()
            .From<AuditEntryDto>()
            .Where<AuditEntryDto>(x => x.EntityKey == entityKey)
            .OrderBy<AuditEntryDto>(x => x.Id);
        return scope.Database.Fetch<AuditEntryDto>(sql);
    }

    public static List<DeletionLogEntryDto> DeletionLogFor(UmbracoTestSite site, Guid entityKey)
    {
        using var scope = site.Services.GetRequiredService<IScopeProvider>().CreateScope(autoComplete: true);
        var sql = scope.SqlContext.Sql()
            .Select<DeletionLogEntryDto>()
            .From<DeletionLogEntryDto>()
            .Where<DeletionLogEntryDto>(x => x.EntityKey == entityKey)
            .OrderBy<DeletionLogEntryDto>(x => x.Id);
        return scope.Database.Fetch<DeletionLogEntryDto>(sql);
    }
}
