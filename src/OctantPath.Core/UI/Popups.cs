namespace OctantPath.Core.UI
{
    // 中央の小窓すべて。余白は Popup.Margin、幅は {inner-width}、残りの高さは {fill-height} で受け取り、数を直に書かない。
    // 行の高さを変えるときは、ここを変えて PopupTests を通す（はみ出すと落ちる）
    public static class Popups
    {
        // 下見の一覧の 1 行の中身（BSML の custom-list の雛形）を入れる場所。MOD が DraftCell.Template に置き換える
        public const string DraftCellToken = "DRAFT_CELL_TEMPLATE";

        // 種類の小窓と下見の小窓の幅
        public const float Width = 138f;

        // 左上の戻るボタンの幅
        public const float BackWidth = 9f;

        // 見出しの行の高さ（戻るボタンも同じ高さ）
        public const float HeaderHeight = 7f;

        // 進み具合の帯。地は明るい灰、伸びる所は OctantPath の青（下見の試聴中の行と同じ）。地が暗く伸びる所も暗い青だと、
        // 結果画面のゲームの背景の上で帯が黒く見えて分からなかった（2026-10-18）。
        // 帯は BSML の bg（round-rect-panel）で描く。bg はゲームの板の絵を色の掛け方ごと写すので暗く出る。明るさは MOD が直す（PanelLook.Brighten）。
        // 伸びる所を image にしたら、伸びる所も地も見えなくなった（2026-10-18）
        public const string ProgressTrackColor = "#FFFFFF4D";
        public const string ProgressFillColor = "#4C9AFF";
        // 帯の太さ（2.5 では太すぎた。2026-10-18）
        public const float ProgressHeight = 1.25f;

        // 戻るボタンの下地の色（下線のない、淡い板）
        public const string BackColor = "#FFFFFF1F";

        // 見出しの行: 真ん中に見出し。左右に戻るボタンと同じ幅の空きを取り、見出しが戻るボタンに重ならないようにする
        public static string HeaderRow(string text, float fontSize)
        {
            var inner = Width - 2 * Popup.Margin;
            var textWidth = inner - 2 * BackWidth;
            return $@"
    <horizontal preferred-width='{{inner-width}}' preferred-height='{Popup.F(HeaderHeight)}' spacing='0' child-expand-width='false' child-align='MiddleCenter'>
      <horizontal preferred-width='{Popup.F(BackWidth)}' preferred-height='{Popup.F(HeaderHeight)}'/>
      <text text='{text}' font-size='{Popup.F(fontSize)}' align='Center' preferred-width='{Popup.F(textWidth)}' preferred-height='{Popup.F(HeaderHeight)}'/>
      <horizontal preferred-width='{Popup.F(BackWidth)}' preferred-height='{Popup.F(HeaderHeight)}'/>
    </horizontal>";
        }

        // 左上の戻るボタン。レイアウトに入れず、小窓の左上の角から上にも左にも Popup.Margin だけ離して置く（上と左の距離が同じ）。
        // 高さは見出しの行と同じ。下線の付く BSML の button は使わず、淡い板の上に OctantPath の「戻る」の絵（MOD に同梱。OctantPath.UI.Icons）を重ねる。
        // 板の角の丸みは MOD が決める（RecommendationTab.SharpenBackPlates。そのまま引き伸ばすと丸すぎた）
        // 絵は余白込みで描いてあり（UI/Icons/draw.py）、押せる範囲は板と同じ
        public const string BackIcon = "OctantPath.Icons.back.png";

        public static string BackButton(string backAction)
        {
            var place = $"anchor-min-x='0' anchor-max-x='0' anchor-min-y='1' anchor-max-y='1' pivot-x='0' pivot-y='1' anchor-pos-x='{Popup.F(Popup.Margin)}' anchor-pos-y='{Popup.F(-Popup.Margin)}' size-delta-x='{Popup.F(BackWidth)}' size-delta-y='{Popup.F(HeaderHeight)}'";
            return $@"
  <image id='{backAction}-plate' src='#RoundRect10' {place} image-color='{BackColor}'/>
  <clickable-image src='{BackIcon}' on-click='{backAction}' {place} preserve-aspect='true' hover-hint='{{{{common.back}}}}'/>";
        }

        // 種類（と choice の入力）を選ぶ小窓。タイルは MOD がコードで choice-tiles（BSML の scroll-view の中身）に足す。
        // scroll-view の右の 8 はスクロールの帯に回し、タイルは下見の一覧の行と同じ幅・同じ左端にする（MOD が viewport を詰める）
        public static readonly Popup Choice = new Popup("choice-modal", "show-choice", "hide-choice", Width, Popup.MaxHeight, 0.5f, @"
" + HeaderRow("~choice-seed", 4.4f) + @"
    <scroll-view id='choice-tiles' spacing='" + Popup.F(ChoiceTileSpacing) + @"' child-control-height='false' child-expand-height='false' child-align='UpperLeft' preferred-width='{inner-width}' preferred-height='{fill-height}'></scroll-view>", BackButton("choice-back"));

        // 種類の小窓のタイルの幅。下見の一覧の行と同じ幅で、同じく左に寄せる（左は縁から Popup.Margin、右はスクロールの帯の分あける）
        public static float ChoiceTileWidth => DraftListWidth;

        // 種類の小窓の、タイルと見出しの間・見出しの高さ
        public const float ChoiceTileSpacing = 0.8f;
        public const float ChoiceHeadingHeight = 3.5f;
        // タイルの高さ。下見の一覧の行と同じ。欄に入りきらない分は scroll-view でめくる（縮めると、見出しとタイルが 9 つ以上で読みにくかった）
        public const float ChoiceTileHeight = DraftCellSize;

        // 数秒で消える知らせ（OctantCompass につながらないときなど）
        public static readonly Popup Toast = new Popup("toast-modal", "show-toast", "hide-toast", 90f, 20f, 0f, @"
    <text text='~toast-text' font-size='3.6' align='Center' word-wrapping='true' preferred-width='{inner-width}' preferred-height='{fill-height}'/>");

        // 下見の小窓の起点の行の高さ。上の種類の行のすぐ下に、行の間をあけずに置く（見出しの行と 1 つの vertical にまとめる）。
        // 上揃えにして行も低くする（5 で間をあけると離れすぎた）
        public const float DraftSeedHeight = 3.5f;

        // 下見の一覧の行の高さと、一度に見える行の数
        public const float DraftCellSize = 7.5f;
        public const int DraftVisibleCells = 6;

        // 下見の一覧の高さ。行の高さのちょうど倍にする。半端だと、表を作り直すたびに HMUI の TableView の
        // ReloadDataKeepingPosition が位置を行の頭まで切り下げ、下の方を押すと一覧が上へずれた（#11 で 45.5 にしたとき）
        public static float DraftListHeight => DraftVisibleCells * DraftCellSize;

        // BSML の custom-list の show-scrollbar は、スクロールの帯を一覧の右の外へ 8 だけはみ出して置く（CustomListTableDataHandler の offsetMax）。
        // 一覧の幅をそのぶん狭め、帯は右の空きに入れる。一覧の幅を中身の幅にすると、帯と行の右の記号が小窓の縁に寄りすぎた
        public const float ListScrollbarWidth = 8f;

        // 下見の一覧の幅（行の帯・カバー・記号はこの中。左は縁から Popup.Margin）
        public static float DraftListWidth => Width - 2 * Popup.Margin - ListScrollbarWidth;

        // 下見の小窓。上は種類（何で作っているか）、下は起点。進み具合の行は作成中だけ出るが、出ているものとして高さを数える。
        // 一覧は高さを固定にするので、余りは下に回る
        public static readonly Popup Draft = new Popup("draft-modal", "show-draft", "hide-draft", Width, Popup.MaxHeight, 1f, @"
    <vertical spacing='0' child-control-height='true' child-expand-height='false' preferred-width='{inner-width}' preferred-height='" + Popup.F(HeaderHeight + DraftSeedHeight) + @"'>" + HeaderRow("~draft-title", 4.6f) + @"
    <text text='~draft-seed' font-size='3' align='Top' preferred-width='{inner-width}' preferred-height='" + Popup.F(DraftSeedHeight) + @"'/>
    </vertical>
    <horizontal preferred-width='{inner-width}' preferred-height='" + Popup.F(DraftListHeight) + @"' spacing='0' child-expand-width='false' child-align='MiddleLeft'>
      <custom-list id='draft-list' contents='draft-cells' select-cell='draft-select' visible-cells='" + DraftVisibleCells + "' cell-size='" + Popup.F(DraftCellSize) + "' clickable-cells='true' show-scrollbar='true' list-width='" + Popup.F(DraftListWidth) + "' list-height='" + Popup.F(DraftListHeight) + "' preferred-width='" + Popup.F(DraftListWidth) + "' preferred-height='" + Popup.F(DraftListHeight) + @"'>
        " + DraftCellToken + @"
      </custom-list>
      <horizontal preferred-width='" + Popup.F(ListScrollbarWidth) + "' preferred-height='" + Popup.F(DraftListHeight) + @"'/>
    </horizontal>
    <text text='~draft-detail' font-size='2.4' align='Center' word-wrapping='true' preferred-width='{inner-width}' preferred-height='6'/>
    <!-- 取得の進み具合（作成を押してから曲選択を開き直すまで）。伸びる所の幅は MOD が決める（PanelLook.SetFill）。
         帯はいつも置いておき、作成中だけ描く（MOD が絵を止める）。出すときに行を足すと、一覧が帯の高さだけ動いた（2026-10-18） -->
    <horizontal id='progress-track' bg='round-rect-panel' bg-color='" + ProgressTrackColor + @"' preferred-width='{inner-width}' preferred-height='" + Popup.F(ProgressHeight) + @"' child-align='MiddleLeft' child-expand-width='false' child-control-width='true'>
      <horizontal id='progress-fill' bg='round-rect-panel' bg-color='" + ProgressFillColor + @"' preferred-width='0' preferred-height='" + Popup.F(ProgressHeight) + @"'/>
    </horizontal>
    <horizontal spacing='2' preferred-width='{inner-width}' preferred-height='7' child-expand-width='false' child-align='MiddleCenter'>
      <button text='{{draft.retype}}' on-click='draft-retype' interactable='~draft-idle' preferred-width='30' preferred-height='7' hover-hint='{{draft.retypeHint}}'/>
      <action-button text='{{draft.create}}' on-click='draft-create' interactable='~draft-create-interactable' preferred-width='20' preferred-height='7' hover-hint='{{draft.createHint}}'/>
      <button text='{{common.close}}' on-click='draft-close' preferred-width='20' preferred-height='7'/>
    </horizontal>", BackButton("draft-back"));

        // 下見の小窓の進み具合の帯の幅（全部終わったときの長さ）
        public static float DraftProgressWidth => Draft.InnerWidth;

        public static readonly Popup[] All = { Choice, Toast, Draft };

        // 中央の画面に重ねる BSML（全部の小窓）。言語の {{キー}} と DraftCellToken は呼ぶ側が埋める
        public static string Markup()
        {
            var markup = "<bg>\n";
            foreach (var popup in All)
            {
                markup += popup.Markup + "\n";
            }
            return markup + "</bg>";
        }
    }
}
