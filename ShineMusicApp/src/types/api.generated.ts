/**
 * ============================================================================
 * 【自动生成，请勿手工修改】
 *
 * 本文件由 `python backend/tools/generate-ts-contracts.py` 从后端
 * `backend/ShineMusic.Api/**\/*.cs` 中的 `public sealed record` DTO 生成。
 *
 * 修改契约的流程：
 *   1. 改后端 Contracts/ 下的 record；
 *   2. 跑 `yarn gen:contracts` 重新生成本文件；
 *   3. `git diff` 里同步出现的前端变化就是「本次契约变更的影响面」。
 *
 * 若后端的改动没有反映到这里，说明命令没跑；直接手改本文件会在下次生成时被覆盖。
 *
 * 命名规则：C# `XxxDto` / `XxxRequest` / `XxxResponse` → TS `ApiXxx`。
 * 与历史手写名不一致的类型，在 `src/types/music.ts` 中以 export alias 收口，
 * 页面只需 import 老名字，迁移期不需要改业务代码。
 * ============================================================================
 */


/** 来源：`backend/ShineMusic.Api/Contracts/UserUploadDtos.cs` */
export interface ApiAddToLibraryRequest {
  trackIds: readonly string[]
}

/** 来源：`backend/ShineMusic.Api/Contracts/CatalogDtos.cs` */
export interface ApiAlbumDetail {
  id: string
  title: string
  releaseYear?: number | null
  artists: readonly ApiArtistSummary[]
  artworkUrl?: string | null
  tracks: readonly ApiTrack[]
}

/** 来源：`backend/ShineMusic.Api/Contracts/CatalogDtos.cs` */
export interface ApiAlbumSummary {
  id: string
  title: string
}

/** 来源：`backend/ShineMusic.Api/Contracts/CatalogDtos.cs` */
export interface ApiErrorDetail {
  code: string
  message: string
  traceId: string
}

/** 来源：`backend/ShineMusic.Api/Contracts/CatalogDtos.cs` */
export interface ApiErrorResponse {
  error: ApiErrorDetail
}

/** 来源：`backend/ShineMusic.Api/Contracts/CatalogDtos.cs` */
export interface ApiArtistDetail {
  id: string
  name: string
  biography?: string | null
  albums: readonly ApiAlbumSummary[]
  tracks: readonly ApiTrack[]
}

/** 来源：`backend/ShineMusic.Api/Contracts/CatalogDtos.cs` */
export interface ApiArtistSummary {
  id: string
  name: string
}

/** 来源：`backend/ShineMusic.Api/Contracts/LibraryImportDtos.cs` */
export interface ApiArtworkUploadResult {
  id: string
  artworkUrl: string
}

/** 来源：`backend/ShineMusic.Api/Contracts/CatalogDtos.cs` */
export interface ApiAudio {
  container: string
  codec: string
  bitrateKbps?: number | null
}

/** 来源：`backend/ShineMusic.Api/Services/TokenService.cs` */
export interface ApiAuthToken {
  accessToken: string
  refreshToken: string
  tokenType: string
  expiresIn: number
}

/** 来源：`backend/ShineMusic.Api/Contracts/ProfileDtos.cs` */
export interface ApiAvatarUrl {
  avatarUrl?: string | null
}

/** 来源：`backend/ShineMusic.Api/Contracts/ProfileDtos.cs` */
export interface ApiChangePassword {
  currentPassword: string
  newPassword: string
}

/** 来源：`backend/ShineMusic.Api/Contracts/CatalogDtos.cs` */
export interface ApiCollectionDetail {
  id: string
  title: string
  description?: string | null
  artworkUrl?: string | null
  tracks: readonly ApiTrack[]
}

/** 来源：`backend/ShineMusic.Api/Contracts/CatalogDtos.cs` */
export interface ApiCollectionSummary {
  id: string
  title: string
  description?: string | null
  artworkUrl?: string | null
  trackCount: number
}

