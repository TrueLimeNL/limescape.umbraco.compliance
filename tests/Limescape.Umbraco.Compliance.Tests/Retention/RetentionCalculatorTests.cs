using Limescape.Umbraco.Compliance.Retention;

namespace Limescape.Umbraco.Compliance.Tests.Retention;

/// <summary>
/// Calculation rules: start events, longest period wins, recalculation on a new event, never shorten
/// without confirmation, item override and category matching. Pure, no Umbraco site.
/// </summary>
public sealed class RetentionCalculatorTests
{
    private static readonly Guid VideoType = Guid.NewGuid();
    private static readonly Guid DocumentType = Guid.NewGuid();
    private static readonly DateTime Now = new(2027, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly RetentionClass SevenYearsAfterArchive =
        Class("Teaching material, 7 years after archive", new(7, 0, 0), RetentionStartEvent.Archived);

    private static readonly RetentionClass TenYearsAfterLastModified =
        Class("Assessment material, 10 years after last modification", new(10, 0, 0), RetentionStartEvent.LastModified);

    [Fact]
    public void Archived_on_15_January_2027_with_seven_years_runs_until_15_January_2034()
    {
        var video = Subject(VideoType) with { Archived = Utc(2027, 1, 15) };

        var result = Calculate(video, [TypeAssignment(SevenYearsAfterArchive, VideoType)], SevenYearsAfterArchive);

        Assert.Equal(Utc(2034, 1, 15), result.Until);
        Assert.Equal(Utc(2027, 1, 15), Assert.Single(result.Rules).StartDate);
        Assert.Equal(RetentionStatus.Active, result.StatusAt(Now));
    }

    [Theory]
    [InlineData(RetentionStartEvent.Published, 2026, 3, 1)]
    [InlineData(RetentionStartEvent.LastModified, 2026, 5, 20)]
    [InlineData(RetentionStartEvent.Archived, 2026, 9, 30)]
    [InlineData(RetentionStartEvent.DateProperty, 2025, 12, 31)]
    public void Each_start_event_takes_its_own_date(RetentionStartEvent startEvent, int year, int month, int day)
    {
        var cls = Class("One year", new(1, 0, 0), startEvent) with
        {
            StartPropertyAlias = startEvent == RetentionStartEvent.DateProperty ? "contractEnd" : null,
        };
        var item = Subject(DocumentType, lastModified: Utc(2026, 5, 20)) with
        {
            Published = Utc(2026, 3, 1),
            Archived = Utc(2026, 9, 30),
            PropertyValues = alias => alias == "contractEnd" ? ["2025-12-31"] : [],
        };

        var result = Calculate(item, [TypeAssignment(cls, DocumentType)], cls);

        Assert.Equal(new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc), result.Rules[0].StartDate);
        Assert.Equal(new DateTime(year + 1, month, day, 0, 0, 0, DateTimeKind.Utc), result.Until);
    }

    [Fact]
    public void Last_modification_class_is_recalculated_when_the_item_is_modified()
    {
        var assignment = TypeAssignment(TenYearsAfterLastModified, DocumentType);
        var before = Calculate(Subject(DocumentType, lastModified: Utc(2027, 2, 1)), [assignment], TenYearsAfterLastModified);

        var after = Calculate(Subject(DocumentType, lastModified: Utc(2027, 5, 3)), [assignment], TenYearsAfterLastModified);
        var merged = RetentionCalculator.Merge(before.IsOpenEnded, before.Until, after, allowShortening: false);

        Assert.Equal(Utc(2037, 2, 1), before.Until);
        Assert.Equal(Utc(2037, 5, 3), merged.Until);
        Assert.False(merged.ShorteningRefused);
    }

    [Fact]
    public void Longest_period_wins_and_both_classes_are_shown_with_their_rule()
    {
        var category = new RetentionAssignment(2, TenYearsAfterLastModified.Key, RetentionAssignmentTarget.PropertyValue)
        {
            PropertyAlias = "category",
            PropertyValue = "Assessment",
        };
        var item = Subject(VideoType, lastModified: Utc(2027, 1, 10)) with
        {
            Archived = Utc(2027, 1, 15),
            PropertyValues = alias => alias == "category" ? ["assessment"] : [],
        };

        var result = Calculate(item, [TypeAssignment(SevenYearsAfterArchive, VideoType), category], SevenYearsAfterArchive, TenYearsAfterLastModified);

        Assert.Equal(2, result.Rules.Count);
        Assert.Same(TenYearsAfterLastModified, result.Governing!.Class);
        Assert.Equal(Utc(2037, 1, 10), result.Until);
        Assert.Equal(RetentionAssignmentTarget.PropertyValue, result.Rules[0].Source);
        Assert.Equal(Utc(2034, 1, 15), result.Rules[1].Until);
        Assert.Equal(RetentionAssignmentTarget.ContentType, result.Rules[1].Source);
    }

