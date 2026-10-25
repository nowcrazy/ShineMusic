import './platform/install'
import { createApp } from 'vue'
import App from './App.vue'
import router from './router'
import { createAppI18n } from '@/locale'

createApp(App).use(router).use(createAppI18n()).mount('#app')