/** 来源：`backend/ShineMusic.Api/Contracts/SocialDtos.cs` */
export interface ApiCommentLikeResult {
  likeCount: number
  likedByMe: boolean
}

/** 来源：`backend/ShineMusic.Api/Endpoints/CollectionManagementEndpoints.cs` */
export interface ApiCreateCollection {
  title: string
  description?: string | null
  trackIds: readonly string[]
  artworkId?: string | null
}

/** 来源：`backend/ShineMusic.Api/Contracts/SocialDtos.cs` */
export interface ApiCreateComment {
  content: string
  parentId?: string | null
}

/** 来源：`backend/ShineMusic.Api/Endpoints/InvitationEndpoints.cs` */
export interface ApiCreateInvitation {
  email?: string | null
}

/** 来源：`backend/ShineMusic.Api/Contracts/CatalogDtos.cs` */
export interface ApiCreateLyric {
  language?: string | null
  format?: string | null
  content: string
  isDefault: boolean
}

/** 来源：`backend/ShineMusic.Api/Contracts/UserLibraryDtos.cs` */
export interface ApiCreatePlaylist {
  name?: string | null
  description?: string | null
  artworkId?: string | null
}

/** 来源：`backend/ShineMusic.Api/Contracts/UserLibraryDtos.cs` */
export interface ApiCreateSongRequest {
  title?: string | null
  artistName?: string | null
  albumTitle?: string | null
  note?: string | null
}

/** 来源：`backend/ShineMusic.Api/Contracts/SourceDownloadDtos.cs` */
export interface ApiCreateSourceDownloadTasks {
  provider: string
  providerTrackIds: readonly string[]
}

/** 来源：`backend/ShineMusic.Api/Contracts/SourceDownloadDtos.cs` */
export interface ApiCreateSourceDownloadTasksResult {
  created: readonly ApiExternalDownloadTask[]
  skippedDuplicates: readonly string[]
}

/** 来源：`backend/ShineMusic.Api/Contracts/ProfileDtos.cs` */
export interface ApiCurrentUser {
  id: string
  email: string
  displayName: string
  roles: readonly string[]
  avatarUrl?: string | null
}

/** 来源：`backend/ShineMusic.Api/Contracts/ListeningDtos.cs` */
export interface ApiDailyRecommendation {
  date: string
  personalized: boolean
  tracks: readonly ApiTrack[]
}

/** 来源：`backend/ShineMusic.Api/Contracts/SourceDownloadDtos.cs` */
export interface ApiExternalDownloadTask {
  id: string
  provider: string
  providerTrackId: string
  title?: string | null
  artists: readonly string[]
  albumTitle?: string | null
  durationMs?: number | null
  quality?: string | null
  license?: string | null
  sourcePageUrl?: string | null
  coverUrl?: string | null
  status: string
  autoScore?: number | null
  autoPublished: boolean
  errorCode?: string | null
  errorMessage?: string | null
  autoRetryCount: number
  nextRetryAtUtc?: string | null
  importItemId?: string | null
  createdAtUtc: string
  updatedAtUtc: string
}

/** 来源：`backend/ShineMusic.Api/Contracts/SourceDownloadDtos.cs` */
export interface ApiExternalDownloadTaskList {
  items: readonly ApiExternalDownloadTask[]
  total: number
  page: number
  pageSize: number
}

/** 来源：`backend/ShineMusic.Api/Contracts/ListeningDtos.cs` */
export interface ApiFeedback {
  kind: string
}

/** 来源：`backend/ShineMusic.Api/Contracts/SocialDtos.cs` */
export interface ApiFollowStatus {
  isFollowing: boolean
  followersCount: number
  followingCount: number
}

/** 来源：`backend/ShineMusic.Api/Contracts/LibraryImportDtos.cs` */
export interface ApiImportMetadata {
  title?: string | null
  artists: readonly string[]
  albumTitle?: string | null
  trackNumber?: number | null
  discNumber?: number | null
  releaseYear?: number | null
  genres: readonly string[]
  durationMs?: number | null
  container?: string | null
  codec?: string | null
  bitrateKbps?: number | null
  artworkAvailable: boolean
  lyrics?: string | null
  lyricsSource?: string | null
  lyricsConfidence?: number | null
}

