<script setup lang="ts">
/** 非管理员账号访问后台时的提示页。 */
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { logout, currentUser } from '@/services/session'

const router = useRouter()

async function switchAccount() {
  await logout()
  ElMessage.success('已退出登录')
  void router.replace({ name: 'login' })
}
</script>

<template>
  <div class="forbidden">
    <div class="card">
      <div class="code">403</div>
      <h2>无访问权限</h2>
      <p>
        当前账号
        <strong>{{ currentUser?.email || '未登录' }}</strong>
        不是管理员，无法进入 ShineMusic 管理后台。
      </p>
      <el-button type="primary" @click="switchAccount">切换管理员账号</el-button>
    </div>
  </div>
</template>

<style scoped>
.forbidden {
  display: grid;
  place-items: center;
  min-height: 100vh;
  background: var(--adm-page-bg);
}

.card {
  width: min(90vw, 420px);
  padding: 40px 36px;
  border: 1px solid var(--adm-border);
  border-radius: 14px;
  background: #ffffff;
  text-align: center;
}

.code {
  color: var(--adm-primary);
  font-size: 56px;
  font-weight: 800;
  letter-spacing: 2px;
}

h2 {
  margin: 8px 0 12px;
  font-size: 20px;
}

p {
  margin: 0 0 24px;
  color: var(--adm-text-2);
  font-size: 14px;
  line-height: 1.7;
}
</style>
