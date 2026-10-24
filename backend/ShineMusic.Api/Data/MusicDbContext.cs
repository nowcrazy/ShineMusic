// 职责：应用唯一的 EF Core 数据上下文（SQLite）。
// 继承 IdentityDbContext 以复用 ASP.NET Core Identity 的用户/角色表映射，
// 并在 OnModelCreating 中集中声明全部业务表的索引、唯一约束、复合主键、
// 表名/属性映射、级联删除行为以及 DateTimeOffset↔UTC DateTime 的转换。

using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;

namespace ShineMusic.Api.Data;

/// <summary>
/// ShineMusic 数据上下文：聚合 Identity 表、曲库目录表、用户互动表与曲库导入流水线表。
/// </summary>
public sealed class MusicDbContext(DbContextOptions<MusicDbContext> options)
	: IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
	// ===== 曲库目录（Catalog）相关 DbSet =====
	public DbSet<Artist> Artists => Set<Artist>();
	public DbSet<Album> Albums => Set<Album>();
	public DbSet<Track> Tracks => Set<Track>();
	public DbSet<Genre> Genres => Set<Genre>();
	public DbSet<Artwork> Artworks => Set<Artwork>();
	public DbSet<MediaAsset> MediaAssets => Set<MediaAsset>();
	public DbSet<EditorialCollection> EditorialCollections => Set<EditorialCollection>();
	public DbSet<Lyric> Lyrics => Set<Lyric>();

	// ===== 身份认证 / 用户互动 / 管理后台相关 DbSet =====
	public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
	public DbSet<Invitation> Invitations => Set<Invitation>();
	public DbSet<Favorite> Favorites => Set<Favorite>();
	public DbSet<UserPlaylist> UserPlaylists => Set<UserPlaylist>();
	public DbSet<UserPlaylistTrack> UserPlaylistTracks => Set<UserPlaylistTrack>();
	public DbSet<SongRequestRecord> SongRequests => Set<SongRequestRecord>();
	public DbSet<SongRequestVote> SongRequestVotes => Set<SongRequestVote>();
	public DbSet<SongRequestEventRecord> SongRequestEvents => Set<SongRequestEventRecord>();
	public DbSet<UserNotification> UserNotifications => Set<UserNotification>();
	public DbSet<AdminAuditEntry> AdminAuditEntries => Set<AdminAuditEntry>();

	// ===== 曲库批量导入流水线相关 DbSet =====
	public DbSet<LibraryImportBatch> LibraryImportBatches => Set<LibraryImportBatch>();
	public DbSet<LibraryImportItem> LibraryImportItems => Set<LibraryImportItem>();
	public DbSet<UserLibraryItem> UserLibraryItems => Set<UserLibraryItem>();

	// ===== 外部音源抓歌任务 =====
	public DbSet<ExternalDownloadTask> ExternalDownloadTasks => Set<ExternalDownloadTask>();

	// ===== 收听行为与个性化推荐相关 DbSet =====
	public DbSet<ListeningEvent> ListeningEvents => Set<ListeningEvent>();
	public DbSet<UserTrackFeedback> UserTrackFeedbacks => Set<UserTrackFeedback>();
	public DbSet<DailyRecommendationItem> DailyRecommendationItems => Set<DailyRecommendationItem>();

	// ===== 社交互动相关 DbSet =====
	public DbSet<TrackComment> TrackComments => Set<TrackComment>();
	public DbSet<UserFollow> UserFollows => Set<UserFollow>();
	public DbSet<TrackCommentLike> TrackCommentLikes => Set<TrackCommentLike>();

	// ===== 用户自定义图片主题 =====
	public DbSet<UserThemeProfile> UserThemeProfiles => Set<UserThemeProfile>();

	/// <summary>
	/// 模型创建钩子：先应用 Identity 的基础表配置，再叠加业务表的索引、约束、关系与值转换配置。
	/// </summary>
	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		// 必须先调用基类配置，Identity 的用户/角色/声明等表映射才会生效。
		base.OnModelCreating(modelBuilder);

		// ----- 用户与认证 -----
		// 展示名限制长度，配合数据库列大小约束。
		modelBuilder.Entity<ApplicationUser>().Property(user => user.DisplayName).HasMaxLength(100);

		// 刷新令牌哈希唯一：一个哈希只能对应一条令牌记录，防止重复登记/碰撞。
		modelBuilder.Entity<RefreshToken>().HasIndex(token => token.TokenHash).IsUnique();
		// (用户, 过期时间) 复合索引：按用户列出令牌及批量清理过期令牌的高频查询路径。
		modelBuilder.Entity<RefreshToken>().HasIndex(token => new { token.UserId, token.ExpiresAtUtc });
		// 刷新令牌 → 用户为一对多（用户侧不维护反向集合），外键为 UserId。
		modelBuilder.Entity<RefreshToken>().HasOne(token => token.User).WithMany().HasForeignKey(token => token.UserId);

		// ----- 邀请注册 -----
		// 邀请令牌哈希唯一：同一封邀请邮件的令牌不可重复登记。
		modelBuilder.Entity<Invitation>().HasIndex(invitation => invitation.TokenHash).IsUnique();
		// (归一化邮箱, 状态) 复合索引：查询某邮箱的有效邀请/防重复邀请。
		modelBuilder.Entity<Invitation>().HasIndex(invitation => new { invitation.NormalizedEmail, invitation.Status });
		// 过期时间单列索引：后台扫描过期邀请并流转为 expired。
		modelBuilder.Entity<Invitation>().HasIndex(invitation => invitation.ExpiresAtUtc);
		// 状态机字段限长 32，与 InvitationStatuses 常量长度匹配。
		modelBuilder.Entity<Invitation>().Property(invitation => invitation.Status).HasMaxLength(32);

	// ----- 收藏 -----
	// (用户, 曲目) 复合主键：同一用户对同一曲目只能收藏一次。
	modelBuilder.Entity<Favorite>().HasKey(favorite => new { favorite.UserId, favorite.TrackId });
	// (用户, 收藏时间) 复合索引：“我的收藏”按收藏时间倒序分页。
	modelBuilder.Entity<Favorite>().HasIndex(favorite => new { favorite.UserId, favorite.CreatedAtUtc });

	// ----- 用户曲库引用（用户上传/导入的曲目，引用模型）-----
	// (用户, 曲目) 唯一索引：同一用户对同一曲目只能有一条曲库引用。
	modelBuilder.Entity<UserLibraryItem>().HasIndex(item => new { item.UserId, item.TrackId }).IsUnique();
	// (用户, 加入时间) 复合索引：“我的曲库”按加入时间倒序分页。
	modelBuilder.Entity<UserLibraryItem>().HasIndex(item => new { item.UserId, item.AddedAtUtc });
	// 曲目 → 引用一对多：曲目删除时级联清理引用（引用本身不含数据，删除无副作用）。
	modelBuilder.Entity<UserLibraryItem>().HasOne(item => item.Track).WithMany().HasForeignKey(item => item.TrackId).OnDelete(DeleteBehavior.Cascade);

		// ----- 用户歌单 -----
		// (用户, 更新时间) 复合索引：歌单列表按最近更新排序。
		modelBuilder.Entity<UserPlaylist>().HasIndex(playlist => new { playlist.UserId, playlist.UpdatedAtUtc });
		modelBuilder.Entity<UserPlaylist>().Property(playlist => playlist.Name).HasMaxLength(100);
		// 歌单 → 条目为一对多，显式指定外键 PlaylistId（另一端有 Playlist 导航属性）。
		modelBuilder.Entity<UserPlaylist>().HasMany(playlist => playlist.Tracks).WithOne(track => track.Playlist).HasForeignKey(track => track.PlaylistId);
		// (歌单, 曲目) 复合主键：同一歌单内曲目不可重复。
		modelBuilder.Entity<UserPlaylistTrack>().HasKey(track => new { track.PlaylistId, track.TrackId });

		// ----- 点歌/求歌与投票 -----
		// 状态单列索引：管理端按审核状态（open/planned/...）筛选列表。
		modelBuilder.Entity<SongRequestRecord>().HasIndex(request => request.Status);
		// (状态, 归档时间) 复合索引：后台归档任务扫描“已满足且未归档”的记录。
		modelBuilder.Entity<SongRequestRecord>().HasIndex(request => new { request.Status, request.ArchivedAtUtc });
		// 标题/艺术家/专辑归一化值 + 状态复合索引：提交点歌时查重及后台筛重。
		modelBuilder.Entity<SongRequestRecord>().HasIndex(request => new { request.NormalizedTitle, request.NormalizedArtistName, request.NormalizedAlbumTitle, request.Status });
		// 点歌记录 → 投票一对多，外键 RequestId。
		modelBuilder.Entity<SongRequestRecord>().HasMany(request => request.Votes).WithOne(vote => vote.Request).HasForeignKey(vote => vote.RequestId);
		// (点歌记录, 用户) 复合主键：一人对一条点歌只能投一票。
		modelBuilder.Entity<SongRequestVote>().HasKey(vote => new { vote.RequestId, vote.UserId });

		// ----- 点歌需求状态事件（时间线） -----
		// (需求, 时间) 复合索引：按需求拉取正序时间线。
		modelBuilder.Entity<SongRequestEventRecord>().HasIndex(evt => new { evt.RequestId, evt.CreatedAtUtc });
		// 事件类型/身份/状态字段限长，控制存储体积。
		modelBuilder.Entity<SongRequestEventRecord>().Property(evt => evt.Kind).HasMaxLength(32);
		modelBuilder.Entity<SongRequestEventRecord>().Property(evt => evt.ActorKind).HasMaxLength(16);
		modelBuilder.Entity<SongRequestEventRecord>().Property(evt => evt.FromStatus).HasMaxLength(32);
		modelBuilder.Entity<SongRequestEventRecord>().Property(evt => evt.ToStatus).HasMaxLength(32);
		// 需求 → 事件一对多：删除需求时级联清理其时间线。
		modelBuilder.Entity<SongRequestEventRecord>().HasOne<SongRequestRecord>().WithMany()
			.HasForeignKey(evt => evt.RequestId).OnDelete(DeleteBehavior.Cascade);

		// ----- 站内信 -----
		// (用户, 创建时间) 复合索引：消息列表按时间倒序分页。
		modelBuilder.Entity<UserNotification>().HasIndex(notification => new { notification.UserId, notification.CreatedAtUtc });
		// (用户, 已读时间) 复合索引：未读角标统计（ReadAtUtc == null）。
		modelBuilder.Entity<UserNotification>().HasIndex(notification => new { notification.UserId, notification.ReadAtUtc });
		// 类型/标题/关联字段限长。
		modelBuilder.Entity<UserNotification>().Property(notification => notification.Kind).HasMaxLength(64);
		modelBuilder.Entity<UserNotification>().Property(notification => notification.Title).HasMaxLength(200);
		modelBuilder.Entity<UserNotification>().Property(notification => notification.Body).HasMaxLength(1000);
		modelBuilder.Entity<UserNotification>().Property(notification => notification.LinkUrl).HasMaxLength(300);
		modelBuilder.Entity<UserNotification>().Property(notification => notification.ResourceType).HasMaxLength(64);
		modelBuilder.Entity<UserNotification>().Property(notification => notification.ResourceId).HasMaxLength(100);

		// ----- 用户自定义图片主题 -----
		// 用户 Id 即主键：每用户至多一条配置（与 ApplicationUser 一对一，壁纸文件在 themes/ 目录）。
		modelBuilder.Entity<UserThemeProfile>().HasKey(profile => profile.UserId);
		// 颜色字段限长 7（#RRGGBB），壁纸扩展名限长 8（含前导点）。
		modelBuilder.Entity<UserThemeProfile>().Property(profile => profile.Accent).HasMaxLength(7);
		modelBuilder.Entity<UserThemeProfile>().Property(profile => profile.Accent2).HasMaxLength(7);
		modelBuilder.Entity<UserThemeProfile>().Property(profile => profile.Accent3).HasMaxLength(7);
		modelBuilder.Entity<UserThemeProfile>().Property(profile => profile.WallpaperExtension).HasMaxLength(8);

		// ----- 管理员审计 -----
		// (写入时间, 操作人) 复合索引：审计日志按时间线/操作人检索的主路径。
		modelBuilder.Entity<AdminAuditEntry>().HasIndex(entry => new { entry.CreatedAtUtc, entry.ActorUserId });
		// 审计各字符串字段限长 100，保证存储与索引体积可控。
		modelBuilder.Entity<AdminAuditEntry>().Property(entry => entry.Action).HasMaxLength(100);
		modelBuilder.Entity<AdminAuditEntry>().Property(entry => entry.ResourceType).HasMaxLength(100);
		modelBuilder.Entity<AdminAuditEntry>().Property(entry => entry.ResourceId).HasMaxLength(100);
		modelBuilder.Entity<AdminAuditEntry>().Property(entry => entry.TraceId).HasMaxLength(100);

		// ----- 曲库目录：名称/标题检索索引 -----
		// 名称、标题为普通（非唯一）索引，支撑模糊/前缀检索与排序。
		modelBuilder.Entity<Artist>().HasIndex(artist => artist.Name);
		modelBuilder.Entity<Album>().HasIndex(album => album.Title);
		modelBuilder.Entity<Track>().HasIndex(track => track.Title);
		// 流派名称唯一：防止同义流派重复建档。
		modelBuilder.Entity<Genre>().HasIndex(genre => genre.Name).IsUnique();
		// 发布时间索引：前台“已上架曲目”按发布时间查询/排序。
		modelBuilder.Entity<Track>().HasIndex(track => track.PublishedAt);
		// SQLite 以 UTC DateTime 存储：DateTimeOffset 写入时转 UTC DateTime，读取时标注为 Utc 还原偏移，保证时间语义一致。
		modelBuilder.Entity<Track>().Property(track => track.PublishedAt).HasConversion<DateTime?>(
			value => value.HasValue ? value.Value.UtcDateTime : null,
			value => value.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)) : null);

		// ----- 媒体资产 -----
		// (曲目, 是否发布) 复合索引：播放时快速定位曲目下已发布的资产。
		modelBuilder.Entity<MediaAsset>().HasIndex(asset => new { asset.TrackId, asset.IsPublished });
		// 内容哈希唯一：物理文件去重，同一音频内容不重复入库。
		modelBuilder.Entity<MediaAsset>().HasIndex(asset => asset.ContentHash).IsUnique();

		// ----- 歌词 -----
		// (曲目, 是否默认) 复合索引：播放时快速定位曲目下的默认歌词。
		modelBuilder.Entity<Lyric>().HasIndex(lyric => new { lyric.TrackId, lyric.IsDefault });
		// 语言字段限长 32（兼容 BCP-47 语言标签）。
		modelBuilder.Entity<Lyric>().Property(lyric => lyric.Language).HasMaxLength(32);
		// 格式字段限长 16（plain / lrc）。
		modelBuilder.Entity<Lyric>().Property(lyric => lyric.Format).HasMaxLength(16);
		// 来源字段限长 32（embedded / sidecar / lrclib / manual），外部源 ID 限长 64。
		modelBuilder.Entity<Lyric>().Property(lyric => lyric.Source).HasMaxLength(32);
		modelBuilder.Entity<Lyric>().Property(lyric => lyric.ProviderId).HasMaxLength(64);
		// 歌词 → 曲目：一对多，删除曲目时级联删除其全部歌词。
		modelBuilder.Entity<Lyric>().HasOne(lyric => lyric.Track).WithMany(track => track.Lyrics).HasForeignKey(lyric => lyric.TrackId).OnDelete(DeleteBehavior.Cascade);

		// ----- 曲库导入流水线 -----
		// (发起人, 创建时间) 复合索引：管理员查看自己的导入批次历史。
		modelBuilder.Entity<LibraryImportBatch>().HasIndex(batch => new { batch.CreatedByUserId, batch.CreatedAtUtc });
		// 批次 → 条目一对多并配置级联删除：删除批次时自动删除其全部条目（条目脱离批次无独立意义）。
		modelBuilder.Entity<LibraryImportBatch>().HasMany(batch => batch.Items).WithOne(item => item.Batch).HasForeignKey(item => item.BatchId).OnDelete(DeleteBehavior.Cascade);
		// 内容哈希索引：导入前去重/比对已存在文件。
		modelBuilder.Entity<LibraryImportItem>().HasIndex(item => item.ContentHash);
		// (状态, 创建时间) 复合索引：后台/管理端按流水线状态扫描条目。
		modelBuilder.Entity<LibraryImportItem>().HasIndex(item => new { item.Status, item.CreatedAtUtc });
		// 原始文件名限长 255，兼容常见文件系统的文件名长度上限。
		modelBuilder.Entity<LibraryImportItem>().Property(item => item.DisplayName).HasMaxLength(255);
		// 歌词来源标识限长 32，外部源记录 ID 限长 64。
		modelBuilder.Entity<LibraryImportItem>().Property(item => item.LyricsSource).HasMaxLength(32);
		modelBuilder.Entity<LibraryImportItem>().Property(item => item.LyricsProviderId).HasMaxLength(64);
		// 音源抓歌来源标识限长 32（audius/jamendo）。
		modelBuilder.Entity<LibraryImportItem>().Property(item => item.SourceProvider).HasMaxLength(32);

		// ----- 外部音源抓歌任务 -----
		// (平台, 平台曲目 Id) 唯一：同一音源的同一首曲目只允许存在一个任务（重试复用原任务），
		// 防止同一首歌被重复抓取占库存储。
		modelBuilder.Entity<ExternalDownloadTask>().HasIndex(task => new { task.Provider, task.ProviderTrackId }).IsUnique();
		// (状态, 创建时间) 复合索引：后台恢复扫描与管理端任务列表的主路径。
		modelBuilder.Entity<ExternalDownloadTask>().HasIndex(task => new { task.Status, task.CreatedAtUtc });
		// 字段限长，控制存储体积。
		modelBuilder.Entity<ExternalDownloadTask>().Property(task => task.Provider).HasMaxLength(32);
		modelBuilder.Entity<ExternalDownloadTask>().Property(task => task.ProviderTrackId).HasMaxLength(128);
		modelBuilder.Entity<ExternalDownloadTask>().Property(task => task.Title).HasMaxLength(300);
		modelBuilder.Entity<ExternalDownloadTask>().Property(task => task.AlbumTitle).HasMaxLength(300);
		modelBuilder.Entity<ExternalDownloadTask>().Property(task => task.Quality).HasMaxLength(16);
		modelBuilder.Entity<ExternalDownloadTask>().Property(task => task.Status).HasMaxLength(32);
		modelBuilder.Entity<ExternalDownloadTask>().Property(task => task.License).HasMaxLength(200);

		// ----- 收听事件（推荐算法的行为输入） -----
		// (用户, 时间) 复合索引：按用户拉取近期行为、聚合歌手/曲目偏好。
		modelBuilder.Entity<ListeningEvent>().HasIndex(evt => new { evt.UserId, evt.OccurredAtUtc });
		// (用户, 曲目) 复合索引：按曲目聚合播放/跳过次数。
		modelBuilder.Entity<ListeningEvent>().HasIndex(evt => new { evt.UserId, evt.TrackId });
		// 事件类型限长 16（play/complete/skip）。
		modelBuilder.Entity<ListeningEvent>().Property(evt => evt.EventType).HasMaxLength(16);

		// ----- 用户显式反馈（喜欢/不喜欢/不感兴趣） -----
		// (用户, 曲目) 复合主键：一人一曲至多一条反馈，天然防重。
		modelBuilder.Entity<UserTrackFeedback>().HasKey(feedback => new { feedback.UserId, feedback.TrackId });
		// (用户, 类型) 复合索引：按反馈类型批量查询（如排除所有“不感兴趣”）。
		modelBuilder.Entity<UserTrackFeedback>().HasIndex(feedback => new { feedback.UserId, feedback.Kind });
		modelBuilder.Entity<UserTrackFeedback>().Property(feedback => feedback.Kind).HasMaxLength(16);

		// ----- 每日推荐明细 -----
		// (用户, 日期, 曲目) 唯一：同一天同一曲目只出现一次。
		modelBuilder.Entity<DailyRecommendationItem>().HasIndex(item => new { item.UserId, item.DateKey, item.TrackId }).IsUnique();
		// (用户, 日期, 排名) 索引：按当日排名顺序取回推荐列表。
		modelBuilder.Entity<DailyRecommendationItem>().HasIndex(item => new { item.UserId, item.DateKey, item.Rank });

		// ----- 曲目评论 -----
		// (曲目, 发表时间) 复合索引：拉取某曲目评论按时间排序。
		modelBuilder.Entity<TrackComment>().HasIndex(comment => new { comment.TrackId, comment.CreatedAtUtc });
		// 父评论索引：按父评论聚合回复（删除父评论时级联清理其回复）。
		modelBuilder.Entity<TrackComment>().HasIndex(comment => comment.ParentId);
		// 评论正文限长 500。
		modelBuilder.Entity<TrackComment>().Property(comment => comment.Content).HasMaxLength(500);
		// 评论 → 曲目：多对一，删除曲目时级联删除其全部评论。
		modelBuilder.Entity<TrackComment>().HasOne<Track>().WithMany().HasForeignKey(comment => comment.TrackId).OnDelete(DeleteBehavior.Cascade);
		// 评论 → 父评论：自引用多对一，删除父评论时级联删除其回复。
		modelBuilder.Entity<TrackComment>().HasOne<TrackComment>().WithMany().HasForeignKey(comment => comment.ParentId).OnDelete(DeleteBehavior.Cascade);

		// ----- 用户关注 -----
		// (关注者, 被关注者) 复合主键：防止重复关注。
		modelBuilder.Entity<UserFollow>().HasKey(follow => new { follow.FollowerId, follow.FolloweeId });
		// (关注者, 关注时间) 复合索引：我的关注列表按时间倒序。
		modelBuilder.Entity<UserFollow>().HasIndex(follow => new { follow.FollowerId, follow.CreatedAtUtc });
		// (被关注者, 关注时间) 复合索引：粉丝列表按时间倒序。
		modelBuilder.Entity<UserFollow>().HasIndex(follow => new { follow.FolloweeId, follow.CreatedAtUtc });

		// ----- 评论点赞 -----
		// (评论, 用户) 复合主键：防止同一用户对同一条评论重复点赞。
		modelBuilder.Entity<TrackCommentLike>().HasKey(like => new { like.CommentId, like.UserId });
		// (用户, 点赞时间) 复合索引：我的点赞历史按时间倒序。
		modelBuilder.Entity<TrackCommentLike>().HasIndex(like => new { like.UserId, like.CreatedAtUtc });
		// 点赞 → 评论：多对一，删除评论时级联清理其全部点赞。
		modelBuilder.Entity<TrackCommentLike>()
			.HasOne<TrackComment>().WithMany().HasForeignKey(like => like.CommentId).OnDelete(DeleteBehavior.Cascade);

		// ----- 多对多关联表复合主键 -----
		// 关联表本身无独立 Id，使用双方外键构成复合主键，天然防止重复关联。
		modelBuilder.Entity<TrackArtist>().HasKey(link => new { link.TrackId, link.ArtistId });
		modelBuilder.Entity<TrackGenre>().HasKey(link => new { link.TrackId, link.GenreId });
		modelBuilder.Entity<AlbumArtist>().HasKey(link => new { link.AlbumId, link.ArtistId });
		modelBuilder.Entity<CollectionTrack>().HasKey(link => new { link.CollectionId, link.TrackId });

		// ----- 多对多关联表两端关系 -----
		// 分别配置关联表到两端实体的一对多关系及外键，EF 依此补全导航属性与连接表行为。
		modelBuilder.Entity<TrackArtist>().HasOne(link => link.Track).WithMany(track => track.TrackArtists).HasForeignKey(link => link.TrackId);
		modelBuilder.Entity<TrackArtist>().HasOne(link => link.Artist).WithMany(artist => artist.TrackArtists).HasForeignKey(link => link.ArtistId);
		modelBuilder.Entity<TrackGenre>().HasOne(link => link.Track).WithMany(track => track.TrackGenres).HasForeignKey(link => link.TrackId);
		modelBuilder.Entity<TrackGenre>().HasOne(link => link.Genre).WithMany(genre => genre.TrackGenres).HasForeignKey(link => link.GenreId);
		modelBuilder.Entity<AlbumArtist>().HasOne(link => link.Album).WithMany(album => album.AlbumArtists).HasForeignKey(link => link.AlbumId);
		modelBuilder.Entity<AlbumArtist>().HasOne(link => link.Artist).WithMany(artist => artist.AlbumArtists).HasForeignKey(link => link.ArtistId);
		modelBuilder.Entity<CollectionTrack>().HasOne(link => link.Collection).WithMany(collection => collection.CollectionTracks).HasForeignKey(link => link.CollectionId);
		modelBuilder.Entity<CollectionTrack>().HasOne(link => link.Track).WithMany(track => track.CollectionTracks).HasForeignKey(link => link.TrackId);

		// ----- 封面引用与级联行为 -----
		// 曲目 → 专辑：常规一对多外键（默认级联限制，专辑删除受曲目约束）。
		modelBuilder.Entity<Track>().HasOne(track => track.Album).WithMany(album => album.Tracks).HasForeignKey(track => track.AlbumId);
		// 曲目/专辑/精选集 → 封面：封面被删除时外键置空（SetNull），避免误删封面连带删除音乐内容。
		modelBuilder.Entity<Track>().HasOne(track => track.Artwork).WithMany().HasForeignKey(track => track.ArtworkId).OnDelete(DeleteBehavior.SetNull);
		modelBuilder.Entity<Album>().HasOne(album => album.Artwork).WithMany().HasForeignKey(album => album.ArtworkId).OnDelete(DeleteBehavior.SetNull);
		// 媒体资产 → 曲目：一对多外键（删除曲目时由资产侧的默认行为约束/处理）。
		modelBuilder.Entity<MediaAsset>().HasOne(asset => asset.Track).WithMany(track => track.MediaAssets).HasForeignKey(asset => asset.TrackId);
		modelBuilder.Entity<EditorialCollection>().HasOne(collection => collection.Artwork).WithMany().HasForeignKey(collection => collection.ArtworkId).OnDelete(DeleteBehavior.SetNull);

	}
}
