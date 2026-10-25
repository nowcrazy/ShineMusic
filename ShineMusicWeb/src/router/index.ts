import { createRouter, createWebHashHistory, type RouteRecordRaw } from 'vue-router'
import { installRouter } from '@/platform/navigation'

const routes: RouteRecordRaw[] = [
  { path: '/', redirect: '/pages/home/index' },
  {
    path: '/pages/home/index',
    name: 'home',
    component: () => import('@/pages/home/index.vue'),
    meta: { title: 'Shine Music' },
  },
  {
    path: '/pages/catalog/collection',
    name: 'catalog-collection',
    component: () => import('@/pages/catalog/collection.vue'),
    meta: { title: '精选专题' },
  },
  {
    path: '/pages/catalog/album',
    name: 'catalog-album',
    component: () => import('@/pages/catalog/album.vue'),
    meta: { title: '专辑' },
  },
  {
    path: '/pages/catalog/artist',
    name: 'catalog-artist',
    component: () => import('@/pages/catalog/artist.vue'),
    meta: { title: '艺人' },
  },
  {
    path: '/pages/catalog/tracks',
    name: 'catalog-tracks',
    component: () => import('@/pages/catalog/tracks.vue'),
    meta: { title: '曲目' },
  },
  {
    path: '/pages/recommend/daily',
    name: 'recommend-daily',
    component: () => import('@/pages/recommend/daily.vue'),
    meta: { title: '每日推荐' },
  },
  {
    path: '/pages/playlist/detail',
    name: 'playlist-detail',
    component: () => import('@/pages/playlist/detail.vue'),
    meta: { title: '歌单' },
  },
  {
    path: '/pages/playlist/pick-track',
    name: 'playlist-pick-track',
    component: () => import('@/pages/playlist/pick-track.vue'),
    meta: { title: '添加歌曲' },
  },
  {
    path: '/pages/requests/index',
    name: 'requests',
    component: () => import('@/pages/requests/index.vue'),
    meta: { title: '歌曲需求' },
  },
  {
    path: '/pages/notifications/index',
    name: 'notifications',
    component: () => import('@/pages/notifications/index.vue'),
    meta: { title: '消息' },
  },
  {
    path: '/pages/track/detail',
    name: 'track-detail',
    component: () => import('@/pages/track/detail.vue'),
    meta: { title: '曲目详情' },
  },
  {
    path: '/pages/history/index',
    name: 'history',
    component: () => import('@/pages/history/index.vue'),
    meta: { title: '播放历史' },
  },
  {
    path: '/pages/library/favorites',
    name: 'favorites',
    component: () => import('@/pages/library/favorites.vue'),
    meta: { title: '我的收藏' },
  },
  {
    path: '/pages/social/follow',
    name: 'social-follow',
    component: () => import('@/pages/social/follow.vue'),
    meta: { title: '关注与粉丝' },
  },
  {
    path: '/pages/library/upload',
    name: 'library-upload',
    component: () => import('@/pages/library/upload.vue'),
    meta: { title: '上传本地音频' },
  },
  {
    path: '/pages/settings/theme',
    name: 'settings-theme',
    component: () => import('@/pages/settings/theme.vue'),
    meta: { title: '主题外观' },
  },
  {
    path: '/pages/profile/index',
    name: 'profile',
    component: () => import('@/pages/profile/index.vue'),
    meta: { title: '个人资料' },
  },
  {
    path: '/pages/admin/collections',
    name: 'admin-collections',
    component: () => import('@/pages/admin/collections.vue'),
    meta: { title: '歌单管理' },
  },
  {
    path: '/pages/admin/catalog',
    name: 'admin-catalog',
    component: () => import('@/pages/admin/catalog.vue'),
    meta: { title: '曲目维护' },
  },
  {
    path: '/pages/admin/import',
    name: 'admin-import',
    component: () => import('@/pages/admin/import.vue'),
    meta: { title: '曲库导入审核' },
  },
  {
    path: '/pages/admin/requests',
    name: 'admin-requests',
    component: () => import('@/pages/admin/requests.vue'),
    meta: { title: '需求管理' },
  },
  {
    path: '/pages/admin/invitation',
    name: 'admin-invitation',
    component: () => import('@/pages/admin/invitation.vue'),
    meta: { title: '邀请管理' },
  },
  {
    path: '/pages/admin/source-download',
    name: 'admin-source-download',
    component: () => import('@/pages/admin/source-download.vue'),
    meta: { title: '音源抓歌' },
  },
  {
    path: '/pages/auth/login',
    name: 'login',
    component: () => import('@/pages/auth/login.vue'),
    meta: { title: '登录' },
  },
  {
    path: '/pages/auth/register',
    name: 'register',
    component: () => import('@/pages/auth/register.vue'),
    meta: { title: '邀请注册' },
  },
  { path: '/:pathMatch(.*)*', redirect: '/pages/home/index' },
]

const router = createRouter({
  history: createWebHashHistory(),
  routes,
  scrollBehavior(_to, _from, savedPosition) {
    return savedPosition ?? { top: 0 }
  },
})

router.afterEach((to) => {
  const title = typeof to.meta.title === 'string' ? to.meta.title : ''
  document.title = title ? `${title} · Shine Music` : 'Shine Music'
})

installRouter(router)

export default router
