using System.Globalization;
using Limescape.Umbraco.Compliance.PublicationGate;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;

namespace Limescape.Umbraco.Compliance.FieldState;

/// <summary>
/// Refuses a save of a gated document that writes a new value into an archived field, or a deprecated code the document
/// did not hold. It sits on <see cref="ContentSavingNotification"/>, so it covers every route that saves:
/// the backoffice, the Management API, the Agent API, integrations and code. The Agent API checks the same rule earlier to
/// answer with a precise message; this is the backstop for the routes that do not.
/// </summary>
public sealed class FieldStateGuardHandler(
    IFieldStateService fieldStates,
    IGatedContentTypes gatedTypes,
    IContentService contentService,
    IBackOfficeSecurityAccessor backOfficeSecurityAccessor) : INotificationHandler<ContentSavingNotification>
{
    /// <inheritdoc />
    public void Handle(ContentSavingNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        foreach (var content in notification.SavedEntities.Where(c => !c.Blueprint && gatedTypes.IsGated(c)))
        {
            if (fieldStates.Check(content, () => content.HasIdentity ? contentService.GetById(content.Key) : null) is not { } violation)
            {
                continue;
            }

            var culture = GateMessages.CultureOf(backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser?.Language);
            notification.CancelOperation(new EventMessage(violation.Code, FieldStateMessages.Text(violation, culture), EventMessageType.Error));
            return;
        }
    }
}

/// <summary>Localized texts (nl, en) of the field state rules.</summary>
public static class FieldStateMessages
{
    /// <summary>The text for <paramref name="violation"/>: Dutch when <paramref name="culture"/> is Dutch, otherwise English.</summary>
    public static string Text(FieldStateViolation violation, CultureInfo? culture)
    {
        ArgumentNullException.ThrowIfNull(violation);
        var dutch = (culture ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName == "nl";
        return violation.Code == FieldStateErrorCodes.FieldArchived
            ? dutch
                ? $"Het veld '{violation.Alias}' is gearchiveerd: er kan geen nieuwe waarde meer in worden gezet. Bestaande waarden blijven staan."
                : $"Field '{violation.Alias}' is archived: no new value can be set. Existing values stay."
            : dutch
                ? $"De waarde '{violation.Value}' van veld '{violation.Alias}' is uitgefaseerd: ze is alleen toegestaan op een materiaal dat haar al had."
                : $"Value '{violation.Value}' of field '{violation.Alias}' is deprecated: only a material that held it already can keep it.";
    }
}
