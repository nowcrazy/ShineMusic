namespace ShineMusic.Service.SourceProviders.Abstractions;

/// <summary>统一模型中的元数据字段。</summary>
public enum ProviderMetadataField
{
    /// <summary>平台标识。</summary>
    ProviderName,
    /// <summary>平台曲目或视频 ID。</summary>
    ProviderTrackId,
    /// <summary>标题。</summary>
    Title,
    /// <summary>艺人或平台作者。</summary>
    Artists,
    /// <summary>专辑或歌单名。</summary>
    AlbumTitle,
    /// <summary>时长（毫秒）。</summary>
    DurationMs,
    /// <summary>发行或视频发布年份。</summary>
    ReleaseYear,
    /// <summary>流派。</summary>
    Genres,
    /// <summary>封面链接。</summary>
    CoverUrl,
    /// <summary>来源页面链接。</summary>
    SourcePageUrl,
    /// <summary>许可或平台收费说明。</summary>
    License,
    /// <summary>声明音质，非探测结果。</summary>
    Quality,
    /// <summary>声明无损可用标记，非探测结果。</summary>
    IsLosslessAvailable,
}

/// <summary>单个入口的字段来源；Supported 仅表示实现会映射，单曲仍可能缺值。</summary>
public sealed record MetadataFieldSource(bool Supported, string Source, string Meaning);

/// <summary>搜索与详情分别声明字段，不将搜索结果的丰富度当成详情保证。</summary>
public sealed record ProviderMetadataFieldDescriptor(
    ProviderMetadataField Field, MetadataFieldSource Search, MetadataFieldSource Detail);

/// <summary>独立的静态字段目录；不探测网络、文件或真实音质。</summary>
public static class ProviderMetadataCatalog
{
    private const string Declared = "仅为实现声明值，不代表真实码率或无损探测；须由媒体探测确认。";
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<ProviderMetadataFieldDescriptor>> Entries = Build();

    /// <summary>已登记的平台名称。</summary>
    public static IEnumerable<string> ProviderNames => Entries.Keys;

    /// <summary>按平台标识查找字段；忽略大小写，未知平台返回空列表。</summary>
    public static IReadOnlyList<ProviderMetadataFieldDescriptor> GetFields(string providerName) =>
        Entries.TryGetValue(providerName, out var fields) ? fields : Array.Empty<ProviderMetadataFieldDescriptor>();

