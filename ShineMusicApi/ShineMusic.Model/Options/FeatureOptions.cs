// 功能开关配置选项：绑定 "Features" 配置节，按部署需要灰度开放各业务能力，并通过系统能力接口下发给客户端。
namespace ShineMusic.Model.Options;

/// <summary>
/// 各业务功能的开关集合，对应配置节 "Features"；
/// 首版发布强制要求 <see cref="ManualReview"/> 与 <see cref="ServerTranscoding"/> 开启，启动时校验。
/// </summary>
public sealed class FeatureOptions
{
    /// <summary>该选项在 appsettings 中对应的配置节名称。</summary>
    public const string SectionName = "Features";

    /// <summary>是否允许未登录的匿名用户试听音频。</summary>
    public bool AnonymousPlayback { get; set; }

    /// <summary>是否允许登录用户创建和管理个人歌单/收藏。</summary>
    public bool UserCollections { get; set; }

    /// <summary>是否开放用户点歌（求歌）与投票功能。</summary>
    public bool SongRequests { get; set; }

    /// <summary>是否开放管理员批量导入媒体库的入口。</summary>
    public bool AdminLibraryImport { get; set; }

    /// <summary>是否开放用户上传本地音频（扫码导入）：用户上传的歌曲默认私有、可立即收听，
    /// 是否转公开进入公共曲库由管理员审核决定。</summary>
    public bool UserUploads { get; set; }

    /// <summary>导入的文件是否必须经过人工审核后才能进入正式曲库；首版强制为 true。</summary>
    public bool ManualReview { get; set; }

    /// <summary>是否开放由运营维护的精选合集。</summary>
    public bool EditorialCollections { get; set; }

    /// <summary>是否采集用户的播放/聆听事件（用于后续统计与推荐）。</summary>
    public bool ListeningEvents { get; set; }

    /// <summary>是否启用个性化推荐能力。</summary>
    public bool PersonalizedRecommendations { get; set; }

    /// <summary>是否启用服务端音频转码；首版强制为 true，客户端统一播放转码后的产物。</summary>
    public bool ServerTranscoding { get; set; }

    /// <summary>是否开放外部音源抓歌（开放版权平台搜索/下载/评分入库）管理功能。</summary>
    public bool SourceDownload { get; set; }
}