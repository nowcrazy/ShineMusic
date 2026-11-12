using SqlSugar;
using Xunit;

namespace ShineMusic.Tests.Architecture;

/// <summary>
/// 验证命名钩子（SqlSugarFactory 的 EntityNameService / EntityService）确实把 PascalCase
/// 实体名/属性名映射为 snake_case 数据库标识符。不连接数据库：用 ToSql() 仅生成 SQL 文本。
/// </summary>
public class NamingConventionTests
{
    private static SqlSugarClient CreateClient() =>
        ShineMusic.Repository.Data.SqlSugarFactory.Create(
            "Host=127.0.0.1;Database=shinemusic;Username=postgres;Password=postgres",
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

    [Fact]
    public void Track_table_and_columns_use_snake_case()
    {
        using var db = CreateClient();
        var sql = db.Queryable<ShineMusic.Model.Entities.Track>().ToSql().Key;
        Assert.Contains("tracks", sql);
        Assert.Contains("title", sql);
        Assert.Contains("album_id", sql);
        // AddedAtUtc 属性被钩子转为 added_at_utc。
        Assert.Contains("added_at_utc", sql);
    }

    [Fact]
    public void External_download_task_provider_column_is_snake()
    {
        using var db = CreateClient();
        var sql = db.Queryable<ShineMusic.Model.Entities.ExternalDownloadTask>().ToSql().Key;
        Assert.Contains("external_download_tasks", sql);
        // Provider 重命名为 SourceProvider，钩子转为 source_provider。
        Assert.Contains("source_provider", sql);
        // AutoPublished 重命名为 IsAutoPublished，钩子转为 is_auto_published。
        Assert.Contains("is_auto_published", sql);
    }

    [Fact]
    public void Identity_user_table_follows_sugar_table_name()
    {
        using var db = CreateClient();
        var sql = db.Queryable<ShineMusic.Model.Entities.ApplicationUser>().ToSql().Key;
        // SugarTable("users") -> 钩子 snake 为 users。
        Assert.Contains("users", sql);
    }

    [Fact]
    public void Admin_audit_entry_ip_column_renamed_to_client_ip()
    {
        using var db = CreateClient();
        var sql = db.Queryable<ShineMusic.Model.Entities.AdminAuditEntry>().ToSql().Key;
        Assert.Contains("admin_audit_entries", sql);
        Assert.Contains("client_ip", sql);
    }

    [Fact]
    public void Song_request_event_kind_column_is_snake()
    {
        using var db = CreateClient();
        var sql = db.Queryable<ShineMusic.Model.Entities.SongRequestEventRecord>().ToSql().Key;
        Assert.Contains("song_request_events", sql);
        // Kind 重命名为 EventKind，钩子转为 event_kind。
        Assert.Contains("event_kind", sql);
    }

    [Fact]
    public void Library_import_item_region_and_explicit_renamed()
    {
        using var db = CreateClient();
        var sql = db.Queryable<ShineMusic.Model.Entities.LibraryImportItem>().ToSql().Key;
        Assert.Contains("library_import_items", sql);
        // Region -> RegionCategory -> region_category
        Assert.Contains("region_category", sql);
        // Explicit -> IsExplicit -> is_explicit
        Assert.Contains("is_explicit", sql);
        // MetadataReviewed -> IsMetadataReviewed -> is_metadata_reviewed
        Assert.Contains("is_metadata_reviewed", sql);
    }

    [Fact]
    public void User_theme_profile_enabled_renamed_to_is_enabled()
    {
        using var db = CreateClient();
        var sql = db.Queryable<ShineMusic.Model.Entities.UserThemeProfile>().ToSql().Key;
        Assert.Contains("user_theme_profiles", sql);
        Assert.Contains("is_enabled", sql);
    }

    [Fact]
    public void Favorites_table_is_user_favorites()
    {
        using var db = CreateClient();
        var sql = db.Queryable<ShineMusic.Model.Entities.Favorite>().ToSql().Key;
        Assert.Contains("user_favorites", sql);
    }

    [Fact]
    public void Listening_event_type_column_is_snake()
    {
        using var db = CreateClient();
        var sql = db.Queryable<ShineMusic.Model.Entities.ListeningEvent>().ToSql().Key;
        Assert.Contains("listening_events", sql);
        // EventType -> ListeningEventType -> listening_event_type
        Assert.Contains("listening_event_type", sql);
    }

    [Fact]
    public void Junction_tables_use_plural_snake_case()
    {
        using var db = CreateClient();
        Assert.Contains("track_artists", db.Queryable<ShineMusic.Model.Entities.TrackArtist>().ToSql().Key);
        Assert.Contains("album_artists", db.Queryable<ShineMusic.Model.Entities.AlbumArtist>().ToSql().Key);
        Assert.Contains("track_genres", db.Queryable<ShineMusic.Model.Entities.TrackGenre>().ToSql().Key);
        Assert.Contains("collection_tracks", db.Queryable<ShineMusic.Model.Entities.CollectionTrack>().ToSql().Key);
    }

    [Fact]
    public void Identity_tables_have_no_asp_prefix()
    {
        using var db = CreateClient();
        Assert.Contains("roles", db.Queryable<ShineMusic.Model.Entities.ApplicationRole>().ToSql().Key);
        var joinSql = db.Queryable<ShineMusic.Model.Entities.ApplicationUserRole>().ToSql().Key;
        Assert.Contains("user_roles", joinSql);
        Assert.DoesNotContain("asp_net", joinSql);
    }

    [Fact]
    public void User_track_feedback_kind_column_is_snake()
    {
        using var db = CreateClient();
        var sql = db.Queryable<ShineMusic.Model.Entities.UserTrackFeedback>().ToSql().Key;
        Assert.Contains("user_track_feedbacks", sql);
        // Kind -> FeedbackKind -> feedback_kind
        Assert.Contains("feedback_kind", sql);
    }
}
