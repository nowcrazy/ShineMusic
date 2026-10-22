/**
 * 构建期常量声明：由 vite.config.ts 的 define 注入。
 * 值来自仓库根目录 config/hosts.json（唯一地址源），避免在代码里硬编码 IP。
 */
declare const __SM_API_BASE__: string
