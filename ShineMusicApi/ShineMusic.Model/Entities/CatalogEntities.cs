// 职责：定义曲库“目录（Catalog）”领域的核心实体——
// 艺术家（Artist）、专辑（Album）、曲目（Track）、流派（Genre）、封面（Artwork）、
// 媒体资产（MediaAsset）、编辑精选集合（EditorialCollection）以及它们之间的多对多关联表。
// 这些实体由 SqlSugar CodeFirst 统一映射到 PostgreSQL，并被管理端编目接口与前台浏览接口共用。
// SqlSugar 映射：表名沿用既有表名（存量数据库不动），导航属性一律 IsIgnore（查询用显式联表或批量水合）。

using System.Diagnostics.CodeAnalysis;
using SqlSugar;

namespace ShineMusic.Model.Entities;

/// <summary>艺术家（歌手/乐队），可通过 TrackArtist/AlbumArtist 关联表与曲目、专辑构成多对多关系。</summary>
[SugarTable("Artists")]
public sealed class Artist
{
    /// <summary>无参构造：满足 SqlSugar Insertable/Updateable 的 new() 约束（required 成员由调用方填充）。</summary>
    [SetsRequiredMembers]
    public Artist() { Name = null!; }

    /// <summary>艺术家唯一标识，使用 Guid 主键（由应用层在构造时生成，避免自增 id 泄露规模）。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>艺术家名称（展示名，非唯一）。</summary>
    public required string Name { get; set; }
    /// <summary>艺术家简介（富文本/纯文本，可为空）。SqlSugar 默认把 string? 推断为 NOT NULL，显式指定 IsNullable。</summary>
    [SugarColumn(IsNullable = true)]
    public string? Biography { get; set; }
    /// <summary>该艺术家参与的“曲目-艺术家”关联行集合。</summary>
    [SugarColumn(IsIgnore = true)]
    public ICollection<TrackArtist> TrackArtists { get; set; } = [];
    /// <summary>该艺术家参与的“专辑-艺术家”关联行集合。</summary>
    [SugarColumn(IsIgnore = true)]
    public ICollection<AlbumArtist> AlbumArtists { get; set; } = [];
}

/// <summary>专辑，一张专辑可包含多首曲目，并可关联多位艺术家与一张封面。</summary>
[SugarTable("Albums")]
public sealed class Album
{
    /// <summary>无参构造：满足 SqlSugar Insertable/Updateable 的 new() 约束（required 成员由调用方填充）。</summary>
    [SetsRequiredMembers]
    public Album() { Title = null!; }

    /// <summary>专辑唯一标识，Guid 主键。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>专辑标题。</summary>
    public required string Title { get; set; }
    /// <summary>发行年份（元数据缺失时可为空）。</summary>
    [SugarColumn(IsNullable = true)]
    public int? ReleaseYear { get; set; }
    /// <summary>封面 Id 外键，指向 Artwork；封面删除时置空（SetNull）。</summary>
    [SugarColumn(IsNullable = true)]
    public Guid? ArtworkId { get; set; }
    /// <summary>封面导航属性。</summary>
    [SugarColumn(IsIgnore = true)]
    public Artwork? Artwork { get; set; }
    /// <summary>“专辑-艺术家”关联行集合（支持合辑/多位艺术家）。</summary>
    [SugarColumn(IsIgnore = true)]
    public ICollection<AlbumArtist> AlbumArtists { get; set; } = [];
    /// <summary>专辑下的曲目集合（一对多导航）。</summary>
    [SugarColumn(IsIgnore = true)]
    public ICollection<Track> Tracks { get; set; } = [];
}

/// <summary>曲目（单曲），曲库最核心的播放单元；可属于一张专辑，并挂载多个媒体资产与封面。</summary>
[SugarTable("Tracks")]
public sealed class Track
{
    /// <summary>无参构造：满足 SqlSugar Insertable/Updateable 的 new() 约束（required 成员由调用方填充）。</summary>
    [SetsRequiredMembers]
    public Track() { Title = null!; }

