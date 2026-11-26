<script setup lang="ts">
import { isAuthenticated, sessionGeneration } from '@/services/session'
</script>

<template>
  <router-view v-slot="{ Component, route }">
    <component :is="Component" v-if="route.meta.public === true || isAuthenticated" :key="`${sessionGeneration}:${route.fullPath}`" />
  </router-view>
</template>
