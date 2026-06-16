# NekoNA Portfolio

NekoNA(猫奈)のポートフォリオサイト。映像・デザイン・電子工作の作品紹介、ブログ(投稿機能つき)、お知らせ機能を備えた静的サイトです。

- デザイン: 和モダン × 桜(温かいダーク × 生成り色 × 桜ピンク)、既存 Discord サイト(NekoNA Circle)と統一感のある世界観。舞い散る花びらと上品なドットカーソル付き
- ホスティング: GitHub Pages 等の静的ホスティングでそのまま動作(ビルド不要)
- ブログ/お知らせ: Supabase(無料)でログイン投稿。閲覧は誰でも / 投稿・編集・削除はログイン中の本人のみ

---

## ファイル構成

```
portfolio/
├── index.html            … トップ(ヒーロー/About/Works/Process/Skills/Contact)
├── blog.html             … ブログ(一覧・記事・投稿エディタ)
├── README.md             … このファイル
└── assets/
    ├── css/
    │   ├── style.css     … 全体のデザイン
    │   └── blog.css      … ブログ/エディタのデザイン
    ├── js/
    │   ├── config.js     ← ★ Supabase の鍵をここに貼る
    │   ├── works.js      ← ★ 作品・スキルのデータ(編集するだけで増やせる)
    │   ├── main.js       … トップの動作
    │   ├── markdown.js   … Markdown 整形
    │   └── blog.js       … ブログ/お知らせの動作
    └── img/              … 画像を置く場所(サムネ等)
```

★印の2ファイルだけ編集すれば、ほぼカスタマイズできます。

---

## 1. 作品を追加する(`assets/js/works.js`)

`WORKS` 配列にオブジェクトを足すだけです。

```js
{
  category: 'video',                  // 'video' | 'design' | 'electronics'
  title: '作品タイトル',
  titleEn: 'Title in English',        // 任意
  desc: '作品の説明(1〜2行)',          // 任意
  thumbnail: 'assets/img/works/a.jpg',// サムネ画像。未指定だと「制作中」表示
  tools: ['After Effects', 'Premiere'],
  specs: '01:32 / 4K',                // 任意
  year: '2026',
  link: 'https://youtu.be/xxxx',      // 任意(クリックで開く)
  // wip: true,                        // 「制作中」にしたいとき
}
```

- 画像は `assets/img/works/` に置いて、`thumbnail` にパスを書きます。
- `wip: true` を消すと「制作中」表示が外れます。
- スキル欄は同ファイル下部の `SKILLS` を編集してください。

---

## 2. ブログ(Supabase)セットアップ

ブログとお知らせは Supabase を使います。最初の1回だけ設定が必要です(無料)。

### ステップ1: プロジェクト作成
1. https://supabase.com/ で無料アカウントを作成し、新規プロジェクトを作る。
2. リージョンは `Northeast Asia (Tokyo)` などお好みで。データベースのパスワードは控えておく。

### ステップ2: テーブルと権限(RLS)を作成
左メニューの **SQL Editor** を開き、以下を貼り付けて実行(RUN):

```sql
-- 記事テーブル（author = 投稿者。default auth.uid() で自動的に本人になる）
create table if not exists public.posts (
  id uuid primary key default gen_random_uuid(),
  created_at timestamptz not null default now(),
  updated_at timestamptz,
  author uuid not null default auth.uid(),
  title text not null,
  body text,
  published boolean not null default true
);

-- お知らせテーブル
create table if not exists public.news (
  id uuid primary key default gen_random_uuid(),
  created_at timestamptz not null default now(),
  updated_at timestamptz,
  author uuid not null default auth.uid(),
  title text not null,
  body text,
  published boolean not null default true
);

-- 行レベルセキュリティを有効化
alter table public.posts enable row level security;
alter table public.news  enable row level security;

-- 閲覧: 公開記事は全員が読める / 未公開（下書き）は本人のみ
create policy "posts_read" on public.posts for select
  using (published or author = auth.uid());
create policy "news_read" on public.news for select
  using (published or author = auth.uid());

-- 書き込み（投稿・編集・削除）: 本人（author = 自分）のみ
create policy "posts_insert" on public.posts for insert to authenticated with check (author = auth.uid());
create policy "posts_update" on public.posts for update to authenticated using (author = auth.uid()) with check (author = auth.uid());
create policy "posts_delete" on public.posts for delete to authenticated using (author = auth.uid());
create policy "news_insert"  on public.news  for insert to authenticated with check (author = auth.uid());
create policy "news_update"  on public.news  for update to authenticated using (author = auth.uid()) with check (author = auth.uid());
create policy "news_delete"  on public.news  for delete to authenticated using (author = auth.uid());
```

