<script setup lang="ts">
/**
 * CommentSection：曲目评论区（楼中楼式，贴近社交平台交互）。
 *
 * 数据流：
 * - 读：GET /tracks/{trackId}/comments（匿名分页），顶层评论内嵌 replies；
 * - 写：POST /tracks/{trackId}/comments，回复带 parentId（须为顶层评论），成功后直接插入列表；
 * - 删：DELETE /comments/{id}，本人或管理员可删。
 *
 * 交互（对齐参考图）：
 * - 顶部「写评论」输入框发表新评论；
 * - 每条评论底部有「回复」按钮，点击后在该评论下方展开内联输入框；
 * - 回复提交后直接显示在该评论的回复列表里（不整页重载）；
 * - 存在回复时显示「共 N 条回复」可展开 / 折叠；
 * - 本人 / 管理员评论显示删除按钮。
 */
import { computed, onMounted, reactive, ref } from 'vue'
import { ChevronDown, ChevronUp, CornerDownRight, Heart, MessageCircle, Send, Trash2 } from 'lucide-vue-next'
import EmptyState from '@/components/EmptyState.vue'
import { createComment, deleteComment, getComments, likeComment, unlikeComment } from '@/services/comments'
import { currentUser, isAuthenticated, requireLogin } from '@/services/session'
import type { ApiComment } from '@/types/music'

/** 前端可变评论节点（replies 可由内联回复动态追加）。 */
interface CommentNode {
  id: string
  trackId: string
  userId: string
  userName: string
  parentId?: string | null
  content: string
  createdAtUtc: string
  likeCount: number
  likedByMe: boolean
  replies: CommentNode[]
}

const props = defineProps<{ trackId: string }>()

const comments = ref<CommentNode[]>([])
const loading = ref(true)
const loadError = ref(false)
const total = ref(0)
const page = ref(1)
const pageSize = 20

// 顶部新评论输入
const draft = ref('')
const submittingTop = ref(false)

// 内联回复状态
const replyingTo = ref<CommentNode | null>(null)
const replyDraft = ref('')
const submittingReply = ref(false)

// 回复展开/折叠状态（记录顶层评论 id）
const expanded = reactive<Record<string, boolean>>({})

const hasMore = computed(() => comments.value.length < total.value)

/** 当前用户是否为评论作者或管理员。 */
function canDelete(comment: CommentNode): boolean {
  if (!isAuthenticated.value) return false
  const user = currentUser.value
  return user?.id === comment.userId || user?.roles.includes('admin') === true
}