    /// <summary>曲目唯一标识，Guid 主键。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>曲目标题。</summary>
    public required string Title { get; set; }
    /// <summary>所属专辑 Id 外键；单曲（无专辑）时为空。</summary>
    [SugarColumn(IsNullable = true)]
    public Guid? AlbumId { get; set; }
    /// <summary>所属专辑导航属性。</summary>
    [SugarColumn(IsIgnore = true)]
    public Album? Album { get; set; }
    /// <summary>碟内曲目序号（来自音频标签 TrackNumber），用于专辑内排序。可为空。</summary>
    [SugarColumn(IsNullable = true)]
    public int? TrackNumber { get; set; }
    /// <summary>碟片号（多碟专辑使用），用于先按碟再按曲号排序。可为空。</summary>
    [SugarColumn(IsNullable = true)]
    public int? DiscNumber { get; set; }
    /// <summary>发行年份（可独立于专辑年份，元数据缺失时为空）。</summary>
    [SugarColumn(IsNullable = true)]
    public int? ReleaseYear { get; set; }
    /// <summary>曲目时长（毫秒）；ffprobe 探测与 TagLib 解析结果会相互兜底。</summary>
    [SugarColumn(IsNullable = true)]
    public long? DurationMs { get; set; }
    /// <summary>曲目自身封面 Id 外键（优先于专辑封面展示）；删除封面时置空。可为空。</summary>
    [SugarColumn(IsNullable = true)]
    public Guid? ArtworkId { get; set; }
    /// <summary>曲目封面导航属性。</summary>
    [SugarColumn(IsIgnore = true)]
    public Artwork? Artwork { get; set; }
    /// <summary>入库时间（UTC），新曲目入库即写入。</summary>
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>发布时间（UTC）；为空表示尚未发布/上架，前台不可见。该列建有索引供“已发布”列表查询。
    /// 用户上传的曲目在转公开前处于此状态（私有，仅上传者本人可见）。</summary>
    [SugarColumn(IsNullable = true)]
    public DateTimeOffset? PublishedAt { get; set; }
    /// <summary>上传者用户 Id：用户上传的曲目私有期间归上传者所有（PublishedAt 为空）；
    /// 官方/管理员导入的曲目为空。转公开后保留，用于追溯来源。</summary>
    [SugarColumn(IsNullable = true)]
    public Guid? OwnerUserId { get; set; }
    /// <summary>“曲目-艺术家”关联行集合。</summary>
    [SugarColumn(IsIgnore = true)]
    public ICollection<TrackArtist> TrackArtists { get; set; } = [];
    /// <summary>“曲目-流派”关联行集合。</summary>
    [SugarColumn(IsIgnore = true)]
    public ICollection<TrackGenre> TrackGenres { get; set; } = [];
    /// <summary>曲目对应的媒体资产集合（同一曲目可有不同编码/码率版本）。</summary>
    [SugarColumn(IsIgnore = true)]
    public ICollection<MediaAsset> MediaAssets { get; set; } = [];
    /// <summary>“精选集合-曲目”关联行集合。</summary>
    [SugarColumn(IsIgnore = true)]
    public ICollection<CollectionTrack> CollectionTracks { get; set; } = [];
    /// <summary>曲目关联的歌词集合（支持多语言/多格式，一首曲目可挂多份歌词）。</summary>
    [SugarColumn(IsIgnore = true)]
    public ICollection<Lyric> Lyrics { get; set; } = [];
}

