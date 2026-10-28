/**
 * 管理端日志服务：登录审计与操作审计查询。
 *
 * 对齐后端 /api/v1/admin/logs（仅管理员）：
 * - GET /admin/logs/login?result=&query=&page=&pageSize=       登录审计
 * - GET /admin/logs/operations?query=&page=&pageSize=          操作审计
 */
import { API_PREFIX, http } from './http'

export interface LoginAuditItem {
  id: string
  login: string
  userId: string | null
  result: 'success' | 'failure'
  reason: string
  ip: string
  clientType: string | null
  deviceInfo: string | null
  createdAtUtc: string
}

export interface OperationAuditItem {
  id: string
  actorUserId: string
  actorName: string | null
  action: string
  resourceType: string
  resourceId: string
  /** 被操作资源的可读名称（曲名 / 邮箱 / 歌单名等），无自然名称时为空 */
  resourceName: string | null
  detailsJson: string | null
  ip: string | null
  clientType: string | null
  deviceInfo: string | null
  createdAtUtc: string
}

export interface AuditPage<T> {
  items: T[]
  page: number
  pageSize: number
  total: number
}

/** 登录失败原因 → 中文标签。 */
export const LoginReasonLabel: Record<string, string> = {
  ok: '成功',
  account_not_found: '账号不存在',
  bad_password: '密码错误',
  account_locked: '账号已锁定',
  missing_credentials: '凭证缺失',
}

/** 客户端类型 → 中文标签。 */
export const ClientTypeLabel: Record<string, string> = {
  'web-admin': 'PC 后台',
  'web-app': 'PC 网页',
  app: 'App',
  unknown: '未知',
}

/** 登录审计分页列表。result 过滤 success / failure。 */
export function getLoginAuditLogs(
  result?: string,
  query?: string,
  page = 1,
  pageSize = 20,
): Promise<AuditPage<LoginAuditItem>> {
  const params = new URLSearchParams()
  if (result) params.set('result', result)
  if (query?.trim()) params.set('query', query.trim())
  params.set('page', String(page))
  params.set('pageSize', String(pageSize))
  return http.get<AuditPage<LoginAuditItem>>(`${API_PREFIX}/admin/logs/login?${params.toString()}`)
}

/** 操作审计分页列表。action 为动作前缀过滤（如 track），actor 可选 system / admin。 */
export function getOperationAuditLogs(
  query?: string,
  page = 1,
  pageSize = 20,
  action?: string,
  actor?: string,
): Promise<AuditPage<OperationAuditItem>> {
  const params = new URLSearchParams()
  if (query?.trim()) params.set('query', query.trim())
  if (action) params.set('action', action)
  if (actor) params.set('actor', actor)
  params.set('page', String(page))
  params.set('pageSize', String(pageSize))
  return http.get<AuditPage<OperationAuditItem>>(`${API_PREFIX}/admin/logs/operations?${params.toString()}`)
}

/** 审计动作 → 中文描述；未登记的动作回退为原始标识。 */
export const ActionLabel: Record<string, string> = {
  'track.auto_published': '自动发布曲目',
  'track.published': '发布曲目',
  'track.updated': '修改曲目',
  'track.deleted': '删除曲目',
  'album.updated': '修改专辑',
  'artist.updated': '修改艺人',
  'artwork.uploaded': '上传封面',
  'collection.created': '新建歌单',
  'collection.updated': '修改歌单',
  'collection.deleted': '删除歌单',
  'collection.tracks_replaced': '替换歌单曲目',
  'invitation.created': '创建邀请',
  'invitation.code_created': '生成邀请码',
  'invitation.resent': '重发邀请',
  'invitation.revoked': '撤销邀请',
  'library_import.created': '新建导入批次',
  'library_import_item.updated': '修改导入项',
  'library_import_item.enriched': '补全导入项元数据',
  'library_import_item.lyrics_uploaded': '上传导入项歌词',
  'library_import_item.song_request_unlinked': '解除点歌关联',
  'library_import_item.rejected': '驳回导入项',
  'library_import_item.dismissed': '忽略导入项',
  'lyric.created': '新建歌词',
  'lyric.updated': '修改歌词',
  'lyric.deleted': '删除歌词',
  'song_request.withdrawn': '撤回点歌',
  'song_request.reopened': '重新打开点歌',
  'song_request.updated': '更新点歌',
  'song_request.forced_match': '指定匹配曲目',
  'song_request.fuzzy_match': '模糊匹配曲目',
  'source_download.task_created': '创建下载任务',
  'source_download.task_retried': '重试下载任务',
  'storage.cleanup': '清理存储',
  'user.role_changed': '修改用户角色',
  'user.reset_password': '重置用户密码',
  'user.locked': '锁定用户',
  'user.unlocked': '解锁用户',
}

/** 资源类型 → 中文标签。 */
export const ResourceTypeLabel: Record<string, string> = {
  track: '曲目',
  album: '专辑',
  artist: '艺人',
  collection: '歌单',
  import_item: '导入项',
  import_batch: '导入批次',
  user: '用户',
  song_request: '点歌',
  invitation: '邀请',
  lyric: '歌词',
  artwork: '封面',
  source_download_task: '下载任务',
  storage: '存储',
}

/** 动作前缀筛选选项：后端按 `前缀.` 匹配。 */
export const ActionFilterOptions = [
  { value: 'track', label: '曲目' },
  { value: 'album', label: '专辑' },
  { value: 'artist', label: '艺人' },
  { value: 'library_import', label: '导入' },
  { value: 'song_request', label: '点歌' },
  { value: 'invitation', label: '邀请' },
  { value: 'user', label: '用户' },
  { value: 'collection', label: '歌单' },
  { value: 'lyric', label: '歌词' },
  { value: 'source_download', label: '音源任务' },
  { value: 'storage', label: '存储' },
]

/** 详情 JSON 键 → 中文标签（未登记的键原样显示）。 */
export const DetailKeyLabel: Record<string, string> = {
  importItemId: '导入项',
  fields: '变更字段',
  itemCount: '条目数',
  trackCount: '曲目数',
  role: '角色',
  status: '状态',
  extension: '扩展名',
  length: '字符数',
  reason: '原因',
  previousNote: '原备注',
  byAdmin: '管理员操作',
  promoted: '提升为正式曲目',
  trackId: '曲目',
  contentType: '内容类型',
  collection: '歌单',
  matchedTrackId: '匹配曲目',
  tempAdminUntilUtc: '临时管理员截止',
  removedFiles: '清理文件数',
  removedAssets: '清理资源数',
  dryRun: '试运行',
}
