using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ShineMusic.Api.Application;
using ShineMusic.Api.Controllers;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using SqlSugar;
using Xunit;

namespace ShineMusic.Tests.Integration;

public sealed class UserUploadStatusTests : IDisposable
{
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

    public UserUploadStatusTests() => db.CodeFirst.InitTables<LibraryImportBatch, LibraryImportItem>();

    [Fact]
    public async Task Status_is_private_and_does_not_expose_admin_imports()
    {
        var owner = Guid.NewGuid();
        var batch = Seed(owner);
        Assert.Equal(401, Status(await Controller(null).GetUploadBatch(batch.Id, db, default)));
        Assert.Equal(404, Status(await Controller(Guid.NewGuid()).GetUploadBatch(batch.Id, db, default)));
        Assert.Equal(404, Status(await Controller(owner).GetUploadBatch(Guid.NewGuid(), db, default)));
        db.Updateable<LibraryImportItem>().SetColumns(item => item.IsUserUpload == false)
            .Where(item => item.BatchId == batch.Id).ExecuteCommand();
        Assert.Equal(404, Status(await Controller(owner).GetUploadBatch(batch.Id, db, default)));
    }

    [Fact]
    public async Task Owner_sees_processing_failure_and_playable_track_without_internal_details()
    {
        var owner = Guid.NewGuid();
        var batch = Seed(owner);
        var failed = db.Queryable<LibraryImportItem>().Single();
        failed.Status = LibraryImportItemStatuses.ProcessingFailed;
        failed.ErrorCode = ErrorCodes.MediaParseFailed;
        failed.ErrorMessage = "decoder failed at C:\\private\\secret.flac (token=secret)";
        db.Updateable(failed).ExecuteCommand();
        var trackId = Guid.NewGuid();
        var ready = new LibraryImportItem
        {
            BatchId = batch.Id, DisplayName = "ready.flac", SourceStorageKey = "uploads/ready.flac",
            ContentHash = "ready", IsUserUpload = true, Status = LibraryImportItemStatuses.PendingPublication,
            PublishedTrackId = trackId
        };
        db.Insertable(ready).ExecuteCommand();
        var response = Assert.IsType<UserUploadBatchDto>(Assert.IsAssignableFrom<IValueHttpResult>(
            await Controller(owner).GetUploadBatch(batch.Id, db, default)).Value);
        Assert.Equal(2, response.Items.Count);
        var failure = Assert.Single(response.Items, item => item.ItemId == failed.Id);
        Assert.Equal(ErrorCodes.MediaParseFailed, failure.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(failure.ErrorMessage));
        Assert.DoesNotContain("secret", failure.ErrorMessage);
        Assert.Equal(DateTimeKind.Utc, failure.UpdatedAt.Kind);
        Assert.Equal(trackId, Assert.Single(response.Items, item => item.ItemId == ready.Id).TrackId);
        var admin = LibraryImportDtoMapper.ToDto(failed, null, new SongRequestFulfillmentOptions());
        Assert.Equal(failure.ErrorMessage, admin.Transcode.ErrorMessage);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(admin));
    }

    private LibraryImportBatch Seed(Guid owner)
    {
        var batch = new LibraryImportBatch { CreatedByUserId = owner };
        db.Insertable(batch).ExecuteCommand();
        db.Insertable(new LibraryImportItem
        {
            BatchId = batch.Id, DisplayName = "uploaded.flac", SourceStorageKey = "uploads/test.flac",
            ContentHash = "test", IsUserUpload = true
        }).ExecuteCommand();
        return batch;
    }

    private static UserUploadController Controller(Guid? userId) => new()
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = userId.HasValue
                    ? new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId.Value.ToString())], "test"))
                    : new ClaimsPrincipal(new ClaimsIdentity())
            }
        }
    };

    private static int? Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode;
    public void Dispose() => db.Dispose();
}