/// <summary>
/// 歌词实体：一首曲目可关联多份歌词（不同语言、纯文本/LRC 逐行时间轴等），
/// 通过 IsDefault 标记默认展示版本，前端播放时按需拉取。
/// </summary>
[SugarTable("Lyrics")]
public sealed class Lyric
{
    /// <summary>歌词唯一标识，Guid 主键。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>所属曲目 Id 外键。</summary>
    public Guid TrackId { get; set; }
    /// <summary>所属曲目导航属性。</summary>
    [SugarColumn(IsIgnore = true)]
    public Track Track { get; set; } = null!;
    /// <summary>语言代码（如 zh-CN、en、ja）；纯音乐或语言未知时可为空。</summary>
    [SugarColumn(IsNullable = true)]
    public string? Language { get; set; }
    /// <summary>歌词格式：plain（纯文本）或 lrc（带时间轴的逐行歌词）。</summary>
    public string Format { get; set; } = LyricFormats.Plain;
    /// <summary>歌词正文（纯文本或 LRC 内容）。</summary>
    [SugarColumn(ColumnDataType = "text")]
    public string Content { get; set; } = string.Empty;
    /// <summary>
    /// 歌词来源，取值见 <see cref="LyricsSources"/>：embedded（内嵌标签）/ sidecar（同名 .lrc）/
    /// lrclib（外部源自动匹配）/ manual（人工填写或上传）。
    /// </summary>
    public string Source { get; set; } = LyricsSources.Manual;
    /// <summary>匹配置信度（0~1）：内嵌/旁挂/人工为 1，外部源按标题+艺人+时长相似度打分。</summary>
    [SugarColumn(IsNullable = true)]
    public double? Confidence { get; set; }
    /// <summary>外部歌词源记录 ID（如 LRCLIB 的 id），便于回溯；非外部来源为 null。</summary>
    [SugarColumn(IsNullable = true)]
    public string? ProviderId { get; set; }
    /// <summary>是否为该曲目默认展示的歌词；同一曲目最多一份默认。</summary>
    public bool IsDefault { get; set; }
    /// <summary>创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>最后更新时间（UTC）。</summary>
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>歌词格式常量，避免硬编码字符串散落各处。</summary>
public static class LyricFormats
{
    /// <summary>纯文本歌词。</summary>
    public const string Plain = "plain";
    /// <summary>带时间轴的 LRC 逐行歌词。</summary>
    public const string Lrc = "lrc";
}

/// <summary>
/// 歌词来源常量：记录一份歌词是怎么来的，用于审核判断与问题回溯。
/// 自动匹配（尤其是外部源）的歌词可信度低于内嵌标签，审核界面据此提示。
/// </summary>
public static class LyricsSources
{
    /// <summary>音频文件内嵌标签（ID3v2 USLT / MP4 ©lyr / Vorbis LYRICS）。</summary>
    public const string Embedded = "embedded";
    /// <summary>与音频同名的旁挂歌词文件（.lrc / .txt）。</summary>
    public const string Sidecar = "sidecar";
    /// <summary>外部歌词源自动匹配（LRCLIB，开源多语言源）。</summary>
    public const string Lrclib = "lrclib";
    /// <summary>外部歌词源自动匹配（酷狗，中文曲目覆盖率高）。</summary>
    public const string KuGou = "kugou";
    /// <summary>外部歌词源自动匹配（咪咕，运营商版权曲库，主流华语覆盖好）。</summary>
    public const string Migu = "migu";
    /// <summary>人工填写或人工上传的歌词文件。</summary>
    public const string Manual = "manual";
}

/// <summary>流派（曲风）字典实体，名称唯一。</summary>
[SugarTable("Genres")]
public sealed class Genre
{
    /// <summary>无参构造：满足 SqlSugar Insertable/Updateable 的 new() 约束（required 成员由调用方填充）。</summary>
    [SetsRequiredMembers]
    public Genre() { Name = null!; }

    /// <summary>流派唯一标识，Guid 主键。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>流派名称，数据库层面配置为唯一索引，防止同义流派重复创建。</summary>
    public required string Name { get; set; }
    /// <summary>“曲目-流派”关联行集合。</summary>
    [SugarColumn(IsIgnore = true)]
    public ICollection<TrackGenre> TrackGenres { get; set; } = [];
}

/// <summary>封面图片资源的元数据记录，文件本体保存在对象存储/本地存储的 StorageKey 处。</summary>
[SugarTable("Artworks")]
public sealed class Artwork
{
    /// <summary>无参构造：满足 SqlSugar Insertable/Updateable 的 new() 约束（required 成员由调用方填充）。</summary>
    [SetsRequiredMembers]
    public Artwork() { StorageKey = null!; ContentType = null!; }

