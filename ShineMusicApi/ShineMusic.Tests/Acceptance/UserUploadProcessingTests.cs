using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Repository.Data;
using ShineMusic.Service;
using ShineMusic.Service.Messaging;
using Xunit;

namespace ShineMusic.Tests.Acceptance;

[Collection("Source download database acceptance")]
public sealed class UserUploadProcessingTests
{
    [Fact]
    public async Task Validation_failure_does_not_claim_transcoding_failed()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        var item = Seed(f, LibraryImportItemStatuses.Uploaded);
        var job = new LibraryImportJob(f.Db, Options.Create(new UploadOptions()),
            Options.Create(new TranscodingOptions()), Options.Create(new SourceDownloadOptions()),
            Options.Create(new LibraryImportOptions()), null!, null!, null!, f.Storage, null!, null!, null!,
            f.Lease(),
            NullLogger<LibraryImportJob>.Instance);

        await job.ProcessAsync(item.Id, default);

        var failed = f.Db.Queryable<LibraryImportItem>().Single(row => row.Id == item.Id);
        Assert.Equal(LibraryImportItemStatuses.ProcessingFailed, failed.Status);
        Assert.Equal(ErrorCodes.ResourceNotFound, failed.ErrorCode);
        Assert.Equal("queued", failed.TranscodeStatus);
        Assert.Equal(LibraryImportStatuses.CompletedWithErrors,
            f.Db.Queryable<LibraryImportBatch>().Single(row => row.Id == item.BatchId).Status);
    }

    [Fact]
    public async Task Retry_sweep_excludes_damaged_audio_but_retries_processing_timeouts()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        var damaged = Seed(f, LibraryImportItemStatuses.ProcessingFailed, ErrorCodes.MediaCorrupt);
        var timeout = Seed(f, LibraryImportItemStatuses.ProcessingFailed, "media_processing_timeout");
        var paths = new LibraryStoragePaths(Options.Create(new StorageOptions { LibraryRoot = f.Root }),
            new TestEnvironment { ContentRootPath = f.Root });
        var publisher = new ImportPublishService(f.Db, Options.Create(new SongRequestFulfillmentOptions()),
            f.Storage, paths, NullLogger<ImportPublishService>.Instance, null!, null!);
        var sweep = new LibraryImportRetrySweepJob(f.Db, f.Bus, publisher, Options.Create(f.Options),
            Options.Create(new LibraryImportOptions()), NullLogger<LibraryImportRetrySweepJob>.Instance,
            new StorageGovernanceService(paths, NullLogger<StorageGovernanceService>.Instance), f.Lease());

        await sweep.SweepAsync(default);

        var unchanged = f.Db.Queryable<LibraryImportItem>().Single(row => row.Id == damaged.Id);
        Assert.Equal(LibraryImportItemStatuses.ProcessingFailed, unchanged.Status);
        Assert.Equal(0, unchanged.ProcessingRetryCount);
        Assert.Equal(ErrorCodes.MediaCorrupt, unchanged.ErrorCode);
        Assert.DoesNotContain(f.Bus.Messages, message => message.Id == damaged.Id);
        var retried = f.Db.Queryable<LibraryImportItem>().Single(row => row.Id == timeout.Id);
        Assert.Equal(LibraryImportItemStatuses.Uploaded, retried.Status);
        Assert.Equal(1, retried.ProcessingRetryCount);
        Assert.Null(retried.ErrorCode);
        Assert.Contains(f.Bus.Messages, message => message.Queue == QueueNames.Imports && message.Id == timeout.Id);
    }

    private static LibraryImportItem Seed(SourceDownloadAcceptanceFixture f, string status, string? code = null)
    {
        var batch = new LibraryImportBatch { CreatedByUserId = Guid.NewGuid() };
        f.Db.Insertable(batch).ExecuteCommand();
        var item = new LibraryImportItem
        {
            BatchId = batch.Id, DisplayName = "song.flac", SourceStorageKey = "uploads/missing.flac",
            ContentHash = Guid.NewGuid().ToString("N"), IsUserUpload = true, Status = status,
            ErrorCode = code, UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-20)
        };
        f.Db.Insertable(item).ExecuteCommand();
        return item;
    }
}