/** 来源：`backend/ShineMusic.Api/Contracts/LibraryImportDtos.cs` */
export interface ApiImportSourceFile {
  displayName: string
  sizeBytes: number
  container?: string | null
  codec?: string | null
}

/** 来源：`backend/ShineMusic.Api/Contracts/LibraryImportDtos.cs` */
export interface ApiImportTranscode {
  status?: string | null
  outputFormat?: string | null
  bitrateKbps?: number | null
  errorCode?: string | null
}

/** 来源：`backend/ShineMusic.Api/Endpoints/InvitationEndpoints.cs` */
export interface ApiInvitationCode {
  code: string
  invitation: ApiInvitation
}

/** 来源：`backend/ShineMusic.Api/Endpoints/InvitationEndpoints.cs` */
export interface ApiInvitation {
  id: string
  email?: string | null
  status: string
  createdAt: string
  expiresAt: string
  sentAt?: string | null
  redeemedAt?: string | null
}

/** 来源：`backend/ShineMusic.Api/Endpoints/AuthEndpoints.cs` */
export interface ApiInvitationValidation {
  email: string
  channel: string
  expiresAt: string
}

/** 来源：`backend/ShineMusic.Api/Contracts/LibraryImportDtos.cs` */
export interface ApiLibraryImportBatch {
  id: string
  status: string
  items: readonly ApiLibraryImportItem[]
  createdAt: string
}

/** 来源：`backend/ShineMusic.Api/Contracts/LibraryImportDtos.cs` */
export interface ApiLibraryImportItem {
  id: string
  status: string
  sourceFile: ApiImportSourceFile
  metadata: ApiImportMetadata
  transcode: ApiImportTranscode
  warnings: readonly string[]
  createdAt: string
  updatedAt: string
  songRequestId?: string | null
  songRequestMatch?: ApiSongRequestMatch | null
}

/** 来源：`backend/ShineMusic.Api/Contracts/LibraryImportDtos.cs` */
export interface ApiLibraryImportItemWithUploader {
  item: ApiLibraryImportItem
  uploadedByUserId?: string | null
}

/** 来源：`backend/ShineMusic.Api/Contracts/ListeningDtos.cs` */
export interface ApiListeningHistoryItem {
  track: ApiTrack
  lastPlayedAtUtc: string
  playCount: number
}

/** 来源：`backend/ShineMusic.Api/Endpoints/AuthEndpoints.cs` */
export interface ApiLogin {
  email?: string | null
  username?: string | null
  password: string
}

/** 来源：`backend/ShineMusic.Api/Contracts/CatalogDtos.cs` */
export interface ApiLyric {
  id: string
  trackId: string
  language?: string | null
  format: string
  content: string
  isDefault: boolean
  updatedAtUtc: string
}

/** 来源：`backend/ShineMusic.Api/Contracts/CatalogDtos.cs` */
export interface ApiMusicHome {
  featured?: ApiTrack | null
  collections: readonly ApiCollectionSummary[]
  recentlyAdded: ApiPageResult<ApiTrack>
}

/** 来源：`backend/ShineMusic.Api/Contracts/NotificationDtos.cs` */
export interface ApiNotification {
  id: string
  kind: string
  title: string
  body?: string | null
  linkUrl?: string | null
  resourceType?: string | null
  resourceId?: string | null
  isRead: boolean
  createdAt: string
}

/** 来源：`backend/ShineMusic.Api/Contracts/CatalogDtos.cs` */
export interface ApiPageResult<T> {
  items: readonly T[]
  page: number
  pageSize: number
  total: number
}

/** 来源：`backend/ShineMusic.Api/Contracts/ListeningDtos.cs` */
export interface ApiPlayEvent {
  trackId: string
  eventType: string
  positionMs?: number | null
}

