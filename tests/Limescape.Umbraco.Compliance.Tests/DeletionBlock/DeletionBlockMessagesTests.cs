using System.Globalization;
using Limescape.Umbraco.Compliance.DeletionBlock;

namespace Limescape.Umbraco.Compliance.Tests.DeletionBlock;

/// <summary>
/// The localized messages (nl, en) for a refused deletion.
/// </summary>
public sealed class DeletionBlockMessagesTests
{
    private static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private static readonly Limescape.Umbraco.Compliance.DeletionBlock.DeletionBlock Retained =
        new(Guid.NewGuid(), "document", "Les 1", DeletionBlockReason.Retention)
        {
            RetentionUntil = new DateTime(2034, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            RetentionClass = "Onderwijsmateriaal",
        };

    [Fact]
    public void Retention_message_names_end_date_and_class_in_dutch() =>
        Assert.Equal("Verwijderen niet toegestaan: bewaartermijn loopt tot 15 januari 2034 (Onderwijsmateriaal).", DeletionBlockMessages.Line(Retained, Dutch));

    [Fact]
    public void Retention_message_names_end_date_and_class_in_english() =>
        Assert.Equal("Deletion not allowed: the retention period runs until 15 January 2034 (Onderwijsmateriaal).", DeletionBlockMessages.Line(Retained, English));

    [Fact]
    public void Other_languages_get_english() =>
        Assert.StartsWith("Deletion not allowed", DeletionBlockMessages.Line(Retained, CultureInfo.GetCultureInfo("de-DE")), StringComparison.Ordinal);

    [Fact]
    public void Legal_hold_and_open_ended_have_their_own_message()
    {
        var hold = Retained with { Reason = DeletionBlockReason.LegalHold };
        var open = Retained with { Reason = DeletionBlockReason.RetentionOpenEnded, RetentionUntil = null };

        Assert.Equal("Verwijderen niet toegestaan: er staat een legal hold op dit item.", DeletionBlockMessages.Line(hold, Dutch));
        Assert.Equal("Deletion not allowed: the retention period has not started yet and runs indefinitely (Onderwijsmateriaal).", DeletionBlockMessages.Line(open, English));
    }

    [Fact]
    public void Descendant_is_named()
    {
        var viaParent = Retained with { ViaItemKey = Guid.NewGuid() };

        Assert.EndsWith("Dit geldt voor het onderliggende item \"Les 1\".", DeletionBlockMessages.Line(viaParent, Dutch), StringComparison.Ordinal);
    }

    [Fact]
    public void At_most_five_items_are_named_and_the_rest_counted()
    {
        var blocks = Enumerable.Range(0, 8).Select(_ => Retained).ToList();

        var lines = DeletionBlockMessages.Lines(blocks, English);

        Assert.Equal(DeletionBlockMessages.MaxNamed + 1, lines.Count);
        Assert.Equal("And 3 more item(s) under retention or legal hold.", lines[^1]);
    }

    [Fact]
    public void Audit_text_is_culture_independent() =>
        Assert.Equal("retention until 2034-01-15T00:00:00.0000000Z (Onderwijsmateriaal)", DeletionBlockMessages.Invariant(Retained));
}
