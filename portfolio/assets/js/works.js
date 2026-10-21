/* =====================================================================
   個人開発データ (GitHub) — ここを編集するだけで項目が増やせます
   ---------------------------------------------------------------------
   1項目 = 下記オブジェクト1つ。PROJECTS 配列に追加してください。
   フィールド:
     name : リポジトリ名/製品名             （必須）
     desc : 1〜2行の説明                     （必須）
     tags : タグ配列 例 ['デスクトップアプリ','C#'] （必須・絞り込みに使用）
     link : GitHubなどのリンク               （任意）
     meta : ステータス等の短いメタ 例 'Releases公開中' （任意）

   ※ works.html では「検索」＋タグで絞り込めます。
   ===================================================================== */

const PROJECTS = [
  {
    name: 'ClaudeTokenMonitor',
    desc: '~/.claude/projects のログを解析して、Claude Codeのトークン使用量・コストをリアルタイムに可視化するツール。時系列グラフとGitHub風ヒートマップ付き。',
    tags: ['デスクトップアプリ', 'C#', 'Swift', 'JavaScript'],
    link: 'https://github.com/n-ekona/ClaudeTokenMonitor',
    meta: 'Windows / macOS / Linux・Releases公開中',
  },
  {
    name: 'nekonabot',
    desc: 'ボイスチャンネルのアクティブ時間とメッセージ数を常時記録し、期間指定で帯グラフランキングを画像出力するDiscordボット。ラズベリーパイでの常駐運用を想定。',
    tags: ['Discordボット', 'JavaScript', 'Node.js'],
    link: 'https://github.com/n-ekona/KagemuNA-DiscordBOT',
    meta: 'Raspberry Pi常駐・SQLite使用',
  },
  {
    name: 'FindSenryu4Discord',
    desc: 'Discordのメッセージから川柳を検出し、詠まれた句をもとに新しい川柳を生成するボット。',
    tags: ['Discordボット', 'Go'],
    link: 'https://github.com/n-ekona/FindSenryu4Discord',
  },
  {
    name: 'website',
    desc: 'このサイト自体。静的HTML/CSS/JavaScriptで構築したポートフォリオ。',
    tags: ['Webサイト', 'HTML', 'CSS', 'JavaScript'],
    link: 'https://github.com/n-ekona/website',
  },
];
