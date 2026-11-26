using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ShineMusic.Api.Controllers;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using SqlSugar;
using Xunit;

namespace ShineMusic.Tests.Integration;

/// <summary>The legacy administrator import API must never substitute for opted-in public review.</summary>
public sealed class AdminImportUserUploadIsolationTests : IDisposable
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private readonly SqlSugarClient db = new(new ConnectionConfig
    {
        ConnectionString = "Data Source=:memory:", DbType = DbType.Sqlite,
        IsAutoCloseConnection = false, InitKeyType = InitKeyType.Attribute,
        ConfigureExternalServices = new ConfigureExternalServices
        {
            EntityNameService = (_, entity) => entity.DbTableName = JsonNamingPolicy.SnakeCaseLower.ConvertName(entity.DbTableName),
            EntityService = (property, column) => column.DbColumnName = JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name)
        }
    });
    private readonly IOptions<SongRequestFulfillmentOptions> fulfillment = Options.Create(new SongRequestFulfillmentOptions());
    private readonly Guid actor = Guid.NewGuid();

    public AdminImportUserUploadIsolationTests() => db.CodeFirst.InitTables<LibraryImportBatch, LibraryImportItem, Track>();

    [Fact]
    public async Task Lists_and_batch_details_use_item_origin_instead_of_batch_uploader()
    {
        var mixed = Batch();
        var regular = Item(mixed.Id, false);
        Item(mixed.Id, true);
        var privateOnly = Batch();
        Item(privateOnly.Id, true);
        var controller = Controller();

        var batches = Json(await controller.ListBatches(db, fulfillment, null));
        Assert.Equal(1, batches.GetProperty("total").GetInt32());
        var batch = Assert.Single(batches.GetProperty("items").EnumerateArray());
        Assert.Equal(mixed.Id, batch.GetProperty("id").GetGuid());
        Assert.Equal(regular.Id, Assert.Single(batch.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        var items = Json(await controller.ListItems(db, fulfillment, null, null, null, null));
        Assert.Equal(1, items.GetProperty("total").GetInt32());
        Assert.Equal(regular.Id, Assert.Single(items.GetProperty("items").EnumerateArray()).GetProperty("item").GetProperty("id").GetGuid());
        var detail = Json(await controller.GetBatch(mixed.Id, db, fulfillment, default));
        Assert.Equal(regular.Id, Assert.Single(detail.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(404, Status(await controller.GetBatch(privateOnly.Id, db, fulfillment, default)));
        Assert.Equal(200, Status(await controller.GetItem(regular.Id, db, fulfillment, default)));
    }

    [Theory]
    [InlineData("get")]
    [InlineData("preview")]
    [InlineData("update")]
    [InlineData("enrich")]
    [InlineData("lyrics")]
    [InlineData("publish")]
    [InlineData("unlink")]
    [InlineData("delete")]
    [InlineData("dismiss")]
    public async Task User_upload_cannot_be_read_or_mutated_through_legacy_item_endpoints(string action)
    {
        var item = Item(Batch().Id, true);
        var track = new Track { Title = "Private", OwnerUserId = actor };
        db.Insertable(track).ExecuteCommand();
        item.Status = LibraryImportItemStatuses.PendingPublication;
        item.PublishedTrackId = track.Id;
        item.Title = "Changed by legacy review";
        db.Updateable(item).ExecuteCommand();
        var controller = Controller();

        // Null collaborators prove rejection happens before file access, enrichment or publication.
        var response = action switch
        {
            "get" => await controller.GetItem(item.Id, db, fulfillment, default),
            "preview" => await controller.PreviewItem(item.Id, db, null!, null!, default),
            "update" => await controller.UpdateItem(item.Id, new(null, null, null, null, null, null, null, null, null), db, fulfillment, default),
            "enrich" => await controller.EnrichItem(item.Id, db, null!, null!, null!, fulfillment, default),
            "lyrics" => await controller.UploadLyricsFile(item.Id, db, fulfillment, default),
            "publish" => await controller.PublishItem(item.Id, false, null, db, null!, default),
            "unlink" => await controller.UnlinkSongRequest(item.Id, db, fulfillment, default),
            "delete" => await controller.DeleteItem(item.Id, db, null!, default),
            _ => await controller.DismissItem(item.Id, db, default)
        };

        Assert.Equal(404, Status(response));
        Assert.Null(db.Queryable<Track>().Single().PublishedAt);
        Assert.Equal("Private", db.Queryable<Track>().Single().Title);
        var unchanged = db.Queryable<LibraryImportItem>().Single();
        Assert.Equal(LibraryImportItemStatuses.PendingPublication, unchanged.Status);
        Assert.False(unchanged.IsDeleted);
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task Bulk_review_reports_user_upload_as_unavailable_without_changing_it(string action)
    {
        var item = Item(Batch().Id, true);
        var result = Assert.IsType<BulkReviewResultDto>(Assert.IsAssignableFrom<IValueHttpResult>(
            await Controller().BulkReviewItems(new([item.Id], action, "review reason"), db, null!, default)).Value);
        Assert.Equal(0, result.Success);
        Assert.False(Assert.Single(result.Results).Success);
        Assert.Equal(LibraryImportItemStatuses.Uploaded, db.Queryable<LibraryImportItem>().Single().Status);
    }

    private LibraryImportBatch Batch()
    {
        var batch = new LibraryImportBatch { CreatedByUserId = actor };
        db.Insertable(batch).ExecuteCommand();
        return batch;
    }

    private LibraryImportItem Item(Guid batchId, bool userUpload)
    {
        var item = new LibraryImportItem
        {
            BatchId = batchId, IsUserUpload = userUpload, DisplayName = "sample.flac",
            SourceStorageKey = "uploads/sample.flac", ContentHash = Guid.NewGuid().ToString("N")
        };
        db.Insertable(item).ExecuteCommand();
        return item;
    }

    private LibraryImportController Controller() => new()
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", actor.ToString())], "test"))
            }
        }
    };

    private static JsonElement Json(IResult response) => JsonSerializer.SerializeToElement(
        Assert.IsAssignableFrom<IValueHttpResult>(response).Value, WebJson);
    private static int? Status(IResult response) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(response).StatusCode;
    public void Dispose() => db.Dispose();
}
