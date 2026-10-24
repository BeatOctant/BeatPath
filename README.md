# OctantPath

> **To use BeatOctant** (install, known issues): [Getting Started](https://github.com/BeatOctant/OctantSpecs/blob/main/GettingStarted_en.md) · [はじめかた（日本語）](https://github.com/BeatOctant/OctantSpecs/blob/main/GettingStarted_ja.md)
>
> BeatOctant is written mostly with AI coding agents (Anthropic's Claude). The maintainer decides what to build and reviews every change. It does not generate maps; it recommends existing maps from BeatSaver. ([作り方（AI の使い方）](https://github.com/BeatOctant/OctantSpecs/blob/main/README.md#作り方ai-の使い方))
>
> OctantPath is a Beat Saber mod for PC (Steam) that recommends BeatSaver maps from your own play history. For Beat Saber on Quest, see OctantPath for Quest ([OctantPathAndroid](https://github.com/BeatOctant/OctantPathAndroid)).
>
> BeatOctant is an unofficial fan project for Beat Saber. It is not affiliated with, endorsed by, or sponsored by Beat Games or Meta. Beat Saber and Meta Quest are trademarks of their respective owners. Beat Games・Meta とは関係のない非公式の MOD・ツールです。
>
> 報告と案は Discord の BeatOctant サーバー（バグは `#bug-reports`、案は `#feedback-ideas`）で受けます。外からの pull request は受けていません。

Beat Saber（PC）1.40.8 の MOD と、ゲームを起動せずに推薦を見る OctantPath Desktop。ゲームの外で動く [OctantCompass](https://github.com/BeatOctant/OctantCompass) に推薦を頼み、推薦されたマップを BeatSaver から取って入れ、自分が入れたマップだけを片付ける。Quest 版 Beat Saber 向けは [OctantPathAndroid](https://github.com/BeatOctant/OctantPathAndroid)（OctantPath for Quest）にある。BeatOctant 全体の中での位置は [OctantSpecs の概要](https://github.com/BeatOctant/OctantSpecs#概要)にある。仕様はすべて [OctantSpecs](https://github.com/BeatOctant/OctantSpecs) にあり、ここには書き写さない。次の順に読む。

1. [憲法](https://github.com/BeatOctant/OctantSpecs/blob/main/constitution.md)の原則 5・7: OctantPath の責務と、OctantPath が持つ状態
2. [契約3（OctantPath to OctantCompass）](https://github.com/BeatOctant/OctantSpecs/blob/main/contracts/mod-api.md)、[契約6（client to OctantCompass）](https://github.com/BeatOctant/OctantSpecs/blob/main/contracts/launch.md)、[外部依存](https://github.com/BeatOctant/OctantSpecs/blob/main/contracts/external.md)
3. [OctantPath 法](https://github.com/BeatOctant/OctantSpecs/blob/main/octantpath/law.md): 実装が守る意味と、旧実装からの教訓
4. [実装構想](https://github.com/BeatOctant/OctantSpecs/blob/main/plans/octantpath-implementation.md): 既存のマップとの区別、ミックスの今の形、リスク
5. 未決は [open.md の OctantPath](https://github.com/BeatOctant/OctantSpecs/blob/main/open.md#octantpath)、決まったことは [decisions.md の OctantPath](https://github.com/BeatOctant/OctantSpecs/blob/main/decisions.md#octantpath)

## 組み合わせる版

支えるのは、各部品の最新の版どうしの組み合わせだけである（[decisions.md の全体と段階](https://github.com/BeatOctant/OctantSpecs/blob/main/decisions.md#全体と段階)、2026-10-22）。この README を書いた時点の相手は次のとおり。

| 相手 | 版 | つなぐもの |
| --- | --- | --- |
| Beat Saber（PC） | 1.40.8 | ゲームの DLL |
| OctantCompass | 最新 | [契約3（OctantPath ↔ OctantCompass）](https://github.com/BeatOctant/OctantSpecs/blob/main/contracts/mod-api.md) 2.0、[契約6（つなぐ側 ↔ OctantCompass）](https://github.com/BeatOctant/OctantSpecs/blob/main/contracts/launch.md) 2.0 |
| OctantSpecs | `OCTANTSPECS_REF`（`main`） | テストが読む契約3 の例 |

このリポジトリの今の版は v0.1.0。

## ビルド

ゲームのフォルダをリポジトリ直下の `BeatSaber` というシンボリックリンクで指すか、`-p:BeatSaberDir=<ゲームのフォルダ>` で渡す。

```bash
dotnet build src/OctantPath -c Release
```

`-p:CopyToGame=true` を付けると、できた DLL をゲームの `Plugins` に置く。

## テスト

Unity に依存しない部品（`src/OctantPath.Core`）を .NET 8 で試す。契約3（OctantPath to OctantCompass）の例は OctantSpecs から `OCTANTSPECS_REF` の版で取ってくる（書き写さない）。

```bash
scripts/fetch-octantspecs.sh
dotnet test tests/OctantPath.Core.Tests
```

動いている OctantCompass とつなぐテストは、`OCTANTPATH_TEST_PAIR_CODE` に OctantCompass の合言葉と、`OCTANTPATH_TEST_ADDRESS` につなぐ先を入れると動く。ペアリングのたびに環境が 1 つ増えるので、普段の OctantCompass ではなく、テスト用のデータフォルダと別のポートで起動したものにつなぐ（DB は普段のものを `--db` で指せる）。つなぐ先を省くと、このテストは失敗する。

```bash
octantcompass serve --data-dir /tmp/octantcompass-test --port 23288 --db <普段の DB のフォルダ> &
OCTANTPATH_TEST_PAIR_CODE=$(octantcompass pair code --data-dir /tmp/octantcompass-test) \
OCTANTPATH_TEST_ADDRESS=127.0.0.1:23288 dotnet test tests/OctantPath.Core.Tests
```

終わったら止めて、`/tmp/octantcompass-test` を消す。

## CI

pull request ごとに GitHub Actions が OctantPath.Core のテストと OctantPath Desktop のビルドを動かします（`.github/workflows/test.yml`）。MOD 本体はゲームの DLL が要るので CI ではビルドしません。文書と `probes/` だけの変更では動かず、同じ pull request に続けて push すると前の実行を止めます。

## デスクトップ（HMD なし）

ゲームを起動せずに、板の手札（「ミックス」の種類と曲を起点にしないカード）、下見（⇔ で入れ替え、↓ で取得、「プレイリストを作る」で並んでいる曲を取得してプレイリストにする）、所有マップを使う。画面は小窓を使わず、FHD の 1 画面に流れの順で並べる: 左に所有マップ（曲を押すとそのまま起点になる）、中に起点の曲の詳細・難易度と、その下に種類（入力は押したカードのすぐ下に開く）、右半分の上に下見、下にプレビュー（「譜面を見る」で ArcViewer を読み込んでそのまま再生する）。ArcViewer（GPLv3）は初回の起動でビルド（約 80MB）をデータフォルダの `arcviewer` に取り、`/arcviewer/` から同じオリジンとして出す。取るのは決めた版（ArcViewer のリポジトリの deploy ブランチの 1 つのコミット、0.8.1。deploy が公開サイトの元のビルドだというのは推測）だけで、ファイルごとに大きさと SHA-256 を確かめ、合わなければ何も残さず、譜面のプレビューは出さない（`ArcViewerMirror.cs`。公開サイトには切り替えない。公開サイトの ArcViewer はブラウザから BeatSaver を直に引き、OctantPath の決まりの外になるため。2026-10-16）。下見で一度「譜面を見る」を押すと、新しく並べるまでは下見で選んだ曲の譜面をすぐ再生する（試聴は流さない）。ほかの曲を流すと ArcViewer を一時停止し、ArcViewer で再生を始めるとこちらの試聴を止める。取り終えるまでと取れなかったときは、譜面のところにそのことを出す。取り直すときは `arcviewer` を消して起動する。OctantCompass とは MOD と同じ OctantPath.Core の契約3・契約6（client to OctantCompass）の実装で話す。

```bash
dotnet run --project src/OctantPath.Desktop
```

手元のサーバー（http://127.0.0.1:5791）のページを、Desktop 自身の窓に出す（Photino。Windows は WebView2、Linux は WebKitGTK 4.1。2026-10-18）。ブラウザの窓ではないので、タスクバーには OctantPath として出る。窓を閉じると Desktop も終わる（端末から起動したときの Ctrl+C でも、窓を閉じて終わる）。Linux では OS の WebKitGTK 4.1 と libnotify が要る（Debian・Ubuntu は `libwebkit2gtk-4.1-0` と `libnotify4`）。Windows は Windows 10 と 11 に入っている WebView2 を使う。`--no-window` では窓を出さずにサーバーだけ動かし、ページはブラウザで開く（そのときはフォルダを選ぶ窓と、プレイリストの保存先の選択は使えない）。曲選択の代わりに、中の欄で起点の曲（BeatSaver のキー・URL・hash）と難易度を選ぶ。下見で曲を選ぶと試聴が流れ、推薦の理由、取得したマップの中身（Info.dat）、ArcViewer での譜面のプレビュー（BeatSaver の版か、取得したマップ）が出る。「ログ」で最後の推薦の要求と応答を見られる。左上の名前の横に版とコミットを出す。上の帯で画面の言語とテーマ（ダーク、ライト、OS に従う自動。Desktop のデータフォルダの `desktop.json`）を選べる。色の見え方が違っても使えるよう、状態の色は赤と緑で分けず（青緑・黄・朱で、明るさも変える）、接続の印の形（塗った丸・輪・ひし形）と文でも分ける（2026-10-19。OctantCompass の窓と同じ色）。窓の中は 1280×720（ページの CSS の px。`WindowMinSize.cs`）より小さくならず、欄はいつも横に並ぶ（左の 2 列は幅に合わせて 280〜400px。2026-10-19）。ページは全体では動かず、欄の中だけが動く（1 つの欄に動く枠は 1 つ）。画面がそれより小さいとき（1920×1080 を拡大率 150% で使うなど）は窓を画面の作業領域に収め、ページごと少し動く。窓は前に閉じたときの位置と大きさで開き、最大化していたら最大化して開く（`desktop.json` の `window`。2026-10-21。`WindowPlacement.cs`）。初めてのときと、その位置がどの画面にも入らないとき（画面を外したときなど）は、最大化して開く。Linux の Wayland ではプログラムが窓の位置を決められないので、大きさと最大化だけを戻し、位置はデスクトップが決める。手元のサーバーは、Host が `127.0.0.1` か `localhost` と待ち受けのポートの要求にだけ答え、POST はこのページから（Origin）の JSON の本文だけを受け付ける（ほかのサイトから使われないため。`LocalGuard.cs`）。カバーと試聴は、OctantPath の User-Agent を付けるため、このサーバーが BeatSaver の CDN から取って渡す（`/api/cdn`）。

- Desktop のデータ（設定とトークン・取得したマップ・プレイリスト・所有マップの覚え書き）は、`--data` を省くと次の順に決めた場所に置く（2026-10-13）: このリポジトリの中で動かしているならリポジトリの `.desktop`（git に入れない）、実行ファイルの隣に `OctantPath.Desktop-data` フォルダがあればそこ（持ち歩き用）、どちらでもなければ、Linux では XDG の決まりに分けて置く（2026-10-14）: 設定（`settings.json`・`desktop.json`）は `~/.config/OctantPath/Desktop`、トークン（`connection.json`）は `~/.local/state/OctantPath/Desktop`、取得したマップ・プレイリスト・作業用の場所は `~/.local/share/OctantPath/Desktop`、所有マップの覚え書き（`library-cache.json`）と ArcViewer は `~/.cache/OctantPath/Desktop`（それぞれ `$XDG_CONFIG_HOME`・`$XDG_STATE_HOME`・`$XDG_DATA_HOME`・`$XDG_CACHE_HOME` があればその下）。Windows・macOS は OS の決まった場所の `OctantPath/Desktop` 1 か所（Windows は `%LOCALAPPDATA%`、macOS は `~/Library/Application Support`）。そこに作れなければ起動したフォルダの `.desktop`。`--data` と持ち歩き用・開発用のフォルダは、今までどおり全部を 1 か所に置く。使っている場所は「設定」のタブに出る。`--data` でほかのフォルダにできる。設定とトークンは OctantCompass とは別の環境としてペアリングしたもの。`--userdata` にゲームの `UserData/OctantPath` を渡すと、ゲームの設定とトークンを読んで同じ環境として話す。そのフォルダには書かない
- 取得したマップはデータの場所の `Levels`、プレイリストは `Playlists` に置く。ゲームのフォルダには書かない。下見の「プレイリストを作る」で、足りないマップを取得してプレイリストを書く。「保存…」で、作ったプレイリストのファイルを、OS のフォルダ選択で選んだフォルダに同じ名前で写す（同じ名前があれば上書きする。名前は作った時刻を含むので、前に保存した同じプレイリスト）
- 「所有しているマップ」で、ゲームに入っている曲（読むだけ）と Desktop で取得した曲をまとめて一覧し（置き場所は曲の分類に見えるので出さない。2026-10-13）、検索・曲の再生・ArcViewer でのプレビュー・起点にすることができる
- OctantCompass とは通信だけでつなぎ、OctantCompass を起動しない（契約6（client to OctantCompass）2.0）。「設定」のタブのペアリングで、つなぐ先（空なら同じ PC の `127.0.0.1:23287`）と OctantCompass の窓に出る合言葉を入れる。同じ PC でも合言葉が要る
- 「設定」のタブで、Desktop が使う設定のうち人が変える値（ゲームのフォルダ・下見の曲数・マップの取得元・ペアリングの名前）を変え、変えた項目だけを Desktop 自身の `settings.json` に保存する（既定値に戻すと項目を消す）。待ち時間などの内部の値は画面に出さない（下の「設定」）。ゲームの MOD の設定は変わらない。`--userdata` のときは読むだけ
- 前に開いた Desktop がまだ動いていれば、新しくは起動せずにその窓を前に出す。新しい版で開き直すときは前の Desktop の窓を閉じてから開く
- signal は送らない（遊んだ記録がないため）
- 画面の言語は上の帯で選ぶ（自動・English・日本語）。選んだ値は設定の `language` に書く（`--userdata` で読むだけのときはその起動の間だけ）。自動は OS の言語（環境変数 LC_ALL・LC_MESSAGES・LANG）、なければ英語。Desktop の文は `src/OctantPath.Desktop/Languages/<言語>.json` にあり、MOD と共通の文（接続の状態など）は OctantPath.Core の言語ファイルを同じ言語で使う。手札の名前は OctantCompass が送る `{言語: 文字列}` から今の言語の文を選び、言語を変えると手札を取り直す
- ゲームに入っている曲は下見で「導入済み」と出し、取得しない。ゲームの `Beat Saber_Data/CustomLevels`・`CustomWIPLevels` と `UserData/OctantPath/Levels` を読んで hash を出す（読むだけ）。ゲームのフォルダは `--game` で渡すか `--userdata` から求める。どちらもなければ「設定」のタブで選んだもの（データフォルダの `desktop.json`。保存するとすぐ読み直す。2026-10-16）。「フォルダを選ぶ…」で OS のフォルダ選択の窓を開ける（Desktop の窓から開く。`--no-window` では使えない）。出した hash と曲の情報はキャッシュの場所の `library-cache.json` に覚えておき、次の起動では変わったフォルダだけ読み直す

### 配布用の実行ファイル

.NET ごと 1 つの実行ファイルにする（使う PC に .NET は要らない）。Linux の上で Windows 向けも作れる。

```bash
scripts/publish-desktop.sh linux-x64   # dist/linux-x64/OctantPath.Desktop
scripts/publish-desktop.sh win-x64     # dist/win-x64/OctantPath.Desktop.exe
```

`v*` のタグを push すると、GitHub Actions（`.github/workflows/release.yml`）が両方を作り、Linux の分が起動して画面を返すことを確かめ、下書きの GitHub Release に置く。手動の実行では、Windows の分の起動も確かめ、成果物として 1 日だけ置く。ビルドにかかわる pull request では、両方を作って Linux の分の起動を確かめるだけで、成果物は置かない（Actions の保存容量の上限に当たるため。2026-10-23）。MOD の DLL はゲームの DLL が要るので、ゲームのある PC で作る。

実行ファイルに入れたもの（.NET、Newtonsoft.Json、Photino など）のライセンスは、`publish-desktop.sh` が `dist/<RID>/THIRD-PARTY-NOTICES.txt` に書き、Release にも実行ファイルと並べて置く。手で書く分は [`src/OctantPath.Desktop/THIRD-PARTY-NOTICES.txt`](src/OctantPath.Desktop/THIRD-PARTY-NOTICES.txt) で、パッケージを足したらここにも足す（テストが名前と版を確かめる）。

## 設定

`UserData/OctantPath/settings.json`。既定値は [`src/OctantPath.Core/Settings/defaults.json`](src/OctantPath.Core/Settings/defaults.json) にあり、ビルドに埋め込む（MOD・Desktop・テストが同じものを読む）。settings.json には人が変えた項目だけを書き、OctantPath は既定値にファイルの値を重ねて読む。ファイルがなくても、項目を消しても、既定値で動く。OctantPath は足りない項目を書き足さない。OctantPath が settings.json に書くのは、MOD の設定画面（言語・板の大きさ・板の不透明度・下見の曲数・自動ダウンロード・前もって取る曲数・削除の試し運転・お試しの期限・お試しの合計の上限）、曲選択の上の板の「自動で次へ」と Desktop の設定の画面・言語で変えた項目だけで、変えた 1 項目だけを書き、既定値に戻した項目は消す（お試しの片付けに関わる `deleteDryRun`・`keepMinPlays`・`trialExpiryLaunchDays`・`trialMaxMegabytes` は、既定が後で変わっても選んだ値が覆らないように、既定値と同じでも残す）。知らない項目は残す。既定値と同じ値で固定したい（既定値が後で変わっても変えたくない）ときは、settings.json に手で書く。読むときもほかの項目を保存するときも、その項目は書き換えない。画面からその項目を既定値と同じ値で保存したときだけ消える。

| 項目 | 中身 |
|---|---|
| `language` | 画面の言語（`auto`・`en`・`ja` など。下の「言語」。MOD の設定画面で変えられ、OK を押すと使う） |
| `trialFrames`・`trialSlotCount` | お試しの枠（「似ている」の分け方と件数）と、枠の件数を省いたときの件数。既定ではどの枠も 0 件。枠は手札の id（`preset`）を名指しし、choice の入力の値を `choices` に書く（例 `{"genre": "jazz music"}`）。既定の枠は公式の手札を使う: `song` → `song`、`genre` → `genre`（語は空なので、書くまで使わない）、`chart` → `chart`、`difficulty` → `for_you`。起点は送らない。OctantCompass の一覧にない手札と、入力を満たせない手札（起点が必須、choice の値が空か選択肢にない）の枠は飛ばす。枠の名前は手札の名前でなく、言語ファイルの `trialFrame.<id>`（`name` で変えられる）。前の版の重みで書いた枠（`weights`・`genre`・`params`）は読まないので、下のスクリプトで直す |
| `keepMinPlays`・`trialExpiryLaunchDays`・`trialMaxMegabytes` | お試しを「残す」にする遊んだ回数と、お試しの期限（起動日。MOD の設定画面では 1〜60）と、お試しの合計の上限（MB。MOD の設定画面では GB で 1〜100）。期限か合計のどちらかを超えたら、起動直後の片付けで消す。合計を超えた分は古く取ったものから消し、他のプレイリストに入っているものは消さない。settings.json があるのに読めないときと、回数や期限が 1 より小さいとき、合計の上限が正の有限の数でないときは、片付けを止めて何も消さない（「0 = 無制限」とは読まない） |
| `autoDownload`・`deleteDryRun`・`recommendationPanelScale`・`panelOpacity` | 自動ダウンロード、削除の試し運転（既定は切。設定画面では「お試しの曲を自動で消す」を切ったもの）、推薦の板の大きさ、板の背景の不透明度（0.2〜1。どれも MOD の設定画面で変えられる） |
| `mixDraftCount`・`mixPrefetchCount`・`mixListOnPanel`・`mixAutoAdvance` | ミックスの下見に並べる曲数、先に取る曲数（この 2 つは MOD の設定画面で変えられる）、板にミックスの一覧を出すか、遊び終えたら次の曲を自動で選ぶか（曲選択の上の板の「自動で次へ」で切り替える。切っていても結果画面で選んだ曲は選ぶ） |
| `zipSource` | マップの取得元（`BeatSaver` か、手元のフォルダ） |
| `environmentName` | ペアリングで OctantCompass に伝える名前（空なら OctantPath が作る） |

内部の値（普通は変えない。画面には出さないが、settings.json に書けば変えられる）:

| 項目 | 中身 |
|---|---|
| `hostRequestTimeoutSeconds`・`recommendationRequestTimeoutSeconds`・`healthRetryIntervalSeconds` | OctantCompass への要求と推薦の要求の待ち時間、未接続のときに確かめ直す間隔（秒。Desktop の画面も同じ間隔で確かめ直し、つながっている間はその 3 倍ごと） |
| `downloadConcurrency`・`downloadStartsPerSecond` | 同時に取得する数と、1 秒あたりの取得の開始数 |
| `beatSaverApiUrl`・`beatSaverApiIntervalSeconds`・`beatSaverApiMaxBackoffSeconds` | BeatSaver API の URL、問い合わせの間隔（1 秒より短くはならない）と 429 で延ばす上限（秒） |
| `zipMaxDownloadMegabytes`・`zipMaxExtractedMegabytes`・`zipMaxEntries` | zip の受信量・展開後の合計（MB）・entry 数の上限 |
| `cdnMaxMegabytes`・`cdnConcurrency`・`cdnTimeoutSeconds` | Desktop の画面のカバーと試聴（`/api/cdn`）の、1 つの受信量の上限（MB）・同時に取る数（次の起動から）・1 つの待ち時間の上限（秒。空きを待つ間と本文を読み終えるまでを含む） |
| `arcViewerFileTimeoutSeconds` | Desktop が初回に ArcViewer を取るときの、1 つのファイルの待ち時間の上限（秒） |

OctantCompass とは通信だけでつなぐ（契約6（client to OctantCompass）2.0）。OctantPath は OctantCompass のファイルを読まず、起動もしない。つなぐ先とトークンは `UserData/OctantPath/connection.json` にあり、つなぐ先が空なら同じ PC の `127.0.0.1:23287` につなぐ。この版は `host`・`port` だけを読む。ペアリングは MOD の「OctantCompass との接続」の画面で、つなぐ先（同じ PC なら空のまま）と OctantCompass の画面に出る合言葉を入れて「ペアリング」を押す。Proton でも Linux の OctantCompass に 127.0.0.1 でつながる（推測: Wine は Linux のネットワークをそのまま使う）。OctantCompass は自分で常駐し、使っていない間は DB を手放す。

## MOD が書くファイル

どれも今使っているもので、消すと記録や設定がなくなる。

| 場所 | 中身 |
|---|---|
| `UserData/OctantPath/settings.json` | 設定（人が書く。MOD は設定画面で変えた項目だけを書く） |
| `UserData/OctantPath/connection.json` | つなぐ先とトークン |
| `UserData/OctantPath/signals.jsonl` | プレイ・いいねなどの記録（追記だけ） |
| `UserData/OctantPath/recommendations.jsonl` | 推薦 1 回ごとの要求の中身と、返った曲と理由（追記だけ。推薦のまとめに使い、外へは送らない） |
| `UserData/OctantPath/summaries/` | 設定画面の「推薦のまとめを書き出す」で書いたまとめ（手札ごとの表示・選択・取得・飛ばし・プレイ・クリアの数と、最近 30 回の推薦） |
| `UserData/OctantPath/ledger.json` | OctantPath が入れたマップの所有の記録 |
| `UserData/OctantPath/placed.json` | 置いたが、SongCore の読み込みをまだ確かめていないマップ（なければ `[]`） |
| `UserData/OctantPath/state.json` | 導入前の記録を取り込んだ日と、起動した日 |
| `UserData/OctantPath/ui.json` | 動かした板の位置（設定ではなく画面の状態） |
| `UserData/OctantPath/Levels/` | 取得したマップ |
| `UserData/OctantPath/Work/` | 取得中の作業場所（終われば空） |
| `UserData/OctantPath/Deleting/` | 消している途中のマップ（消し終われば空） |
| `Playlists/OctantPath/` | お試しの枠ごとのプレイリスト（手動・ミックス・設定の枠）、ミックスの一時的なプレイリスト、保存したミックス |

`UserData/OctantPath/lang/` は人が置くもので、MOD は書かない。

## 言語

画面の文は `src/OctantPath.Core/Languages/<言語>.json`（キー → 文）にあり、英語（`en`）を既定に日本語（`ja`）を同梱しています（2026-10-11）。MOD の設定画面（Mod Settings の OctantPath）の「言語」で選べ、OK を押すと使います（OK でゲームがメニューを作り直すときに当て直す。画面の文は組み立てるときに決まるため）。設定の `language` が `auto` なら、ゲームの言語（英語以外のとき）、OS の言語、英語の順に選びます。`en` や `ja` と書けば固定できます。

`UserData/OctantPath/lang/<言語>.json` を置くと、同梱の文より優先します。同梱にない言語もファイルを置くだけで選べます。足りないキーは英語で出ます。手札（推薦のボタン）の名前は配布 DB から来るので、ここではなく DB の `{"en": …, "ja": …}` から選びます。ログは訳しません。

推薦はゲームの Logs の OctantPath のログに `[recommend]` で 1 回ごとに書きます（種類・起点・選択肢と、返った曲ごとの BeatSaver のキー・曲名・譜面・理由）。

英語は日本語から推測で付けたものです。

## ライセンス

[MIT](LICENSE)（2026-10-12）。OctantPath Desktop が初回に取ってくる ArcViewer（GPLv3）は同梱していません。
