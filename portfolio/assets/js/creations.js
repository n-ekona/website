/* =====================================================================
   実績データ (Creations) — ここを編集するだけで項目が増やせます
   ---------------------------------------------------------------------
   1項目 = 下記オブジェクト1つ。CREATIONS 配列に追加してください。
   フィールド:
     name : 制作物のタイトル                   （必須）
     desc : 1〜2行の説明                        （必須）
     tags : タグ配列 例 ['映像']                （必須・下記CREATION_TAGSから選ぶ）
     link : YouTube/GitHubなどのリンク          （任意）
     meta : 年や役割などの短いメタ 例 '2026・本編映像' （任意）
     linkLabel : リンクの表示文 例 'YouTubeで見る' （任意・省略時 'YouTube'）

   ※ creations.html では「検索」＋タグで絞り込めます。
   ※ タグは CREATION_TAGS の4つのみ。項目が0件でもタグボタンは表示されます。
   ===================================================================== */

/* 実績ページのタグ(この4つ) */
const CREATION_TAGS = ['作曲', '映像', '電子工作', 'アプリ開発'];

const CREATIONS = [
  {
    name: 'mikunity 本編映像',
    desc: '高専生を中心としたファンメイドライブプロジェクト。ライブ本編の映像を担当。',
    tags: ['映像'],
    link: 'https://mikunity.live/',
    meta: 'mikunity — 本編映像担当',
    linkLabel: '公式サイト',
  },
  {
    name: 'ClaudeTokenMonitor',
    desc: 'Claude Codeのトークン使用量・コストをリアルタイムに可視化するデスクトップアプリ。',
    tags: ['アプリ開発'],
    link: 'https://github.com/n-ekona/ClaudeTokenMonitor',
    meta: 'Windows / macOS / Linux',
    linkLabel: 'GitHub',
  },
  {
    name: 'nekonabot',
    desc: 'ボイスチャンネルのアクティブ時間とメッセージ数を帯グラフランキング画像として出力するDiscordボット。',
    tags: ['アプリ開発'],
    link: 'https://github.com/n-ekona/KagemuNA-DiscordBOT',
    meta: 'Raspberry Pi常駐',
    linkLabel: 'GitHub',
  },
];
