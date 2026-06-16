/* =====================================================================
   作品データ — ここを編集するだけで作品が増やせます
   ---------------------------------------------------------------------
   1作品 = 下記オブジェクト1つ。WORKS 配列に追加してください。
   フィールド:
     category : 'video' | 'illust' | 'cg' | 'electronics' | 'music'   （必須・カテゴリ）
     title    : 作品名（日本語）                       （必須）
     titleEn  : ローマ字/英語表記（任意・カードに小さく表示）
     desc     : 1〜2行の説明                          （任意）
     thumbnail: サムネ画像のパス 例 'assets/img/works/xxx.jpg'（任意・未指定なら制作中ビジュアル）
     altImage : 電子工作の「裏面/回路図」画像パス（任意・X線トグル用）
     tools    : 使用ツール・部品の配列 例 ['After Effects','Premiere Pro']（任意）
     specs    : 尺/解像度などの短いメタ 例 '01:32 / 4K'（任意）
     year     : 制作年 例 '2026'                        （任意）
     link     : 外部リンク（YouTube/詳細ページ等）       （任意）
     wip      : true にすると「制作中 / Coming Soon」表示（任意）
   ===================================================================== */

const WORKS = [
  // ▼ 代表作は制作中。実際の作品ができたら wip を消して thumbnail 等を入れてください。
  {
    category: 'video',
    title: '映像作品',
    titleEn: 'coming soon',
    desc: '心に残る映像・モーション作品。準備が整い次第ここで公開します。',
    tools: ['After Effects', 'Premiere Pro'],
    year: '2026',
    wip: true,
  },
  {
    category: 'illust',
    title: 'イラスト作品',
    titleEn: 'coming soon',
    desc: 'イラスト・グラフィックを準備中です。',
    tools: ['Photoshop', 'Procreate'],
    year: '2026',
    wip: true,
  },
  {
    category: 'cg',
    title: '3DCG作品',
    titleEn: 'coming soon',
    desc: '3DCG・モデリング作品を予定しています。',
    tools: ['Blender'],
    year: '2026',
    wip: true,
  },
  {
    category: 'electronics',
    title: '電子工作',
    titleEn: 'coming soon',
    desc: '人を驚かせる電子工作。回路と装置を準備中です。',
    tools: ['Arduino', 'KiCad'],
    year: '2026',
    wip: true,
  },
  {
    category: 'music',
    title: '楽曲・作曲',
    titleEn: 'coming soon',
    desc: 'オリジナル楽曲・劇伴を準備中です。',
    tools: ['DAW'],
    year: '2026',
    wip: true,
  },
];

/* カテゴリ定義（ラベル・色・モチーフ）。基本そのままでOK。 */
const CATEGORIES = {
  video:       { label: '映像',     color: '#5AA9FF' },
  illust:      { label: 'イラスト', color: '#4ECCE6' },
  cg:          { label: '3DCG',     color: '#7E8CF0' },
  electronics: { label: '電子工作', color: '#9A8BFF' },
  music:       { label: '作曲',     color: '#5BC8C0' },
};

/* =====================================================================
   スキル / 使うもの — 実際に使うツールに合わせて自由に編集してください
   ===================================================================== */
const SKILLS = [
  { name: '映像・編集', color: '#5AA9FF', tools: ['Premiere Pro', 'After Effects', 'DaVinci Resolve'] },
  { name: 'イラスト',   color: '#4ECCE6', tools: ['Photoshop', 'Procreate', 'Illustrator'] },
  { name: '3DCG',       color: '#7E8CF0', tools: ['Blender', 'Cinema 4D'] },
  { name: '電子工作',   color: '#9A8BFF', tools: ['Arduino', 'ESP32', 'KiCad', 'はんだづけ'] },
  { name: '作曲',       color: '#5BC8C0', tools: ['DAW', 'Studio One'] },
];
