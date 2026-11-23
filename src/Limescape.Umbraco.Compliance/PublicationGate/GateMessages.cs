using System.Globalization;

namespace Limescape.Umbraco.Compliance.PublicationGate;

/// <summary>
/// Error codes and localized messages (nl, en) of the publication gate. API callers match on the code; people read the text.
/// </summary>
public static class GateMessages
{
    /// <summary>Code of a publish that was cancelled because no approval exists for the version.</summary>
    public const string PublicationRequiresApproval = "publication_requires_approval";

    /// <summary>Code of an approval request by someone without the reviewer permission.</summary>
    public const string ApprovalRequiresReviewer = "approval_requires_reviewer";

    /// <summary>Code of an approval request by an API user without an acting-user token that names the reviewer.</summary>
    public const string ApprovalRequiresActingUser = "approval_requires_acting_user";

    /// <summary>Code of an approval request for a version that is no longer the current draft.</summary>
    public const string ApprovalVersionChanged = "approval_version_changed";

    /// <summary>Code of an approval request with an unknown decision value.</summary>
    public const string ApprovalInvalidDecision = "approval_invalid_decision";

    /// <summary>A non-interactive acting-user channel cannot decide.</summary>
    public const string ApprovalChannelNotAllowed = "approval_channel_not_allowed";
    /// <summary>An owner cannot approve their own material.</summary>
    public const string ApprovalOwnMaterial = "approval_own_material";

    /// <summary>A rejection must say why: a comment or findings.</summary>
    public const string ApprovalRejectionRequiresFeedback = "approval_rejection_requires_feedback";

    /// <summary>The decision could not be recorded because saving the lifecycle status was cancelled by another rule.</summary>
    public const string ApprovalCancelled = "approval_cancelled";

    /// <summary>An archived document cannot be published again.</summary>
    public const string PublicationArchived = "publication_archived";

    /// <summary>A save tried to set the archive date or move the status into or out of archived outside the archive route.</summary>
    public const string LifecycleArchiveRequiresRoute = "lifecycle_archive_requires_route";

    /// <summary>A save tried to set the status rejected without a recorded rejection.</summary>
    public const string LifecycleRejectionRequiresDecision = "lifecycle_rejection_requires_decision";

    /// <summary>Archiving needs a person: a backoffice user, or an API user with an acting-user token.</summary>
    public const string ArchiveRequiresActingUser = "archive_requires_acting_user";

    /// <summary>Only an owner or a functional manager can archive.</summary>
    public const string ArchiveNotOwner = "archive_not_owner";

    /// <summary>Only a published document can be archived.</summary>
    public const string ArchiveRequiresPublished = "archive_requires_published";

    /// <summary>The document is archived already.</summary>
    public const string ArchiveAlreadyArchived = "archive_already_archived";

    /// <summary>The document type has no lifecycle (not gated, or no status or archive date property).</summary>
    public const string ArchiveNotSupported = "archive_not_supported";

    /// <summary>Another rule cancelled the archive.</summary>
    public const string ArchiveCancelled = "archive_cancelled";

    private static readonly Dictionary<string, (string En, string Nl)> Texts = new(StringComparer.Ordinal)
    {
        [ApprovalRejectionRequiresFeedback] = (
            "A rejection needs feedback for the submitter: send a comment or findings.",
            "Een afkeuring heeft feedback voor de indiener nodig: stuur een toelichting of bevindingen mee."),
        [ApprovalCancelled] = (
            "The decision was not recorded: another rule refused to change the lifecycle status.",
            "De beslissing is niet vastgelegd: een andere regel weigerde de levenscyclusstatus te wijzigen."),
        [PublicationArchived] = (
            "\"{0}\" is archived and cannot be published again.",
            "\"{0}\" is gearchiveerd en kan niet opnieuw worden gepubliceerd."),
        [LifecycleArchiveRequiresRoute] = (
            "The archive status and archive date of \"{0}\" can only be set by archiving it.",
            "De archiefstatus en archiefdatum van \"{0}\" worden alleen gezet door te archiveren."),
        [LifecycleRejectionRequiresDecision] = (
            "The status of \"{0}\" becomes rejected only through a recorded rejection with feedback.",
            "De status van \"{0}\" wordt alleen afgekeurd via een vastgelegde afkeuring met feedback."),
        [ArchiveRequiresActingUser] = (
            "Archiving must name the person who confirms it: send an acting-user token.",
            "Archiveren moet de persoon noemen die het bevestigt: stuur een acting-user-token mee."),
        [ArchiveNotOwner] = (
            "Only an owner or a functional manager can archive \"{0}\".",
            "Alleen een eigenaar of functioneel beheerder kan \"{0}\" archiveren."),
        [ArchiveRequiresPublished] = (
            "Only a published material can be archived; \"{0}\" is not published.",
            "Alleen een gepubliceerd materiaal kan worden gearchiveerd; \"{0}\" is niet gepubliceerd."),
        [ArchiveAlreadyArchived] = ("\"{0}\" is archived already.", "\"{0}\" is al gearchiveerd."),
        [ArchiveNotSupported] = (
            "\"{0}\" has no lifecycle that can be archived.",
            "\"{0}\" heeft geen levenscyclus die kan worden gearchiveerd."),
        [ArchiveCancelled] = (
            "Archiving \"{0}\" was refused by another rule.",
            "Archiveren van \"{0}\" werd door een andere regel geweigerd."),
        [ApprovalChannelNotAllowed] = ("Decisions require the app or api acting-user channel.", "Beslissingen vereisen acting-user-kanaal app of api."),
        [ApprovalOwnMaterial] = ("An owner cannot approve their own material.", "Een eigenaar kan eigen materiaal niet goedkeuren."),
        [PublicationRequiresApproval] = (
            "Publishing requires an approved quality check for this version of \"{0}\".",
            "Publicatie vereist een goedgekeurde kwaliteitscheck voor deze versie van \"{0}\"."),
        [ApprovalRequiresReviewer] = (
            "Only a reviewer can approve or reject a version.",
            "Alleen een beoordelaar kan een versie goedkeuren of afkeuren."),
        [ApprovalRequiresActingUser] = (
            "An approval must name the reviewer: send an acting-user token for the person who decides.",
            "Een goedkeuring moet de beoordelaar noemen: stuur een acting-user-token mee voor de persoon die beslist."),
        [ApprovalVersionChanged] = (
            "The document changed after it was opened for review; review the current version.",
            "Het document is gewijzigd nadat het voor beoordeling werd geopend; beoordeel de huidige versie."),
        [ApprovalInvalidDecision] = (
            "The decision must be \"approved\" or \"rejected\".",
            "De beslissing moet \"approved\" of \"rejected\" zijn."),
    };

    /// <summary>The text for <paramref name="code"/>: Dutch when <paramref name="culture"/> is Dutch, otherwise English.</summary>
    public static string Text(string code, CultureInfo? culture, params object?[] args)
    {
        var (en, nl) = Texts[code];
        var dutch = (culture ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName == "nl";
        return string.Format(CultureInfo.InvariantCulture, dutch ? nl : en, args);
    }

    /// <summary>Culture for an Umbraco user language such as <c>nl-NL</c> or <c>en-US</c>; null when unknown.</summary>
    public static CultureInfo? CultureOf(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        try
        {
            return CultureInfo.GetCultureInfo(language);
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }
}
