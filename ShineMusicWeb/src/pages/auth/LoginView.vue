<script setup lang="ts">
/**
 * 管理后台独立登录页：邮箱 + 密码，登录后校验 admin 角色。
 * 与 App 端 H5 无任何关系，仅面向运营/管理员。
 */
import { reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, type FormInstance, type FormRules } from 'element-plus'
import { Lock, User } from '@element-plus/icons-vue'
import { isAdmin, login, logout } from '@/services/session'
import { describeError } from '@/services/errors'

const route = useRoute()
const router = useRouter()

const formRef = ref<FormInstance>()
const form = reactive({ email: '', password: '' })
const submitting = ref(false)

const rules: FormRules = {
  email: [
    { required: true, message: '请输入邮箱或用户名', trigger: 'blur' },
  ],
  password: [{ required: true, message: '请输入密码', trigger: 'blur' }],
}

async function handleSubmit() {
  if (!formRef.value || submitting.value) return
  const valid = await formRef.value.validate().catch(() => false)
  if (!valid) return

  submitting.value = true
  try {
    await login(form.email, form.password)
    if (!isAdmin.value) {
      await logout()
      ElMessage.error('该账号没有管理员权限')
      return
    }
    ElMessage.success('登录成功')
    const redirect = typeof route.query.redirect === 'string' ? route.query.redirect : '/dashboard'
    await router.replace(redirect.startsWith('/') ? redirect : '/dashboard')
  } catch (error) {
    ElMessage.error(describeError(error, '登录失败，请检查邮箱与密码'))
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <div class="login-page">
    <section class="brand-panel">
      <div class="brand-logo">
        <span class="mark">♪</span>
        <span class="name">ShineMusic</span>
      </div>
      <div class="brand-body">
        <h1>音乐库运营管理后台</h1>
        <p>曲库维护、导入审核、点歌处理、邀请与存储治理，一站式完成日常运营。</p>
        <ul class="feature-list">
          <li><span class="dot" />曲目元数据与歌词在线编辑</li>
          <li><span class="dot" />音频导入、试听、发布与拒绝</li>
          <li><span class="dot" />点歌需求流转与外部音源抓取</li>
          <li><span class="dot" />存储审计与孤儿文件清理</li>
        </ul>
      </div>
      <div class="brand-foot">ShineMusic Admin Console</div>
    </section>

    <section class="form-panel">
      <div class="form-card">
        <header class="form-head">
          <h2>管理员登录</h2>
          <p>请使用管理员账号登录后台</p>
        </header>

        <el-form
          ref="formRef"
          :model="form"
          :rules="rules"
          label-position="top"
          size="large"
          @submit.prevent="handleSubmit"
        >
          <el-form-item label="邮箱 / 用户名" prop="email">
            <el-input
              v-model="form.email"
              placeholder="邮箱或用户名"
              :prefix-icon="User"
              autocomplete="username"
              clearable
            />
          </el-form-item>
          <el-form-item label="密码" prop="password">
            <el-input
              v-model="form.password"
              type="password"
              placeholder="请输入密码"
              :prefix-icon="Lock"
              autocomplete="current-password"
              show-password
              @keyup.enter="handleSubmit"
            />
          </el-form-item>

          <el-button
            class="submit"
            type="primary"
            size="large"
            native-type="submit"
            :loading="submitting"
            @click="handleSubmit"
          >
            登录后台
          </el-button>
        </el-form>

        <p class="tip">仅管理员账号可访问，如需开通请联系系统管理员。</p>
      </div>
    </section>
  </div>
</template>

<style scoped>
.login-page {
  display: grid;
  grid-template-columns: 1.1fr 1fr;
  min-height: 100vh;
  background: #ffffff;
}

.brand-panel {
  position: relative;
  display: flex;
  flex-direction: column;
  justify-content: space-between;
  padding: 48px 56px;
  overflow: hidden;
  background:
    radial-gradient(circle at 20% 15%, rgba(91, 224, 122, 0.24), transparent 42%),
    radial-gradient(circle at 85% 80%, rgba(52, 165, 111, 0.22), transparent 48%),
    #0f1722;
  color: #e8f5ec;
}

.brand-logo {
  display: flex;
  align-items: center;
  gap: 10px;
}

.brand-logo .mark {
  display: grid;
  place-items: center;
  width: 38px;
  height: 38px;
  border-radius: 10px;
  background: linear-gradient(135deg, #5be07a, #34a56f);
  color: #07240f;
  font-size: 21px;
  font-weight: 700;
}

.brand-logo .name {
  font-size: 17px;
  font-weight: 700;
  letter-spacing: 0.4px;
}

.brand-body h1 {
  margin: 0 0 14px;
  font-size: 34px;
  font-weight: 700;
  line-height: 1.25;
}

.brand-body p {
  max-width: 420px;
  margin: 0;
  color: #9fb3a7;
  font-size: 15px;
  line-height: 1.7;
}

.feature-list {
  margin: 32px 0 0;
  padding: 0;
  list-style: none;
}

.feature-list li {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-bottom: 14px;
  color: #c9dbd0;
  font-size: 14px;
}

.feature-list .dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: #5be07a;
  box-shadow: 0 0 0 4px rgba(91, 224, 122, 0.14);
}

.brand-foot {
  color: #5f7268;
  font-size: 12px;
  letter-spacing: 0.5px;
}

.form-panel {
  display: grid;
  place-items: center;
  padding: 40px;
}

.form-card {
  width: 100%;
  max-width: 380px;
}

.form-head h2 {
  margin: 0 0 6px;
  color: #111827;
  font-size: 24px;
  font-weight: 700;
}

.form-head p {
  margin: 0 0 28px;
  color: #6b7280;
  font-size: 13px;
}

.submit {
  width: 100%;
  margin-top: 6px;
}

.tip {
  margin: 22px 0 0;
  color: #9ca3af;
  font-size: 12px;
  text-align: center;
}

@media (max-width: 900px) {
  .login-page {
    grid-template-columns: 1fr;
  }

  .brand-panel {
    display: none;
  }
}
</style>
