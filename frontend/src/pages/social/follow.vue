<script setup lang="ts">
/**
 * 关注 / 粉丝页：顶部切换「关注中」与「粉丝」两个分组。
 * - 关注中：GET /me/following，逐条可取消关注；
 * - 粉丝：  GET /me/followers，只读列表（展示谁在关注你）。
 */
import { ref } from 'vue'
import { onLoad } from '@dcloudio/uni-app'
import { UserRound, Users } from 'lucide-vue-next'
import EmptyState from '@/components/EmptyState.vue'
import PlayerBar from '@/components/PlayerBar.vue'
import { getFollowers, getFollowing, unfollowUser } from '@/services/follow'
import { isAuthenticated } from '@/services/session'
import type { ApiUserBrief } from '@/types/music'

type Tab = 'following' | 'followers'

const tab = ref<Tab>('following')
const following = ref<ApiUserBrief[]>([])
const followers = ref<ApiUserBrief[]>([])
const loading = ref(true)
const loadError = ref(false)

async function load() {
  if (!isAuthenticated.value) {
    loading.value = false
    return
  }
  loading.value = true
  loadError.value = false
  try {
    const [followingResult, followersResult] = await Promise.all([
      getFollowing(1, 100),
      getFollowers(1, 100),
    ])
    following.value = [...followingResult.items]
    followers.value = [...followersResult.items]
  } catch {
    loadError.value = true
  } finally {
    loading.value = false
  }
}

function switchTab(next: Tab) {
  tab.value = next
}

async function handleUnfollow(user: ApiUserBrief) {
  try {
    await unfollowUser(user.id)
    following.value = following.value.filter((item) => item.id !== user.id)
    uni.showToast({ title: '已取消关注', icon: 'none' })
  } catch {
    uni.showToast({ title: '操作失败，请稍后再试', icon: 'none' })
  }
}

function goLogin() {
  uni.navigateTo({ url: '/pages/auth/login' })
}

onLoad(() => void load())
</script>

<template>
  <view class="follow-page">
    <!-- 顶部切换 -->
    <view class="follow-tabs">
      <view class="ftab" :class="{ active: tab === 'following' }" @tap="switchTab('following')">
        关注中 {{ following.length }}
      </view>
      <view class="ftab" :class="{ active: tab === 'followers' }" @tap="switchTab('followers')">
        粉丝 {{ followers.length }}
      </view>
    </view>

    <view v-if="loading" class="page-state">加载中…</view>
    <view v-else-if="loadError" class="page-state">加载失败，请稍后再试</view>
    <template v-else>
      <EmptyState
        v-if="!isAuthenticated"
        :icon="UserRound"
        title="登录后查看关注"
        hint="登录后即可关注喜欢的歌手与用户"
        action-text="去登录"
        @action="goLogin"
      />

      <!-- 关注中 -->
      <template v-else-if="tab === 'following'">
        <EmptyState
          v-if="!following.length"
          :icon="UserRound"
          title="还没有关注任何人"
          hint="关注用户，第一时间发现他们的动态"
          compact
        />
        <view v-else class="user-list">
          <view v-for="user in following" :key="user.id" class="user-item">
            <view class="user-avatar">{{ user.displayName.slice(0, 1) }}</view>
            <text class="user-name">{{ user.displayName }}</text>
            <view class="user-action" @tap="handleUnfollow(user)">取消关注</view>
          </view>
        </view>
      </template>

      <!-- 粉丝 -->
      <template v-else>
        <EmptyState
          v-if="!followers.length"
          :icon="Users"
          title="还没有粉丝"
          hint="分享好歌，让更多人关注你"
          compact
        />
        <view v-else class="user-list">
          <view v-for="user in followers" :key="user.id" class="user-item">
            <view class="user-avatar">{{ user.displayName.slice(0, 1) }}</view>
            <text class="user-name">{{ user.displayName }}</text>
          </view>
        </view>
      </template>
    </template>
    <view class="page-bottom-spacer"></view>
  </view>
  <PlayerBar />
</template>

<style lang="scss" scoped>
.follow-page { min-height: 100vh; padding: 24rpx 40rpx 60rpx; background: var(--sm-page-bg); box-sizing: border-box; }

.follow-tabs { display: flex; gap: 12rpx; margin-bottom: 28rpx; padding: 8rpx; border-radius: 22rpx; background: var(--sm-surface-3); }
.ftab { flex: 1; padding: 18rpx 0; border-radius: 16rpx; color: var(--sm-text-2); font-size: 21rpx; font-weight: 700; text-align: center; transition: all .15s ease; }
.ftab.active { background: var(--sm-hero-ink-bg); color: var(--sm-hero-ink); }

.page-state { padding: 100rpx 20rpx; color: var(--sm-text-2); font-size: 19rpx; text-align: center; }

.user-list { display: flex; flex-direction: column; }
.user-item { display: flex; align-items: center; gap: 20rpx; min-height: 104rpx; padding: 14rpx 20rpx; border-radius: 22rpx; background: var(--sm-surface-2); }
.user-item + .user-item { margin-top: 14rpx; }
.user-avatar { flex: none; display: grid; place-items: center; width: 64rpx; height: 64rpx; border-radius: 50%; background: var(--sm-accent-grad); color: var(--sm-accent-ink); font-size: 24rpx; font-weight: 800; }
.user-name { flex: 1; min-width: 0; overflow: hidden; color: var(--sm-text); font-size: 24rpx; font-weight: 700; text-overflow: ellipsis; white-space: nowrap; }
.user-action { flex: none; padding: 12rpx 26rpx; border-radius: 999rpx; border: 1rpx solid var(--sm-border-strong); color: var(--sm-text-2); font-size: 18rpx; font-weight: 700; }
.page-bottom-spacer { height: 180rpx; }
</style>