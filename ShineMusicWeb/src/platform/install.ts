/** 平台层安装入口：必须在任何业务模块之前被 import，保证模块级代码能拿到 uni 全局。 */
import { installUniShim } from './uni'

installUniShim()
