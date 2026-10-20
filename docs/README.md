# Shine Music 文档

本目录是对当前仓库实现的理解成果，按主题拆开，方便后续改代码时对照。更细的接口字段、枚举和安全约定仍以代码与后端契约为准：

- 后端实现说明：[`backend/README.md`](../backend/README.md)
- 接口清单：[`backend/api-list.md`](../backend/api-list.md)
- 全局 DTO / 错误码 / 开关：[`backend/domain-contract.md`](../backend/domain-contract.md)
- 机器可读 OpenAPI：开发环境 `/openapi/v1.json` 与 `/swagger`
- 前端生成类型：`frontend/src/types/api.generated.ts`（由 `backend/tools/generate-ts-contracts.py` 生成）

## 阅读顺序

1. [overview.md](./overview.md) — 产品定位、仓库结构、当前能力与边界
2. [architecture.md](./architecture.md) — 运行时结构、存储、认证、契约同步
3. [domain.md](./domain.md) — 领域模型、状态机、关键不变量
4. [workflows.md](./workflows.md) — 听歌、邀请注册、导入发布、点歌满足、推荐
5. [frontend.md](./frontend.md) — uni-app 页面、服务层、播放器
6. [local-setup.md](./local-setup.md) — 本机运行、换 IP、密钥与媒体依赖
