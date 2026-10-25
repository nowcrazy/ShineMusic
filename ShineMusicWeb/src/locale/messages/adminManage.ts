// locale/messages/adminManage.ts
// 管理员维护页文案包（由 i18n 补全任务生成）：曲目维护 / 歌单管理 / 邀请管理 / 需求管理
// 覆盖 pages/admin/catalog.vue、pages/admin/collections.vue、pages/admin/invitation.vue、
// pages/admin/requests.vue、services/admin-artwork.ts。
// 在 locale/index.ts 中与 messages.ts 深合并；zh-CN 值与源文件原文逐字一致，便于后续接线替换。
// 说明：inventory 159 条中 143 条已复用 messages.ts 现有 key（catalogAdmin.* / collectionsAdmin.* /
// requestsAdmin.* / invitationAdmin.* / admin.common.* / admin.catalog.*），不在本文件重复。
// 本文件仅承载新增 15 个 key：
// - adminCommon：管理页通用加载提示 + 封面上传服务（admin-artwork.ts）错误文案；
// - 各管理页补 pageTitle / 筛选终态 / 邀请注册码弹窗等 messages.ts 缺失项。
// 注意：invitationAdmin.statusIssuedGenerated =「已生成」与现有 statusIssued =「待兑换」
// 指向同一状态（issued）但措辞不同，接线时需人工择一。
export const adminManageMessages = {
  'zh-CN': {
    adminCommon: {
      loading: '正在加载…',
      artworkUploadFailedWith: '上传失败（{status}）',
      artworkUploadResponseInvalid: '上传成功，但响应解析失败',
      artworkUploadFailed: '上传失败',
    },
    catalogAdmin: {
      editTrackInfo: '编辑曲目信息',
    },
    collectionsAdmin: {
      pageTitle: '歌单管理',
    },
    invitationAdmin: {
      pageTitle: '邀请管理',
      codeModalContent: '{code}\n\n注册码仅在本次展示，请立即复制保存。',
      codeCreateFailed: '注册码生成失败',
      statusIssuedGenerated: '已生成',
      codeNoEmail: '注册码（未绑定邮箱）',
    },
    requestsAdmin: {
      pageTitle: '需求管理',
      filterDeclined: '已拒绝',
      filterWithdrawn: '已撤回',
      empty: '暂无需求',
    },
  },
  en: {
    adminCommon: {
      loading: 'Loading…',
      artworkUploadFailedWith: 'Upload failed ({status})',
      artworkUploadResponseInvalid: 'Upload succeeded, but the response could not be parsed',
      artworkUploadFailed: 'Upload failed',
    },
    catalogAdmin: {
      editTrackInfo: 'Edit track info',
    },
    collectionsAdmin: {
      pageTitle: 'Playlist Admin',
    },
    invitationAdmin: {
      pageTitle: 'Invitation Admin',
      codeModalContent: '{code}\n\nThis registration code is shown only once. Copy and save it now.',
      codeCreateFailed: 'Failed to generate registration code',
      statusIssuedGenerated: 'Issued',
      codeNoEmail: 'Registration code (no email bound)',
    },
    requestsAdmin: {
      pageTitle: 'Request Admin',
      filterDeclined: 'Declined',
      filterWithdrawn: 'Withdrawn',
      empty: 'No requests yet',
    },
  },
  ja: {
    adminCommon: {
      loading: '読み込み中…',
      artworkUploadFailedWith: 'アップロードに失敗しました（{status}）',
      artworkUploadResponseInvalid: 'アップロードは成功しましたが、レスポンスの解析に失敗しました',
      artworkUploadFailed: 'アップロードに失敗しました',
    },
    catalogAdmin: {
      editTrackInfo: '曲情報を編集',
    },
    collectionsAdmin: {
      pageTitle: 'プレイリスト管理',
    },
    invitationAdmin: {
      pageTitle: '招待管理',
      codeModalContent: '{code}\n\n登録コードは今回のみ表示されます。今すぐコピーして保存してください。',
      codeCreateFailed: '登録コードの生成に失敗しました',
      statusIssuedGenerated: '発行済み',
      codeNoEmail: '登録コード（メール未紐付け）',
    },
    requestsAdmin: {
      pageTitle: 'リクエスト管理',
      filterDeclined: '却下済み',
      filterWithdrawn: '取り下げ済み',
      empty: 'リクエストはありません',
    },
  },
}
