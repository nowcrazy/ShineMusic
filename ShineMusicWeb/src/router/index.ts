import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router'
import { isAdmin, isAuthenticated, restoreSession } from '@/services/session'

const routes: RouteRecordRaw[] = [
  {
    path: '/login',
    name: 'login',
    component: () => import('@/pages/auth/LoginView.vue'),
    meta: { title: '登录', public: true },
  },
  {
    path: '/403',
    name: 'forbidden',
    component: () => import('@/pages/error/Forbidden.vue'),
    meta: { title: '无访问权限' },
  },
  {
    path: '/',
    component: () => import('@/layouts/AdminLayout.vue'),
    redirect: '/dashboard',
    children: [
      {
        path: 'dashboard',
        name: 'dashboard',
        component: () => import('@/pages/dashboard/index.vue'),
        meta: { title: '仪表盘' },
      },
      {
        path: 'catalog',
        name: 'catalog',
        component: () => import('@/pages/admin/catalog.vue'),
        meta: { title: '曲库管理' },
      },
      {
        path: 'import',
        name: 'import',
        component: () => import('@/pages/admin/import.vue'),
        meta: { title: '导入审核' },
      },
      {
        path: 'requests',
        name: 'requests',
        component: () => import('@/pages/admin/requests.vue'),
        meta: { title: '点歌需求' },
      },
      {
        path: 'collections',
        name: 'collections',
        component: () => import('@/pages/admin/collections.vue'),
        meta: { title: '运营歌单' },
      },
      {
        path: 'invitations',
        name: 'invitations',
        component: () => import('@/pages/admin/invitation.vue'),
        meta: { title: '邀请管理' },
      },
      {
        path: 'users',
        name: 'users',
        component: () => import('@/pages/admin/users.vue'),
        meta: { title: '用户管理' },
      },
      {
        path: 'logs',
        name: 'logs',
        component: () => import('@/pages/admin/logs.vue'),
        meta: { title: '日志管理' },
      },
      {
        path: 'source-download',
        name: 'source-download',
        component: () => import('@/pages/admin/source-download.vue'),
        meta: { title: '音源抓歌' },
      },
      {
        path: 'playlist-import',
        name: 'playlist-import',
        component: () => import('@/pages/admin/playlist-import.vue'),
        meta: { title: '歌单解析' },
      },
      {
        path: 'storage',
        name: 'storage',
        component: () => import('@/pages/admin/storage.vue'),
        meta: { title: '存储治理' },
      },
    ],
  },
  { path: '/:pathMatch(.*)*', redirect: '/dashboard' },
]

const router = createRouter({
  history: createWebHistory(),
  routes,
})

/** 会话恢复只执行一次：首个导航前完成令牌恢复与 /me 拉取。 */
let sessionReady: Promise<void> | null = null
function ensureSession() {
  if (!sessionReady) sessionReady = restoreSession()
  return sessionReady
}

router.beforeEach(async (to) => {
  await ensureSession()
  const publicPage = to.meta.public === true

  if (publicPage) {
    if (to.name === 'login' && isAuthenticated.value && isAdmin.value) return { name: 'dashboard' }
    return true
  }

  if (!isAuthenticated.value) {
    return { name: 'login', query: { redirect: to.fullPath } }
  }
  if (!isAdmin.value && to.name !== 'forbidden') {
    return { name: 'forbidden' }
  }
  return true
})

router.afterEach((to) => {
  const title = typeof to.meta.title === 'string' ? to.meta.title : ''
  document.title = title ? `${title} · ShineMusic 管理后台` : 'ShineMusic 管理后台'
})

export default router