> このポリシーは「本人(author)のみ書き込み・下書き閲覧」を **RLS で実際に強制**します。万一あとで新規登録を開けたり、別ユーザーが増えても、他人の記事は編集・削除できません(多層防御)。`author` は投稿時に `auth.uid()` が自動で入るので、フォーム側で意識する必要はありません。

### ステップ3: 新規登録を無効化 + 自分のアカウントを作成
> ⚠ ここが重要です。新規登録を開けたままにすると、第三者がログインして投稿できてしまいます。

1. **Authentication → Sign In / Providers**(または Settings)で
   **「Allow new users to sign up」を OFF** にする。
2. **Authentication → Users → Add user** で自分のアカウントを1つ作成。
   - メールアドレスとパスワードを入力
   - **「Auto Confirm User」を ON**(これをしないとログインできません)
   - ここで決めた **メール + パスワードが、ブログのログインID/パスワード**になります。

### ステップ4: 鍵を貼る(`assets/js/config.js`)
**Project Settings → API** から2つの値をコピーし、`config.js` に貼ります:

```js
window.NEKONA_CONFIG = {
  SUPABASE_URL: 'https://xxxxxxxx.supabase.co',   // Project URL
  SUPABASE_ANON_KEY: 'eyJhbGciOi...',             // anon public キー
};
```

- `anon public` キーは**公開して安全**な鍵です(RLS で書き込みを守ります)。GitHub に上げてOK。
- **`service_role` キーは絶対に貼らないでください**(全権限の秘密鍵です)。

設定が終わると、`blog.html` のセットアップ案内が消え、記事の閲覧・投稿ができるようになります。

---

## 3. 記事・お知らせの書き方

1. `blog.html` を開き、右上の **「管理者ログイン」** → ステップ3で作ったメール/パスワードでログイン。
2. **「＋ 新規投稿」** を押すとエディタが開きます。
3. **投稿先** を選択:
   - `ブログ` … `blog.html` に表示
   - `お知らせ (Discord)` … `DIscordweb` の NEWS 欄に表示
4. タイトルと本文(Markdown)を書く。右側にライブプレビューが出ます。
5. **公開する** で保存。「公開する」のチェックを外すと下書き(自分だけ閲覧)。

### 使える Markdown
| 記法 | 結果 |
|------|------|
| `# 見出し` | 大見出し |
| `## 小見出し` | 小見出し |
| `### さらに小さく` | 小々見出し |
| `**太字**` | **太字** |
| `*斜体*` | 斜体 |
| `` `コード` `` | インラインコード |
| ` ```…``` ` | コードブロック |
| `> 引用` | 引用 |
| `- 項目` / `1. 項目` | リスト |
| `[文字](URL)` | リンク |
| `![alt](画像URL)` | 画像 |
| `---` | 区切り線 |

---

## 4. 色・テキストの変更

- 配色: `assets/css/style.css` 冒頭の `:root{ … }` の変数(`--accent` `--sky` `--petal`(桜カーソル) `--ink` など)を変えると全体に反映されます。
- 肩書き・自己紹介・コピー: `index.html` のヒーロー部分を直接編集。
- SNS リンク: `index.html` / `blog.html` のヘッダー・Contact・フッターにあります。
  - 仕事用: `@n_ekona`(X/YouTube/Discord)
  - 趣味用: `@nekolnu`(X)

---

## 5. 公開(GitHub Pages)

このリポジトリは GitHub Pages 想定です。`portfolio/` を公開ディレクトリに含めれば、
`https://(ドメイン)/portfolio/` でアクセスできます(リポジトリ設定の Pages に合わせてください)。

トップを `portfolio/index.html` にしたい場合は、リポジトリ直下に置く・サブパス公開にする等、運用に合わせて調整してください。

---

## メモ
- アニメーションは `prefers-reduced-motion`(OSの「視差効果を減らす」)に対応し、自動で控えめになります。
- カスタムカーソルはマウス環境のみ。タッチ端末では通常カーソルに戻ります。
- **OGP画像**: SNS/Discord共有時のサムネは `assets/img/og.png`(1200×630)を作成して置いてください。未作成だと画像なしカードになります。各ページの `og:image` は絶対URL(`https://nekona.jp/...`)を指しています。公開ドメインが異なる場合は、その値と各ページの `canonical` / `og:url` も合わせて変更してください。
- **(任意・上級者向け)** Supabase クライアントの CDN 読込はメジャー固定(`@supabase/supabase-js@2`)です。より堅牢にしたい場合は特定バージョン＋ `integrity`(SRI)＋`crossorigin` に固定できます。