    [Fact]
    public void A_class_whose_start_event_has_not_happened_keeps_the_period_open_ended_and_wins()
    {
        var item = Subject(VideoType, lastModified: Utc(2027, 1, 10));

        var result = Calculate(item, [TypeAssignment(SevenYearsAfterArchive, VideoType), TypeAssignment(TenYearsAfterLastModified, VideoType)], SevenYearsAfterArchive, TenYearsAfterLastModified);

        Assert.True(result.IsOpenEnded);
        Assert.Null(result.Until);
        Assert.Same(SevenYearsAfterArchive, result.Governing!.Class);
        Assert.True(result.Governing.IsPending);
        Assert.Equal(RetentionStatus.Pending, result.StatusAt(Now));
    }

    [Fact]
    public void No_assignment_means_no_retention()
    {
        var result = Calculate(Subject(DocumentType), [TypeAssignment(SevenYearsAfterArchive, VideoType)], SevenYearsAfterArchive);

        Assert.Empty(result.Rules);
        Assert.Equal(RetentionStatus.None, result.StatusAt(Now));
    }

    [Fact]
    public void Item_assignment_overrides_type_and_category_assignments()
    {
        var oneYear = Class("One year after last modification", new(1, 0, 0), RetentionStartEvent.LastModified);
        var item = Subject(VideoType, lastModified: Utc(2027, 1, 1));
        var assignments = new[]
        {
            TypeAssignment(TenYearsAfterLastModified, VideoType),
            new RetentionAssignment(3, oneYear.Key, RetentionAssignmentTarget.Item) { ItemKey = item.Key },
        };

        var result = Calculate(item, assignments, TenYearsAfterLastModified, oneYear);

        Assert.Same(oneYear, Assert.Single(result.Rules).Class);
        Assert.Equal(RetentionAssignmentTarget.Item, result.Rules[0].Source);
        Assert.Equal(Utc(2028, 1, 1), result.Until);
    }

    [Fact]
    public void Item_assignment_for_another_item_does_not_apply()
    {
        var oneYear = Class("One year", new(1, 0, 0), RetentionStartEvent.LastModified);
        var assignments = new[] { new RetentionAssignment(3, oneYear.Key, RetentionAssignmentTarget.Item) { ItemKey = Guid.NewGuid() } };

        Assert.Empty(Calculate(Subject(VideoType), assignments, oneYear).Rules);
    }

    [Fact]
    public void Category_assignment_can_be_limited_to_one_type()
    {
        var assignment = new RetentionAssignment(4, SevenYearsAfterArchive.Key, RetentionAssignmentTarget.PropertyValue)
        {
            ContentTypeKey = VideoType,
            PropertyAlias = "category",
            PropertyValue = "Teaching",
        };
        IEnumerable<object?> Values(string alias) => alias == "category" ? ["Teaching"] : [];

        Assert.Single(Calculate(Subject(VideoType) with { PropertyValues = Values }, [assignment], SevenYearsAfterArchive).Rules);
        Assert.Empty(Calculate(Subject(DocumentType) with { PropertyValues = Values }, [assignment], SevenYearsAfterArchive).Rules);
    }

    [Theory]
    [InlineData("[\"Video\",\"Teaching\"]")]
    [InlineData("Video, Teaching")]
    [InlineData("teaching")]
    public void Category_matches_inside_multi_value_properties(string stored)
    {
        Assert.True(RetentionCalculator.HasValue([stored], "Teaching"));
    }

    [Theory]
    [InlineData("[\"Teachings\"]")]
    [InlineData("Teach")]
    [InlineData("")]
    public void Category_does_not_match_partial_values(string stored)
    {
        Assert.False(RetentionCalculator.HasValue([stored], "Teaching"));
    }

    [Fact]
    public void A_class_assigned_twice_counts_once()
    {
        var item = Subject(VideoType) with { Archived = Utc(2027, 1, 15), PropertyValues = _ => ["Teaching"] };
        var assignments = new[]
        {
            TypeAssignment(SevenYearsAfterArchive, VideoType),
            new RetentionAssignment(5, SevenYearsAfterArchive.Key, RetentionAssignmentTarget.PropertyValue) { PropertyAlias = "category", PropertyValue = "Teaching" },
        };

        var rule = Assert.Single(Calculate(item, assignments, SevenYearsAfterArchive).Rules);
        Assert.Equal(RetentionAssignmentTarget.ContentType, rule.Source);
    }

    [Fact]
    public void Shorter_period_after_a_class_change_is_refused_without_confirmation()
    {
        var shortened = SevenYearsAfterArchive with { Duration = new(3, 0, 0) };
        var item = Subject(VideoType) with { Archived = Utc(2027, 1, 15) };
        var result = Calculate(item, [TypeAssignment(shortened, VideoType)], shortened);

        var refused = RetentionCalculator.Merge(false, Utc(2034, 1, 15), result, allowShortening: false);
        var confirmed = RetentionCalculator.Merge(false, Utc(2034, 1, 15), result, allowShortening: true);

        Assert.Equal(Utc(2034, 1, 15), refused.Until);
        Assert.True(refused.ShorteningRefused);
        Assert.Equal(Utc(2030, 1, 15), confirmed.Until);
        Assert.False(confirmed.ShorteningRefused);
    }