/** 来源：`backend/ShineMusic.Api/Contracts/UserLibraryDtos.cs` */
export interface ApiPlaylistDetail {
  id: string
  name: string
  description?: string | null
  tracks: readonly ApiTrack[]
  updatedAt: string
  artworkUrl?: string | null
}

/** 来源：`backend/ShineMusic.Api/Contracts/UserLibraryDtos.cs` */
export interface ApiPlaylistSummary {
  id: string
  name: string
  description?: string | null
  trackCount: number
  updatedAt: string
  artworkUrl?: string | null
}

/** 来源：`backend/ShineMusic.Api/Contracts/UserUploadDtos.cs` */
export interface ApiPrecheckMatch {
  trackId: string
  title: string
  artists: readonly string[]
  isPublic: boolean
  artworkUrl?: string | null
}

/** 来源：`backend/ShineMusic.Api/Contracts/LibraryImportDtos.cs` */
export interface ApiPublishImportItemResult {
  trackId: string
  status: string
}

/** 来源：`backend/ShineMusic.Api/Contracts/UserUploadDtos.cs` */
export interface ApiQueuedUpload {
  itemId: string
  displayName: string
}

/** 来源：`backend/ShineMusic.Api/Contracts/UserUploadDtos.cs` */
export interface ApiReferencedTrack {
  trackId: string
  title: string
  artists: readonly string[]
}

/** 来源：`backend/ShineMusic.Api/Endpoints/AuthEndpoints.cs` */
export interface ApiRefreshToken {
  refreshToken?: string | null
}

/** 来源：`backend/ShineMusic.Api/Endpoints/AuthEndpoints.cs` */
export interface ApiRegister {
  invitationToken?: string | null
  password: string
  displayName: string
  email?: string | null
}

/** 来源：`backend/ShineMusic.Api/Contracts/UserUploadDtos.cs` */
export interface ApiRejectedUpload {
  displayName: string
  reason: string
}

/** 来源：`backend/ShineMusic.Api/Contracts/UserLibraryDtos.cs` */
export interface ApiReopenSongRequest {
  reason?: string | null
}

/** 来源：`backend/ShineMusic.Api/Endpoints/CollectionManagementEndpoints.cs` */
export interface ApiReplaceCollectionTracks {
  trackIds: readonly string[]
}

/** 来源：`backend/ShineMusic.Api/Contracts/UserLibraryDtos.cs` */
export interface ApiSongRequest {
  id: string
  title: string
  artistName: string
  albumTitle?: string | null
  status: string
  voteCount: number
  myVote: boolean
  matchedTrackId?: string | null
  createdAt: string
  updatedAt: string
  createdByMe: boolean
}

/** 来源：`backend/ShineMusic.Api/Contracts/LibraryImportDtos.cs` */
export interface ApiSongRequestMatch {
  kind: string
  trackDisplay: string
  requestDisplay: string
  blocking: boolean
  message: string
}

/** 来源：`backend/ShineMusic.Api/Contracts/UserLibraryDtos.cs` */
export interface ApiSongRequestTimelineItem {
  id: string
  kind: string
  fromStatus?: string | null
  toStatus?: string | null
  actorKind: string
  note?: string | null
  createdAt: string
}

/** 来源：`backend/ShineMusic.Api/Contracts/SourceDownloadDtos.cs` */
export interface ApiSourceProviderError {
  provider: string
  message: string
}

/** 来源：`backend/ShineMusic.Api/Contracts/SourceDownloadDtos.cs` */
export interface ApiSourceSearchItem {
  provider: string
  providerTrackId: string
  title: string
  artists: readonly string[]
  albumTitle?: string | null
  durationMs?: number | null
  genres: readonly string[]
  coverUrl?: string | null
  sourcePageUrl?: string | null
  license?: string | null
  quality: string
  isLosslessAvailable: boolean
}