    /// <summary>封面唯一标识，Guid 主键。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>存储键（相对存储根的路径/键名），实际二进制不入库。</summary>
    public required string StorageKey { get; set; }
    /// <summary>图片 MIME 类型（如 image/jpeg、image/png）。</summary>
    public required string ContentType { get; set; }
}

/// <summary>媒体资产：曲目下的一份具体音频文件（某种容器/编码/码率组合），转码产物也记录于此。</summary>
[SugarTable("MediaAssets")]
public sealed class MediaAsset
{
    /// <summary>无参构造：满足 SqlSugar Insertable/Updateable 的 new() 约束（required 成员由调用方填充）。</summary>
    [SetsRequiredMembers]
    public MediaAsset() { StorageKey = null!; Container = null!; Codec = null!; ContentHash = null!; }

    /// <summary>媒体资产唯一标识，Guid 主键。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>所属曲目 Id 外键。</summary>
    public Guid TrackId { get; set; }
    /// <summary>所属曲目导航属性。</summary>
    [SugarColumn(IsIgnore = true)]
    public Track Track { get; set; } = null!;
    /// <summary>音频文件存储键。</summary>
    public required string StorageKey { get; set; }
    /// <summary>容器格式（规范化后的名称，如 mp3、flac、m4a）。</summary>
    public required string Container { get; set; }
    /// <summary>音频编码名称（ffprobe 探测的 codec_name，如 aac、flac）。</summary>
    public required string Codec { get; set; }
    /// <summary>文件内容哈希（去重/校验用），配置为唯一索引，防止同一文件重复入库。</summary>
    public required string ContentHash { get; set; }
    /// <summary>码率（kbps），探测失败时可为空。</summary>
    [SugarColumn(IsNullable = true)]
    public int? BitrateKbps { get; set; }
    /// <summary>是否已发布（上架）；与 (TrackId, IsPublished) 复合索引共同支撑“取已发布资产”查询。</summary>
    public bool IsPublished { get; set; }
    /// <summary>是否为主资产（同曲目多版本时默认播放的那份）。</summary>
    public bool IsPrimary { get; set; }
}

/// <summary>
/// 用户曲库引用：用户“拥有”一首曲目的记录（引用模型，曲目本体全局唯一）。
/// 用户上传的歌曲（含查重命中后直接引用的系统已有曲目）写入此表；
/// 同一用户对同一曲目仅一行（UserId+TrackId 唯一索引）。移除引用不影响曲目本体。
/// </summary>
[SugarTable("UserLibraryItems")]
public sealed class UserLibraryItem
{
    /// <summary>引用记录唯一标识，Guid 主键。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>用户 Id 外键。</summary>
    public Guid UserId { get; set; }
    /// <summary>曲目 Id 外键。</summary>
    public Guid TrackId { get; set; }
    /// <summary>曲目导航属性。</summary>
    [SugarColumn(IsIgnore = true)]
    public Track Track { get; set; } = null!;
    /// <summary>加入曲库时间（UTC）。</summary>
    public DateTimeOffset AddedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>编辑精选集合（运营手工编排的歌单/合辑），与用户自建歌单相区分。</summary>
[SugarTable("EditorialCollections")]
public sealed class EditorialCollection
{
    /// <summary>无参构造：满足 SqlSugar Insertable/Updateable 的 new() 约束（required 成员由调用方填充）。</summary>
    [SetsRequiredMembers]
    public EditorialCollection() { Title = null!; }

