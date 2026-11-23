using Limescape.Umbraco.Compliance.PublicationGate;
using Limescape.Umbraco.Compliance.Tests.Infrastructure;

namespace Limescape.Umbraco.Compliance.Tests.PublicationGate;

public sealed class ContentVersionHasherTests(UmbracoTestSite site)
{
    private IContentVersionHasher Hasher => site.Services.GetRequiredService<IContentVersionHasher>();

    [Fact]
    public async Task Same_draft_gives_the_same_hash_after_reload_and_after_saving_without_changes()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();
        var first = Hasher.ComputeHash(bed.Reload(draft.Key));

        bed.ContentService.Save(bed.Reload(draft.Key));

        Assert.Equal(first, Hasher.ComputeHash(bed.Reload(draft.Key)));
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    [Fact]
    public async Task Changing_a_property_or_the_name_changes_the_hash()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();
        var original = Hasher.ComputeHash(bed.Reload(draft.Key));

        var changedValue = bed.Reload(draft.Key);
        changedValue.SetValue("title", "Other title");
        var changedName = bed.Reload(draft.Key);
        changedName.Name = "Other name";

        Assert.NotEqual(original, Hasher.ComputeHash(changedValue));
        Assert.NotEqual(original, Hasher.ComputeHash(changedName));
    }

    [Fact]
    public async Task Empty_and_absent_values_are_the_same_draft()
    {
        var bed = await GateTestBed.GetAsync(site);
        var withEmpty = bed.Reload(bed.CreateDraft().Key);
        withEmpty.SetValue("title", string.Empty);
        var withNull = bed.Reload(withEmpty.Key);
        withNull.SetValue("title", null);

        Assert.Equal(Hasher.ComputeHash(withEmpty), Hasher.ComputeHash(withNull));
    }
}