/** 来源：`backend/ShineMusic.Api/Contracts/SourceDownloadDtos.cs` */
export interface ApiSourceSearch {
  results: readonly ApiSourceSearchItem[]
  providerErrors: readonly ApiSourceProviderError[]
}

/** 来源：`backend/ShineMusic.Api/Contracts/StorageDtos.cs` */
export interface ApiStorageAudit {
  referencedFileCount: number
  diskFileCount: number
  missingFileCount: number
  orphanFileCount: number
  reclaimableBytes: number
  emptyDirectoryCount: number
  orphans: readonly ApiStorageFileRef[]
  missing: readonly ApiStorageFileRef[]
  emptyDirectories: readonly string[]
}

/** 来源：`backend/ShineMusic.Api/Contracts/StorageDtos.cs` */
export interface ApiStorageCleanup {
  deletedOrphanCount: number
  deletedBytes: number
  removedEmptyDirectoryCount: number
}

/** 来源：`backend/ShineMusic.Api/Contracts/StorageDtos.cs` */
export interface ApiStorageFileRef {
  storageKey: string
  sizeBytes: number
}

/** 来源：`backend/ShineMusic.Api/Contracts/CatalogDtos.cs` */
export interface ApiSystemCapabilities {
  anonymousPlayback: boolean
  userCollections: boolean
  songRequests: boolean
  adminLibraryImport: boolean
  manualReview: boolean
  editorialCollections: boolean
  listeningEvents: boolean
  personalizedRecommendations: boolean
  serverTranscoding: boolean
  userUploads: boolean
  allowedFormats: readonly string[]
  maxUploadSizeBytes?: number | null
  minimumPasswordStrength: number
}

/** 来源：`backend/ShineMusic.Api/Contracts/SocialDtos.cs` */
export interface ApiTrackComment {
  id: string
  trackId: string
  userId: string
  userName: string
  parentId?: string | null
  content: string
  createdAtUtc: string
  replies?: readonly ApiTrackComment[] | null
  likeCount: number
  likedByMe: boolean
}

/** 来源：`backend/ShineMusic.Api/Contracts/StorageDtos.cs` */
export interface ApiTrackDeleteResult {
  trackId: string
  title: string
  deletedFileCount: number
  deletedBytes: number
  artworkDeleted: boolean
}

/** 来源：`backend/ShineMusic.Api/Contracts/CatalogDtos.cs` */
export interface ApiTrack {
  id: string
  title: string
  artists: readonly ApiArtistSummary[]
  album?: ApiAlbumSummary | null
  trackNumber?: number | null
  discNumber?: number | null
  releaseYear?: number | null
  genres: readonly string[]
  durationMs?: number | null
  artworkUrl?: string | null
  audio?: ApiAudio | null
}

/** 来源：`backend/ShineMusic.Api/Contracts/NotificationDtos.cs` */
export interface ApiUnreadNotificationCount {
  count: number
}

/** 来源：`backend/ShineMusic.Api/Endpoints/CatalogManagementEndpoints.cs` */
export interface ApiUpdateAdminAlbum {
  title?: string | null
  artists?: readonly string[] | null
  releaseYear?: number | null
  artworkId?: string | null
}

/** 来源：`backend/ShineMusic.Api/Endpoints/CatalogManagementEndpoints.cs` */
export interface ApiUpdateAdminArtist {
  name?: string | null
  biography?: string | null
}

/** 来源：`backend/ShineMusic.Api/Endpoints/CatalogManagementEndpoints.cs` */
export interface ApiUpdateAdminTrack {
  title?: string | null
  artists?: readonly string[] | null
  albumId?: string | null
  trackNumber?: number | null
  discNumber?: number | null
  releaseYear?: number | null
  genres?: readonly string[] | null
  artworkId?: string | null
}

/** 来源：`backend/ShineMusic.Api/Endpoints/CollectionManagementEndpoints.cs` */
export interface ApiUpdateCollection {
  title?: string | null
  description?: string | null
  isPublished?: boolean | null
  artworkId?: string | null
}