    /// <summary>集合唯一标识，Guid 主键。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>集合标题。</summary>
    public required string Title { get; set; }
    /// <summary>集合描述/推荐语。</summary>
    [SugarColumn(IsNullable = true)]
    public string? Description { get; set; }
    /// <summary>封面 Id 外键；删除封面时置空。</summary>
    [SugarColumn(IsNullable = true)]
    public Guid? ArtworkId { get; set; }
    /// <summary>封面导航属性。</summary>
    [SugarColumn(IsIgnore = true)]
    public Artwork? Artwork { get; set; }
    /// <summary>集合之间的排序序号（越小越靠前），用于运营位/列表展示顺序。</summary>
    public int SortOrder { get; set; }
    /// <summary>集合是否已发布；未发布时前台不可见。</summary>
    public bool IsPublished { get; set; }
    /// <summary>“精选集合-曲目”关联行集合（集合内曲目及其顺序）。</summary>
    [SugarColumn(IsIgnore = true)]
    public ICollection<CollectionTrack> CollectionTracks { get; set; } = [];
}

/// <summary>曲目-艺术家多对多关联表（复合主键 TrackId+ArtistId，与 EF 既有表结构一致）。</summary>
[SugarTable("TrackArtist")]
public sealed class TrackArtist
{
    /// <summary>曲目 Id（复合主键的一部分）。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid TrackId { get; set; }
    /// <summary>曲目导航属性。</summary>
    [SugarColumn(IsIgnore = true)]
    public Track Track { get; set; } = null!;
    /// <summary>艺术家 Id（复合主键的一部分）。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid ArtistId { get; set; }
    /// <summary>艺术家导航属性。</summary>
    [SugarColumn(IsIgnore = true)]
    public Artist Artist { get; set; } = null!;
}

/// <summary>曲目-流派多对多关联表（复合主键 TrackId+GenreId，与 EF 既有表结构一致）。</summary>
[SugarTable("TrackGenre")]
public sealed class TrackGenre
{
    /// <summary>曲目 Id（复合主键的一部分）。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid TrackId { get; set; }
    /// <summary>曲目导航属性。</summary>
    [SugarColumn(IsIgnore = true)]
    public Track Track { get; set; } = null!;
    /// <summary>流派 Id（复合主键的一部分）。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid GenreId { get; set; }
    /// <summary>流派导航属性。</summary>
    [SugarColumn(IsIgnore = true)]
    public Genre Genre { get; set; } = null!;
}

/// <summary>专辑-艺术家多对多关联表（复合主键 AlbumId+ArtistId，与 EF 既有表结构一致）。</summary>
[SugarTable("AlbumArtist")]
public sealed class AlbumArtist
{
    /// <summary>专辑 Id（复合主键的一部分）。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid AlbumId { get; set; }
    /// <summary>专辑导航属性。</summary>
    [SugarColumn(IsIgnore = true)]
    public Album Album { get; set; } = null!;
    /// <summary>艺术家 Id（复合主键的一部分）。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid ArtistId { get; set; }
    /// <summary>艺术家导航属性。</summary>
    [SugarColumn(IsIgnore = true)]
    public Artist Artist { get; set; } = null!;
}

/// <summary>精选集合-曲目关联表（复合主键 CollectionId+TrackId），SortOrder 决定集合内播放顺序。</summary>
[SugarTable("CollectionTrack")]
public sealed class CollectionTrack
{
    /// <summary>精选集合 Id（复合主键的一部分）。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid CollectionId { get; set; }
    /// <summary>精选集合导航属性。</summary>
    [SugarColumn(IsIgnore = true)]
    public EditorialCollection Collection { get; set; } = null!;
    /// <summary>曲目 Id（复合主键的一部分）。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid TrackId { get; set; }
    /// <summary>曲目导航属性。</summary>
    [SugarColumn(IsIgnore = true)]
    public Track Track { get; set; } = null!;
    /// <summary>集合内排序序号（手工编排的播放顺序，越小越靠前）。</summary>
    public int SortOrder { get; set; }
}
