using System.Globalization;

namespace Limescape.Umbraco.Compliance.DeletionBlock;

/// <summary>
/// Localized messages for a refused deletion, in Dutch and English. Any culture other than Dutch gets English.
/// The texts live in code so they are the same in the backoffice notification, the API problem detail and tests.
/// </summary>
public static class DeletionBlockMessages
{
    /// <summary>At most this many blocked items are named in one message; the rest are counted.</summary>
    public const int MaxNamed = 5;

    /// <summary>Notification heading.</summary>
    public static string Category(CultureInfo culture) => IsDutch(culture) ? "Verwijderen geblokkeerd" : "Deletion blocked";

    /// <summary>One line per blocked item (at most <see cref="MaxNamed"/>), plus a count of the rest.</summary>
    public static IReadOnlyList<string> Lines(IReadOnlyList<DeletionBlock> blocks, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(culture);
        var lines = blocks.Take(MaxNamed).Select(b => Line(b, culture)).ToList();
        if (blocks.Count > MaxNamed)
        {
            var rest = blocks.Count - MaxNamed;
            lines.Add(IsDutch(culture)
                ? $"En nog {rest} ander(e) item(s) met een bewaartermijn of legal hold."
                : $"And {rest} more item(s) under retention or legal hold.");
        }

        return lines;
    }

    /// <summary>Summary when emptying the recycle bin removed the items that may go and kept the rest.</summary>
    public static string PartialEmpty(int deleted, int kept, CultureInfo culture) => IsDutch(culture)
        ? $"De prullenbak is gedeeltelijk geleegd: {deleted} item(s) verwijderd, {kept} item(s) blijven staan."
        : $"The recycle bin was emptied partially: {deleted} item(s) deleted, {kept} item(s) kept.";

    /// <summary>All lines as one text, for the API problem detail.</summary>
    public static string Detail(IReadOnlyList<DeletionBlock> blocks, CultureInfo culture) => string.Join(" ", Lines(blocks, culture));

    /// <summary>
    /// The message for one blocked item, for example "Verwijderen niet toegestaan: bewaartermijn loopt tot 15 januari 2034 (Onderwijsmateriaal)."
    /// When the block comes from a descendant, its name is added.
    /// </summary>
    public static string Line(DeletionBlock block, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(culture);
        var nl = IsDutch(culture);
        var formatCulture = nl ? CultureInfo.GetCultureInfo("nl-NL") : CultureInfo.GetCultureInfo("en-GB");
        var cls = string.IsNullOrWhiteSpace(block.RetentionClass) ? string.Empty : $" ({block.RetentionClass})";

        var text = block.Reason switch
        {
            DeletionBlockReason.LegalHold => nl
                ? "Verwijderen niet toegestaan: er staat een legal hold op dit item."
                : "Deletion not allowed: the item is under legal hold.",
            DeletionBlockReason.RetentionOpenEnded => nl
                ? $"Verwijderen niet toegestaan: de bewaartermijn is nog niet gestart en loopt voor onbepaalde tijd{cls}."
                : $"Deletion not allowed: the retention period has not started yet and runs indefinitely{cls}.",
            _ => nl
                ? $"Verwijderen niet toegestaan: bewaartermijn loopt tot {FormatDate(block.RetentionUntil, formatCulture)}{cls}."
                : $"Deletion not allowed: the retention period runs until {FormatDate(block.RetentionUntil, formatCulture)}{cls}.",
        };

        if (block.ViaItemKey is not null)
        {
            text += nl
                ? $" Dit geldt voor het onderliggende item \"{block.Name}\"."
                : $" This applies to the descendant \"{block.Name}\".";
        }

        return text;
    }

    /// <summary>
    /// Culture-independent description for the audit log, for example <c>retention until 2034-01-15T00:00:00.0000000Z (Teaching material)</c>.
    /// </summary>
    public static string Invariant(DeletionBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        var cls = string.IsNullOrWhiteSpace(block.RetentionClass) ? string.Empty : $" ({block.RetentionClass})";
        var reason = block.Reason switch
        {
            DeletionBlockReason.LegalHold => "legalHold",
            DeletionBlockReason.RetentionOpenEnded => "retention open-ended" + cls,
            _ => $"retention until {block.RetentionUntil?.ToString("O", CultureInfo.InvariantCulture)}{cls}",
        };
        return block.ViaItemKey is { } via ? $"{reason}; attempted on ancestor {via}" : reason;
    }

    private static string FormatDate(DateTime? date, CultureInfo culture) =>
        date is { } d ? d.ToString("d MMMM yyyy", culture) : "?";

    private static bool IsDutch(CultureInfo culture) => culture.TwoLetterISOLanguageName == "nl";
}