    private static IReadOnlyDictionary<string, IReadOnlyList<ProviderMetadataFieldDescriptor>> Build()
    {
        var entries = new Dictionary<string, IReadOnlyList<ProviderMetadataFieldDescriptor>>(StringComparer.OrdinalIgnoreCase);
        Add("netease", "id", "name", "artists[].name|ar[].name", "album.name|al.name", "duration|dt",
            "album.publishTime/releaseDate|al.publishTime/releaseDate", null, "album.picUrl|al.picUrl",
            "fee → DescribeFee（收费描述，不是版权许可或下载授权）", "固定 high", "固定 false");
        Add("migu", "copyrightId", "name|songName", "artists[].name", "albums[0].name|album", "duration/格式时长|length",
            "releaseYear/releaseDate/publishDate/publishTime", null, "ExtractCoverUrl", null,
            "格式列表及配置 ToneFlag → InferQuality", "格式列表含 SQ");
        Add("kuwo", "MUSICRID", "SONGNAME|name", "ARTIST|artist", "ALBUM|album", "DURATION|duration",
            "releaseYear/releaseDate/publishTime/year", null, "|pic", null, "固定 standard", "固定 false");
        Add("qq", "songmid", "songname|songname/name/title", "singer[].name", "albumname|albumname/album.name/album.title", "interval",
            "pubtime/year|pubtime/year/time_public/album.time_public", null, "albummid → 拼接封面 URL|albummid/album.mid → 拼接封面 URL", null,
            "pay.payplay → high/standard|固定 standard", "pay.payplay == 1|固定 false");
        Add("kugou", "FileHash", "SongName|fileName 拆分标题", "SingerName|fileName 拆分艺人", "AlbumName|", "Duration|timeLength",
            "PublishYear/PublishDate/PublishTime/Year", null, "Image|", null, "固定 standard", "固定 false");
        Add("qianqian", "TSID", "title", "artist 数组", "albumTitle", "duration",
            "releaseDate/pushTime", null, "pic", null, "固定 standard", "固定 false");
        Add("qishui", "track id", "曲目对象/SEO snapshot.Title", "曲目对象/SEO snapshot.Artists",
            "曲目对象/SEO snapshot.AlbumTitle", "曲目对象/SEO snapshot.DurationMs", "album.release_date/SEO snapshot.ReleaseYear",
            null, "曲目对象/SEO snapshot.CoverUrl", null, "固定 standard", "固定 false");
        Add("audius", "id", "title", "user.name", "album_name/playlist_name（可能是歌单名）", "duration",
            "release_year/release_date/releasedate", "genre", "artwork:480x480/1000x1000/150x150", "license",
            "is_downloadable → lossless/high", "is_downloadable（可下载原文件标记，不保证原文件无损）");
        Add("jamendo", "id", "name", "artist_name", "album_name", "duration",
            "releasedate/release_date/release_year", "musicinfo.tags.genres", "image", "license_ccurl（CC 许可链接）",
            "audiodownload URL 含 flac → lossless，否则 high", "audiodownload URL 含 flac");
        Add("bilibili", "bvid", "title（视频标题）", "author（UP主，不保证是演唱艺人）|owner.name（UP主，不保证是演唱艺人）",
            null, "duration（分:秒）|duration（秒）", "pubdate（视频发布年，不是歌曲发行年）",
            null, "pic（视频封面）", null, "固定 standard", "固定 false");
        Add("ytmusic", "yt-dlp entry.Id", "Title，回退视频 ID", "artist，回退 uploader/channel（上传者不保证是艺人）",
            "album", "DurationMs", "yt-dlp entry.ReleaseYear", null, "ThumbnailUrl", null, "固定 standard", "固定 false");
        return entries;

        void Add(string name, string id, string title, string artists, string? album, string duration,
            string year, string? genres, string? cover, string? license, string quality, string lossless)
        {
            var fields = new List<ProviderMetadataFieldDescriptor>
            {
                Field(ProviderMetadataField.ProviderName, name, "固定平台标识。"),
                Field(ProviderMetadataField.ProviderTrackId, id, "平台歌曲/视频 ID；详情沿用请求 ID。"),
                Field(ProviderMetadataField.Title, title, "标题。"),
                Field(ProviderMetadataField.Artists, artists, "艺人列表；来源注明上传者时不可视为演唱者保证。"),
                Field(ProviderMetadataField.AlbumTitle, album, "专辑名；playlist_name 可表示歌单。"),
                Field(ProviderMetadataField.DurationMs, duration, "统一毫秒；秒字段乘 1000，视频搜索时长先解析。"),
                Field(ProviderMetadataField.ReleaseYear, year, "年份可缺失；视频发布年与歌曲发行年语义不同。"),
                Field(ProviderMetadataField.Genres, genres, "流派；未映射时固定空列表。"),
                Field(ProviderMetadataField.CoverUrl, cover, "封面链接；视频平台为视频封面。"),
                Field(ProviderMetadataField.SourcePageUrl, name == "audius" ? "permalink → 绝对链接"
                    : name == "jamendo" ? "shareurl/shorturl" : name == "ytmusic" ? "WebpageUrl"
                    : name == "migu" ? "搜索 id/详情 songId → 平台歌曲页" : "平台 ID → 拼接歌曲/视频来源页",
                    "来源页面，不是音频下载直链。"),
                Field(ProviderMetadataField.License, license, "平台许可字段；网易云仅收费描述，不等于授权。"),
                Field(ProviderMetadataField.Quality, quality, Declared),
                Field(ProviderMetadataField.IsLosslessAvailable, lossless, Declared),
            };
            entries.Add(name, fields.AsReadOnly());
        }
    }

    private static ProviderMetadataFieldDescriptor Field(ProviderMetadataField field, string? sources, string meaning)
    {
        var pair = sources?.Split('|');
        return new(field, Source(pair?[0]), Source(pair is { Length: > 1 } ? pair[1] : pair?[0]));

        MetadataFieldSource Source(string? source) => new(!string.IsNullOrWhiteSpace(source), source ?? "未映射", meaning);
    }
}