/** 把 UTC 时间转为「刚刚 / n 分钟前 / n 小时前 / n 天前 / 日期」。 */
function relativeTime(iso: string): string {
  const time = new Date(iso).getTime()
  if (!Number.isFinite(time)) return ''
  const diff = Date.now() - time
  const min = Math.floor(diff / 60000)
  if (min < 1) return '刚刚'
  if (min < 60) return `${min} 分钟前`
  const hours = Math.floor(min / 60)
  if (hours < 24) return `${hours} 小时前`
  const days = Math.floor(hours / 24)
  if (days < 7) return `${days} 天前`
  const d = new Date(time)
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

/** 后端只读 DTO → 前端可变节点。 */
function toNode(comment: ApiComment): CommentNode {
  return {
    id: comment.id,
    trackId: comment.trackId,
    userId: comment.userId,
    userName: comment.userName,
    parentId: comment.parentId,
    content: comment.content,
    createdAtUtc: comment.createdAtUtc,
    likeCount: comment.likeCount,
    likedByMe: comment.likedByMe,
    replies: (comment.replies ?? []).map(toNode),
  }
}

function isExpanded(comment: CommentNode): boolean {
  return expanded[comment.id] === true
}

function toggleReplies(comment: CommentNode) {
  expanded[comment.id] = !isExpanded(comment)
}

/** 拉取顶层评论（分页），reset=true 时清空重载。 */
async function load(reset = false) {
  if (reset) {
    loading.value = true
    page.value = 1
  }
  loadError.value = false
  try {
    const result = await getComments(props.trackId, reset ? 1 : page.value, pageSize)
    const incoming = (result.items ?? []).map(toNode)
    comments.value = reset ? incoming : [...comments.value, ...incoming]
    total.value = result.total
    page.value += 1
  } catch {
    loadError.value = true
  } finally {
    loading.value = false
  }
}

/** 点击「回复」：需登录，聚焦该评论的内联输入框。 */
function startReply(comment: CommentNode) {
  if (!requireLogin()) return
  replyingTo.value = comment
  replyDraft.value = ''
  // 回复时自动展开回复列表，便于看到提交结果
  // 放在 nextTick 由模板渲染；此处直接标记展开
  expanded[comment.id] = true
}

function cancelReply() {
  replyingTo.value = null
  replyDraft.value = ''
}

/** 发表顶层新评论。 */
async function submitTop() {
  const content = draft.value.trim()
  if (!content || submittingTop.value) return
  if (!requireLogin()) return
  submittingTop.value = true
  try {
    const created = await createComment(props.trackId, content)
    comments.value = [toNode(created), ...comments.value]
    total.value += 1
    draft.value = ''
  } catch {
    uni.showToast({ title: '发表失败，请稍后再试', icon: 'none' })
  } finally {
    submittingTop.value = false
  }
}

/** 提交内联回复，成功后直接插入该评论的回复列表末尾。 */
async function submitReply() {
  const content = replyDraft.value.trim()
  const parent = replyingTo.value
  if (!content || !parent || submittingReply.value) return
  if (!requireLogin()) return
  submittingReply.value = true
  try {
    const created = await createComment(props.trackId, content, parent.id)
    parent.replies.push(toNode(created))
    expanded[parent.id] = true
    replyDraft.value = ''
    replyingTo.value = null
  } catch {
    uni.showToast({ title: '回复失败，请稍后再试', icon: 'none' })
  } finally {
    submittingReply.value = false
  }
}

/** 删除某条评论（顶层或回复），带确认。 */
function remove(target: CommentNode, parent?: CommentNode) {
  uni.showModal({
    title: '删除评论',
    content: '删除后不可恢复，确定删除这条评论吗？',
    confirmText: '删除',
    confirmColor: '#e05a3f',
    success: async ({ confirm }) => {
      if (!confirm) return
      try {
        await deleteComment(target.id)
        if (parent) {
          parent.replies = parent.replies.filter((item) => item.id !== target.id)
        } else {
          comments.value = comments.value.filter((item) => item.id !== target.id)
          total.value = Math.max(0, total.value - 1)
        }
        uni.showToast({ title: '已删除', icon: 'none' })
      } catch {
        uni.showToast({ title: '删除失败，请稍后再试', icon: 'none' })
      }
    },
  })
}

/** 点赞/取消点赞，乐观更新后以服务端返回值为准。 */
async function toggleLike(comment: CommentNode) {
  if (!requireLogin()) return
  const wasLiked = comment.likedByMe
  comment.likedByMe = !wasLiked
  comment.likeCount = Math.max(0, comment.likeCount + (wasLiked ? -1 : 1))
  try {
    const result = wasLiked ? await unlikeComment(comment.id) : await likeComment(comment.id)
    comment.likeCount = result.likeCount
    comment.likedByMe = result.likedByMe
  } catch {
    comment.likedByMe = wasLiked
    comment.likeCount = Math.max(0, comment.likeCount + (wasLiked ? 1 : -1))
    uni.showToast({ title: '操作失败，请稍后再试', icon: 'none' })
  }
}

/** 加载更多。 */
function loadMore() {
  if (hasMore.value && !loading.value) void load()
}

onMounted(() => void load(true))
</script>

<template>
  <view class="comment-section">
    <view class="cs-head">
      <view class="cs-title"><MessageCircle :size="20" /><text>评论 {{ total }} 条</text></view>
    </view>

    <!-- 顶部写评论 -->
    <view class="cs-composer">
      <view class="cs-input-row">
        <view class="cs-avatar cs-avatar--sm">{{ currentUser?.displayName?.slice(0, 1) || '我' }}</view>
        <input
          v-model="draft"
          class="cs-input"
          placeholder="写下你的评论…"
          confirm-type="send"
          maxlength="500"
          @confirm="submitTop"
        />
        <view class="cs-send" :class="{ disabled: !draft.trim() || submittingTop }" @tap="submitTop">
          <Send :size="18" />
        </view>
      </view>
    </view>

    <!-- 列表态 -->
    <view v-if="loading" class="cs-state">加载评论中…</view>
    <view v-else-if="loadError" class="cs-state">评论加载失败，下拉重试</view>
    <template v-else>
      <EmptyState
        v-if="!comments.length"
        :icon="MessageCircle"
        title="还没有评论"
        hint="抢占头排，来聊聊这首歌吧"
        compact
      />
      <view v-else class="cs-list">
        <view v-for="comment in comments" :key="comment.id" class="cs-item">
          <view class="cs-avatar">{{ comment.userName.slice(0, 1) }}</view>
          <view class="cs-main">
            <view class="cs-meta">
              <text class="cs-name">{{ comment.userName }}</text>
              <text class="cs-time">{{ relativeTime(comment.createdAtUtc) }}</text>
            </view>
            <text class="cs-content">{{ comment.content }}</text>

            <!-- 操作栏：点赞 + 回复 + 回复数折叠 + 删除 -->
            <view class="cs-ops">
              <view class="cs-op cs-op--like" :class="{ liked: comment.likedByMe }" @tap="toggleLike(comment)">
                <Heart :size="16" :fill="comment.likedByMe ? 'currentColor' : 'none'" />
                <text v-if="comment.likeCount > 0">{{ comment.likeCount }}</text>
              </view>
              <text class="cs-op cs-op--reply" @tap="startReply(comment)">回复</text>
              <text v-if="comment.replies.length" class="cs-op cs-op--toggle" @tap="toggleReplies(comment)">
                {{ isExpanded(comment) ? '收起' : `共 ${comment.replies.length} 条回复` }}
                <ChevronUp v-if="isExpanded(comment)" :size="14" />
                <ChevronDown v-else :size="14" />
              </text>
              <text v-if="canDelete(comment)" class="cs-op cs-op--danger" @tap="remove(comment)">
                <Trash2 :size="14" />
              </text>
            </view>

            <!-- 回复区（展开时显示） -->
            <view v-if="isExpanded(comment) && comment.replies.length" class="cs-replies">
              <view v-for="reply in comment.replies" :key="reply.id" class="cs-reply">
                <view class="cs-reply-avatar">{{ reply.userName.slice(0, 1) }}</view>
                <view class="cs-reply-main">
                  <view class="cs-meta">
                    <text class="cs-name cs-name--sm">{{ reply.userName }}</text>
                    <text class="cs-time">{{ relativeTime(reply.createdAtUtc) }}</text>
                  </view>
                  <view class="cs-reply-body">
                    <text class="cs-reply-content">{{ reply.content }}</text>
                    <text v-if="canDelete(reply)" class="cs-op cs-op--danger cs-op--inline" @tap="remove(reply, comment)">
                      <Trash2 :size="13" />
                    </text>
                  </view>
                  <view class="cs-reply-ops">
                    <view class="cs-op cs-op--like" :class="{ liked: reply.likedByMe }" @tap="toggleLike(reply)">
                      <Heart :size="14" :fill="reply.likedByMe ? 'currentColor' : 'none'" />
                      <text v-if="reply.likeCount > 0">{{ reply.likeCount }}</text>
                    </view>
                    <text class="cs-op cs-op--reply" @tap="startReply(comment)">回复</text>
                  </view>
                </view>
              </view>
            </view>

            <!-- 内联回复输入框 -->
            <view v-if="replyingTo?.id === comment.id" class="cs-inline-reply">
              <view class="cs-reply-hint">
                <CornerDownRight :size="14" />
                <text>回复 @{{ comment.userName }}</text>
                <text class="cs-reply-cancel" @tap="cancelReply">取消</text>
              </view>
              <view class="cs-input-row">
                <input
                  v-model="replyDraft"
                  class="cs-input"
                  :placeholder="`回复 ${comment.userName}…`"
                  confirm-type="send"
                  maxlength="500"
                  @confirm="submitReply"
                />
                <view class="cs-send" :class="{ disabled: !replyDraft.trim() || submittingReply }" @tap="submitReply">
                  <Send :size="18" />
                </view>
              </view>
            </view>
          </view>
        </view>
      </view>
      <view v-if="hasMore" class="cs-more" @tap="loadMore">加载更多</view>
    </template>
  </view>
</template>

<style lang="scss" scoped>
.comment-section { padding: 8rpx 0 20rpx; }
.cs-head { padding: 10rpx 0 18rpx; }
.cs-title { display: flex; align-items: center; gap: 10rpx; color: var(--sm-text); font-size: 27rpx; font-weight: 800; }

.cs-composer { margin-bottom: 24rpx; padding: 16rpx 18rpx; border-radius: var(--sm-radius-card); background: var(--sm-surface-2); border: 1rpx solid var(--sm-border); }
.cs-reply-hint { display: flex; align-items: center; gap: 8rpx; margin-bottom: 12rpx; padding: 10rpx 14rpx; border-radius: 12rpx; background: var(--sm-accent-soft); color: var(--sm-accent); font-size: 17rpx; }
.cs-reply-cancel { margin-left: auto; color: var(--sm-text-2); }
.cs-input-row { display: flex; align-items: center; gap: 14rpx; }
.cs-input { flex: 1; height: 72rpx; padding: 0 22rpx; border-radius: 36rpx; background: var(--sm-surface); border: 1rpx solid var(--sm-border-strong); color: var(--sm-text); font-size: 19rpx; }
.cs-send { display: grid; place-items: center; flex: none; width: 72rpx; height: 72rpx; border-radius: 50%; background: var(--sm-accent); color: var(--sm-accent-ink); }
.cs-send.disabled { opacity: .45; }

.cs-state { padding: 60rpx 20rpx; color: var(--sm-text-2); font-size: 18rpx; text-align: center; }

.cs-list { display: flex; flex-direction: column; }
.cs-item { display: flex; gap: 18rpx; padding: 24rpx 4rpx; border-bottom: 1rpx solid var(--sm-divider); }
.cs-avatar { flex: none; display: grid; place-items: center; width: 68rpx; height: 68rpx; border-radius: 50%; background: var(--sm-accent-grad); color: var(--sm-accent-ink); font-size: 26rpx; font-weight: 800; }
.cs-avatar--sm { width: 60rpx; height: 60rpx; font-size: 22rpx; }
.cs-main { flex: 1; min-width: 0; }
.cs-meta { display: flex; align-items: baseline; gap: 14rpx; }
.cs-name { color: var(--sm-text-2); font-size: 21rpx; font-weight: 700; }
.cs-name--sm { font-size: 19rpx; }
.cs-time { color: var(--sm-text-3); font-size: 16rpx; }
.cs-content { display: block; margin-top: 8rpx; color: var(--sm-text); font-size: 23rpx; line-height: 1.6; word-break: break-word; }

.cs-ops { display: flex; align-items: center; gap: 26rpx; margin-top: 14rpx; }
.cs-op { display: inline-flex; align-items: center; gap: 4rpx; color: var(--sm-text-3); font-size: 17rpx; }
.cs-op--toggle { color: var(--sm-text-2); }
.cs-op--reply { color: var(--sm-text-2); }
.cs-op--danger { color: var(--sm-danger); }
.cs-op--inline { margin-left: 8rpx; vertical-align: -2rpx; }
.cs-op--like { color: var(--sm-text-2); transition: color .15s ease; }
.cs-op--like.liked { color: var(--sm-danger); }

/* 回复区 */
.cs-replies { margin-top: 14rpx; padding: 12rpx 16rpx; border-radius: 18rpx; background: var(--sm-surface-3); }
.cs-reply { display: flex; gap: 14rpx; padding: 12rpx 0; }
.cs-reply + .cs-reply { border-top: 1rpx solid var(--sm-divider); }
.cs-reply-avatar { flex: none; display: grid; place-items: center; width: 44rpx; height: 44rpx; border-radius: 50%; background: var(--sm-accent-soft); color: var(--sm-accent); font-size: 19rpx; font-weight: 800; }
.cs-reply-main { flex: 1; min-width: 0; }
.cs-reply-body { display: flex; align-items: baseline; margin-top: 6rpx; }
.cs-reply-content { flex: 1; color: var(--sm-text); font-size: 21rpx; line-height: 1.55; word-break: break-word; }
.cs-reply-ops { display: flex; align-items: center; gap: 22rpx; margin-top: 10rpx; }

/* 内联回复输入框 */
.cs-inline-reply { margin-top: 14rpx; padding: 14rpx 16rpx; border-radius: 18rpx; background: var(--sm-surface-2); border: 1rpx solid var(--sm-border); }

.cs-more { padding: 26rpx 0 6rpx; color: var(--sm-accent); font-size: 18rpx; text-align: center; }
</style>