/** 来源：`backend/ShineMusic.Api/Contracts/ProfileDtos.cs` */
export interface ApiUpdateDisplayName {
  displayName: string
}

/** 来源：`backend/ShineMusic.Api/Contracts/LibraryImportDtos.cs` */
export interface ApiUpdateImportItem {
  title?: string | null
  artists?: readonly string[] | null
  albumTitle?: string | null
  trackNumber?: number | null
  discNumber?: number | null
  releaseYear?: number | null
  genres?: readonly string[] | null
  lyrics?: string | null
  songRequestId?: string | null
}

/** 来源：`backend/ShineMusic.Api/Contracts/CatalogDtos.cs` */
export interface ApiUpdateLyric {
  language?: string | null
  format?: string | null
  content?: string | null
  isDefault?: boolean | null
}

/** 来源：`backend/ShineMusic.Api/Contracts/UserLibraryDtos.cs` */
export interface ApiUpdatePlaylist {
  name?: string | null
  description?: string | null
  artworkId?: string | null
  clearArtwork: boolean
}

/** 来源：`backend/ShineMusic.Api/Contracts/UserLibraryDtos.cs` */
export interface ApiUpdateSongRequest {
  status?: string | null
  adminNote?: string | null
  matchedTrackId?: string | null
}

/** 来源：`backend/ShineMusic.Api/Contracts/ProfileDtos.cs` */
export interface ApiUpdateUserTheme {
  accent?: string | null
  accent2?: string | null
  accent3?: string | null
  maskOpacity: number
  enabled: boolean
}

/** 来源：`backend/ShineMusic.Api/Contracts/UserUploadDtos.cs` */
export interface ApiUploadPrecheckFile {
  fileName: string
  sizeBytes?: number | null
  title?: string | null
  artist?: string | null
  durationMs?: number | null
}

/** 来源：`backend/ShineMusic.Api/Contracts/UserUploadDtos.cs` */
export interface ApiUploadPrecheckRequest {
  files: readonly ApiUploadPrecheckFile[]
}

/** 来源：`backend/ShineMusic.Api/Contracts/UserUploadDtos.cs` */
export interface ApiUploadPrecheckResponse {
  results: readonly ApiUploadPrecheckResult[]
}

/** 来源：`backend/ShineMusic.Api/Contracts/UserUploadDtos.cs` */
export interface ApiUploadPrecheckResult {
  fileName: string
  match?: ApiPrecheckMatch | null
}

/** 来源：`backend/ShineMusic.Api/Contracts/ListeningDtos.cs` */
export interface ApiUserFeedback {
  trackId: string
  kind: string
  updatedAtUtc: string
}

/** 来源：`backend/ShineMusic.Api/Contracts/UserUploadDtos.cs` */
export interface ApiUserLibraryItem {
  trackId: string
  title: string
  artists: readonly string[]
  albumTitle?: string | null
  durationMs?: number | null
  artworkUrl?: string | null
  isPublic: boolean
  addedAtUtc: string
}

/** 来源：`backend/ShineMusic.Api/Contracts/SocialDtos.cs` */
export interface ApiUserSummary {
  id: string
  displayName: string
}

/** 来源：`backend/ShineMusic.Api/Contracts/ProfileDtos.cs` */
export interface ApiUserTheme {
  accent?: string | null
  accent2?: string | null
  accent3?: string | null
  maskOpacity: number
  enabled: boolean
  wallpaperUrl?: string | null
}

/** 来源：`backend/ShineMusic.Api/Contracts/UserUploadDtos.cs` */
export interface ApiUserUploadResponse {
  batchId?: string | null
  referenced: readonly ApiReferencedTrack[]
  queued: readonly ApiQueuedUpload[]
  rejected: readonly ApiRejectedUpload[]
}

/** 来源：`backend/ShineMusic.Api/Endpoints/AuthEndpoints.cs` */
export interface ApiValidateInvitation {
  invitationToken?: string | null
}
