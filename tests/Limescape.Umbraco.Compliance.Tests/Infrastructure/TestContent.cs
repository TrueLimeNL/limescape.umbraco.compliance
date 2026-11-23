using System.Collections.Concurrent;
using Limescape.Umbraco.Compliance.Retention;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;

namespace Limescape.Umbraco.Compliance.Tests.Infrastructure;

/// <summary>
/// Creates document types and documents in the test site. Each test makes its own type, so assignments of one
/// test never match items of another.
/// </summary>
internal static class TestContent
{
    public const string ArchivedAt = "archivedAt";
    public const string Category = "category";
    public const string ContractEnd = "contractEnd";

    /// <summary>Alias of the media picker property of <see cref="CreateDocumentTypeWithElementsAsync"/>; the package default.</summary>
    public const string Elements = "elements";

    /// <summary>A document type with the properties archivedAt and contractEnd (date) and category (text).</summary>
    public static async Task<IContentType> CreateDocumentTypeAsync(UmbracoTestSite site, string name)
    {
        var dataTypes = (await site.Services.GetRequiredService<IDataTypeService>().GetAllAsync()).ToList();
        var date = dataTypes.FirstOrDefault(d => d.EditorAlias == Constants.PropertyEditors.Aliases.DateTime)
            ?? throw new InvalidOperationException("No date picker data type in the test site.");
        var text = dataTypes.First(d => d.EditorAlias == Constants.PropertyEditors.Aliases.TextBox);
        var shortStringHelper = site.Services.GetRequiredService<IShortStringHelper>();

        var alias = "t" + Guid.NewGuid().ToString("N")[..12];
        var contentType = new ContentType(shortStringHelper, -1) { Alias = alias, Name = name, AllowedAsRoot = true };
        contentType.AddPropertyType(new PropertyType(shortStringHelper, date, ArchivedAt) { Name = "Archived at" });
        contentType.AddPropertyType(new PropertyType(shortStringHelper, date, ContractEnd) { Name = "Contract end" });
        contentType.AddPropertyType(new PropertyType(shortStringHelper, text, Category) { Name = "Category" });

        var result = await site.Services.GetRequiredService<IContentTypeService>().CreateAsync(contentType, Constants.Security.SuperUserKey);
        Assert.True(result.Success, $"creating document type failed: {result.Result}");
        return contentType;
    }

    /// <summary>A document type with a multiple media picker property <see cref="Elements"/>, like a learning material.</summary>
    public static async Task<IContentType> CreateDocumentTypeWithElementsAsync(UmbracoTestSite site, string name)
    {
        var picker = await site.Services.GetRequiredService<IDataTypeService>().GetAsync(Constants.DataTypes.Guids.MediaPicker3MultipleGuid)
            ?? throw new InvalidOperationException("No multiple media picker data type in the test site.");
        var shortStringHelper = site.Services.GetRequiredService<IShortStringHelper>();
        var contentType = new ContentType(shortStringHelper, -1) { Alias = "t" + Guid.NewGuid().ToString("N")[..12], Name = name, AllowedAsRoot = true };
        contentType.AddPropertyType(new PropertyType(shortStringHelper, picker, Elements) { Name = "Elements" });

        var result = await site.Services.GetRequiredService<IContentTypeService>().CreateAsync(contentType, Constants.Security.SuperUserKey);
        Assert.True(result.Success, $"creating document type failed: {result.Result}");
        return contentType;
    }

    /// <summary>The stored value of a media picker that holds <paramref name="media"/>.</summary>
    public static string Picked(params IMedia[] media) =>
        System.Text.Json.JsonSerializer.Serialize(media.Select(m => new { key = Guid.NewGuid(), mediaKey = m.Key }));

    /// <summary>Creates and saves a document; <paramref name="values"/> are property values.</summary>
    public static IContent CreateDocument(UmbracoTestSite site, IContentType contentType, string name, params (string Alias, object? Value)[] values)
    {
        var contentService = site.Services.GetRequiredService<IContentService>();
        var content = contentService.Create(name, Constants.System.Root, contentType);
        foreach (var (alias, value) in values)
        {
            content.SetValue(alias, value);
        }

        var saved = contentService.Save(content);
        Assert.True(saved.Success, $"saving document failed: {saved.Result}");
        return content;
    }

    /// <summary>Sets property values on a document and saves it.</summary>
    public static IContent Update(UmbracoTestSite site, Guid key, params (string Alias, object? Value)[] values)
    {
        var contentService = site.Services.GetRequiredService<IContentService>();
        var content = contentService.GetById(key)!;
        foreach (var (alias, value) in values)
        {
            content.SetValue(alias, value);
        }

        Assert.True(contentService.Save(content).Success);
        return contentService.GetById(key)!;
    }
}

/// <summary>Records every <see cref="RetentionEndedNotification"/> raised in the test site.</summary>
public sealed class RetentionEndedRecorder : INotificationHandler<RetentionEndedNotification>
{
    public static ConcurrentQueue<RetentionEndedNotification> Received { get; } = new();

    public void Handle(RetentionEndedNotification notification) => Received.Enqueue(notification);
}

/// <summary>Registers the test-only handlers; Umbraco discovers composers in the test assembly.</summary>
public sealed class TestComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder) => builder.AddNotificationHandler<RetentionEndedNotification, RetentionEndedRecorder>()
        .AddNotificationHandler<global::Umbraco.Cms.Core.Notifications.ContentUnpublishingNotification, CancelArchiveUnpublishHandler>();
}

/// <summary>Only cancels unpublishing for the unique document armed by a rollback test.</summary>
public sealed class CancelArchiveUnpublishHandler : INotificationHandler<global::Umbraco.Cms.Core.Notifications.ContentUnpublishingNotification>
{
    public static ConcurrentDictionary<Guid, bool> Targets { get; } = new();

    public void Handle(global::Umbraco.Cms.Core.Notifications.ContentUnpublishingNotification notification)
    {
        foreach (var content in notification.UnpublishedEntities)
        {
            if (Targets.ContainsKey(content.Key))
            {
                Targets[content.Key] = true;
                notification.CancelOperation(new EventMessage("test_unpublish_cancelled", "Cancelled by rollback test", EventMessageType.Error));
            }
        }
    }
}