    [Fact]
    public void Removing_every_class_does_not_drop_a_stored_period_without_confirmation()
    {
        var refused = RetentionCalculator.Merge(false, Utc(2034, 1, 15), RetentionResult.None, allowShortening: false);

        Assert.Equal(Utc(2034, 1, 15), refused.Until);
        Assert.True(refused.ShorteningRefused);
    }

    [Fact]
    public void Longer_period_after_a_class_change_is_applied_directly()
    {
        var longer = SevenYearsAfterArchive with { Duration = new(9, 0, 0) };
        var result = Calculate(Subject(VideoType) with { Archived = Utc(2027, 1, 15) }, [TypeAssignment(longer, VideoType)], longer);

        var merged = RetentionCalculator.Merge(false, Utc(2034, 1, 15), result, allowShortening: false);

        Assert.Equal(Utc(2036, 1, 15), merged.Until);
        Assert.False(merged.ShorteningRefused);
    }

    [Fact]
    public void Open_ended_period_gets_its_end_date_when_the_start_event_happens()
    {
        var result = Calculate(Subject(VideoType) with { Archived = Utc(2027, 1, 15) }, [TypeAssignment(SevenYearsAfterArchive, VideoType)], SevenYearsAfterArchive);

        var merged = RetentionCalculator.Merge(true, null, result, allowShortening: false);

        Assert.False(merged.OpenEnded);
        Assert.Equal(Utc(2034, 1, 15), merged.Until);
        Assert.False(merged.ShorteningRefused);
    }

    [Fact]
    public void Open_ended_period_is_not_dropped_without_confirmation()
    {
        var refused = RetentionCalculator.Merge(true, null, RetentionResult.None, allowShortening: false);
        var confirmed = RetentionCalculator.Merge(true, null, RetentionResult.None, allowShortening: true);

        Assert.True(refused.OpenEnded);
        Assert.True(refused.ShorteningRefused);
        Assert.False(confirmed.OpenEnded);
        Assert.Null(confirmed.Until);
    }

    [Fact]
    public void Duration_adds_years_then_months_then_days_and_clamps_month_ends()
    {
        Assert.Equal(Utc(2027, 2, 28), new RetentionDuration(0, 1, 0).AddTo(Utc(2027, 1, 31)));
        Assert.Equal(Utc(2028, 2, 29), new RetentionDuration(1, 0, 0).AddTo(Utc(2027, 2, 28)).AddDays(1));
        Assert.Equal(Utc(2034, 3, 25), new RetentionDuration(7, 2, 10).AddTo(Utc(2027, 1, 15)));
    }

    [Fact]
    public void Ended_when_the_end_date_has_passed()
    {
        Assert.Equal(RetentionStatus.Ended, RetentionCalculator.Status(true, false, Utc(2027, 5, 31), Now));
        Assert.Equal(RetentionStatus.Ended, RetentionCalculator.Status(true, false, Now, Now));
        Assert.Equal(RetentionStatus.Active, RetentionCalculator.Status(true, false, Now.AddSeconds(1), Now));
    }

    [Theory]
    [InlineData("2027-01-15", 2027, 1, 15)]
    [InlineData("2027-01-15T10:00:00+02:00", 2027, 1, 15)]
    [InlineData("{\"date\":\"2027-01-15T00:00:00+00:00\",\"timeZone\":\"Europe/Amsterdam\"}", 2027, 1, 15)]
    public void Date_values_are_parsed_from_stored_formats(string stored, int year, int month, int day)
    {
        Assert.Equal(new DateOnly(year, month, day), DateOnly.FromDateTime(RetentionCalculator.ParseDate(stored)!.Value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a date")]
    [InlineData("{\"timeZone\":\"UTC\"}")]
    public void Missing_or_unreadable_dates_are_null(string? stored)
    {
        Assert.Null(RetentionCalculator.ParseDate(stored));
    }

    private static RetentionResult Calculate(RetentionSubject subject, IEnumerable<RetentionAssignment> assignments, params RetentionClass[] classes) =>
        RetentionCalculator.Calculate(subject, RetentionCalculator.Applicable(subject, assignments, classes.ToDictionary(c => c.Key)));

    private static RetentionSubject Subject(Guid contentTypeKey, DateTime? lastModified = null) =>
        new(Guid.NewGuid(), contentTypeKey, lastModified ?? Utc(2027, 1, 1));

    private static RetentionClass Class(string name, RetentionDuration duration, RetentionStartEvent startEvent) =>
        new(Guid.NewGuid(), name, duration, startEvent, RetentionEndAction.Notify);

    private static RetentionAssignment TypeAssignment(RetentionClass cls, Guid contentTypeKey) =>
        new(1, cls.Key, RetentionAssignmentTarget.ContentType) { ContentTypeKey = contentTypeKey };

    private static DateTime Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);
}
