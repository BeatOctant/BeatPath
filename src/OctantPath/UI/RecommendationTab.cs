using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using OctantPath.Core.Presets;
using OctantPath.Core.Recommendations;
using OctantPath.Maps;
using OctantPath.Recommendations;
using BeatSaberMarkupLanguage.Attributes;
using OctantPath.Connection;
using BeatSaberMarkupLanguage.Components;
using BeatSaberMarkupLanguage.GameplaySetup;
using HMUI;
using Zenject;
using OctantPath.Core.Languages;

namespace OctantPath.UI
{
    // 曲選択の足元に浮かぶ板。選んでいる譜面を起点に推薦を受け取る。
    // 順は OctantCompass が返した順のまま出す
    internal sealed class RecommendationTab : NotifiableBase, IInitializable, ITickable, IDisposable
    {
        // 板の大きさ（UI の単位）。並びの一覧を出すときと出さないとき（既定）で変わる
        private UnityEngine.Vector2 PanelSize => new UnityEngine.Vector2(120f, (ListVisible ? 62f : 36f) + Core.UI.HomeTiles.Height + 1f);

        // 板に並びの一覧（とそれを操作する「取得」「全部」）を出すか。作成したミックスは曲選択のプレイリストで見えるので、既定では出さない
        [UIValue("list-visible")]
        public bool ListVisible => _settingsHolder.Current.MixListOnPanel;
        private const float FloorClearance = 0.02f;

        private float PanelScale => PanelLook.Scale(_settingsHolder.Current.RecommendationPanelScale);

        private BeatSaberMarkupLanguage.FloatingScreen.FloatingScreen _panel;
        private LevelSelectionNavigationController _levelSelection;
        private bool _panelFailed;
        private float _nextSearch;

        private readonly RecommendationService _recommendations;
        private readonly DownloadService _downloads;
        private StandardLevelDetailViewController _levelDetail;
        private readonly ConnectionService _connection;
        private readonly CoverCache _covers;
        private readonly Signals.SignalService _signals;
        private readonly Trials.TrialService _trials;
        private readonly Installers.SettingsHolder _settingsHolder;
        private readonly PreviewPlayer _preview;
        private readonly PanelPlacement _placement;
        private bool _pinned = true;
        private readonly SettingsMenu _settingsMenu;

        // 板の曲がり（曲選択の画面と同じ半径。CreatePanel）。持ち手を板の手前に出すのに使う
        private float _panelRadius;
        // 板を作ったときの画面（曲選択か OctantPath の設定ページ）。置き直すときの最初の位置はこの画面のすぐ上
        private HMUI.Screen _panelScreen;

        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();

        private ChartRef _seed;
        private string _seedLabel;

        // ミックス: 起点の譜面から作った並び
        private ChartRef _mixSeed;
        private string _mixLabel;
        // 下見: 推薦を並べて「入れ替え」で入れ替え、「作成」で並んでいる曲だけを取得してプレイリストを開く（取得を絞るため）
        private Core.Mix.MixDraft<RecommendationItem> _draft;
        private Preset _draftPreset;
        private string _draftChoiceLabel;
        private Preset _mixPreset;
        private string _mixChoiceLabel;

        // 種類の小窓: OctantCompass の一覧から作るプリセットの行と、choice の入力を選んでいる途中の状態
        private List<Preset> _menuPresets;
        private Preset _choosing;
        private List<PresetInput> _choiceInputs;
        private Dictionary<string, string> _chosen;
        private List<string> _chosenLabels;
        // 種類の小窓は、浮かぶ板ではなく中央の画面（曲の詳細）に重ねて出す
        private BeatSaberMarkupLanguage.Parser.BSMLParserParams _parserParams;
        // 中央の画面に重ねる小窓（種類を選ぶ小窓、知らせ、下見の小窓）。縁からの余白と寸法は OctantPath.Core.UI.Popups が決める
        private static readonly string ChoiceModalBsml = Core.UI.Popups.Markup().Replace(Core.UI.Popups.DraftCellToken, DraftCell.Template);

        [UIComponent("choice-modal-frame")]
        private HMUI.ImageView _choiceFrame;

        [UIComponent("toast-modal-frame")]
        private HMUI.ImageView _toastFrame;

        [UIComponent("draft-modal-frame")]
        private HMUI.ImageView _draftFrame;

        [UIComponent("choice-back-plate")]
        private HMUI.ImageView _choiceBackPlate;

        [UIComponent("draft-back-plate")]
        private HMUI.ImageView _draftBackPlate;

        // 戻るボタンの板の角の丸みを、そのまま引き伸ばしたとき（Simple）の 2/3 にする。
        // Sliced にして、角の絵の大きさ（sprite の border）が UI の単位でその半径になるよう pixelsPerUnitMultiplier を決める
        private void SharpenBackPlates()
        {
            foreach (var plate in new[] { _choiceBackPlate, _draftBackPlate })
            {
                if (plate != null)
                {
                    SharpenPlate(plate, ((UnityEngine.RectTransform)plate.transform).rect.size, "back");
                }
            }
        }

        // 板の大きさ（UI の単位）を渡す。レイアウトに入る板は、まだ並べられていなくて rect が 0 のことがあるので
        private static void SharpenPlate(HMUI.ImageView plate, UnityEngine.Vector2 size, string what)
        {
            var sprite = plate != null ? plate.sprite : null;
            if (sprite == null || sprite.border.x <= 0 || sprite.border.y <= 0 || size.x <= 0 || size.y <= 0)
            {
                return;
            }
            var stretched = UnityEngine.Mathf.Min(sprite.border.x / sprite.rect.width * size.x, sprite.border.y / sprite.rect.height * size.y);
            var radius = stretched * 2f / 3f;
            // sprite の pixelsPerUnit を Canvas の referencePixelsPerUnit で割ったもの（multiplier は含まない）
            var pixelsPerUnit = plate.pixelsPerUnit;
            plate.type = UnityEngine.UI.Image.Type.Sliced;
            plate.pixelsPerUnitMultiplier = sprite.border.x / (radius * pixelsPerUnit);
            Plugin.Log.Debug($"[popup] {what} plate radius {stretched:0.##} -> {radius:0.##} (multiplier {plate.pixelsPerUnitMultiplier:0.##})");
        }

        private const float PinPlateSize = 7f;

        // 推薦のカードの裏に敷く黒の濃さ（推測で決めた値。2026-10-16 にもう少し濃くと言われた）
        private const float HomeTileBacking = 0.45f;

        // 小窓の外周の枠線: ゲームの枠の sprite を Sliced で張る。見つからなければ出さない。押下は受けない
        private void DrawPopupFrames()
        {
            var sprite = new[] { "RoundRect10BorderFade", "RoundRectBigStroke", "RoundRectSmallStroke" }
                .Select(name => UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.Sprite>().FirstOrDefault(s => s.name == name))
                .FirstOrDefault(s => s != null);
            foreach (var frame in new[] { _choiceFrame, _toastFrame, _draftFrame })
            {
                if (frame == null)
                {
                    continue;
                }
                frame.raycastTarget = false;
                if (sprite == null)
                {
                    frame.gameObject.SetActive(false);
                    continue;
                }
                frame.sprite = sprite;
                frame.type = UnityEngine.UI.Image.Type.Sliced;
            }
        }

        private string _toastText = "";
        private int _toastId;

        [UIValue("toast-text")]
        public string ToastText => _toastText;

        // 中央の画面に知らせを出し、数秒で消す
        private async void Toast(string text, float seconds = 3f)
        {
            _toastText = text;
            NotifyPropertyChanged(nameof(ToastText));
            _parserParams?.EmitEvent("show-toast");
            var id = ++_toastId;
            try
            {
                await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(seconds), _lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (id == _toastId)
            {
                _parserParams?.EmitEvent("hide-toast");
            }
        }

        // OctantCompass が使えないときは知らせを出して true を返す
        private bool ToastIfUnavailable()
        {
            var status = _connection.Status;
            if (status.State == Core.Connection.ConnectionState.Ready && _connection.Presets != null)
            {
                return false;
            }
            var text = status.State != Core.Connection.ConnectionState.Ready
                ? $"{Lang.T("connection.unreachable")}\n<size=80%>{Core.Connection.StatusText.Describe(status)}</size>"
                : _connection.PresetsProblem ?? Lang.T("presets.none");
            Toast(text);
            _ = _connection.RefreshNowAsync();
            return true;
        }

        [UIValue("draft-cells")]
        private List<object> _draftCells = new List<object>();

        [UIObject("home-row")]
        private UnityEngine.GameObject _homeRow;

        [UIObject("home-row-2")]
        private UnityEngine.GameObject _homeRow2;

        [UIComponent("draft-list")]
        private CustomCellListTableData _draftList;

        [UIComponent("draft-modal")]
        private ModalView _draftModal;

        [UIComponent("choice-modal")]
        private ModalView _choiceModal;

        // 小窓をすぐ閉じる（アニメーションなし）。曲選択を開き直すときに残らないように、画面が変わる前に呼ぶ
        private void HideModals()
        {
            try
            {
                if (_draftModal != null && _draftModal.isShown)
                {
                    _draftModal.Hide(false);
                }
                if (_choiceModal != null && _choiceModal.isShown)
                {
                    _choiceModal.Hide(false);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[mix] could not hide the popups: {ex.Message}");
            }
        }

        // 下見で選んでいる曲（板の並びの選択とは別）と、下見の小窓に出す文
        private RecommendationItem _draftSelected;
        private string _draftMessage = "";
        private bool _draftFromSong;

        [UIValue("draft-title")]
        public string DraftTitle => _draft == null ? (_busy ? Lang.T("mix.making") : "") : Lang.T("draft.title", new { name = TypeName(_draftPreset, _draftChoiceLabel) });

        [UIValue("draft-seed")]
        // 起点を使わない種類（おすすめなど。OctantCompass へ起点を送らない）では、起点の行は空にする
        public string DraftSeed => _draftFromSong && _mixUsesSeed && _mixLabel != null ? Lang.T("seed.from", new { song = _mixLabel }) : "";

        // 今作っている（作った）種類が起点を使うか。PresetRules.Request が起点を送るかと同じ
        private bool _mixUsesSeed;

        // 今作っている（作った）種類。下見の小窓の戻るで、ひとつ前へ戻すのに使う
        private Preset _draftingPreset;

        [UIValue("choice-seed")]
        public string ChoiceSeed => _seed != null ? Lang.T("seed.from", new { song = _seedLabel }) : Lang.T("seed.noneChoice");

        [UIObject("choice-tiles")]
        private UnityEngine.GameObject _choiceGrid;

        // 種類の小窓の見出しの行（「曲」「譜面」など）
        private void AddHeading(string text)
        {
            var label = BeatSaberMarkupLanguage.BeatSaberUI.CreateText((UnityEngine.RectTransform)_choiceGrid.transform, text, UnityEngine.Vector2.zero, new UnityEngine.Vector2(Core.UI.Popups.ChoiceTileWidth, Core.UI.Popups.ChoiceHeadingHeight));
            label.fontSize = 3.2f;
            label.alignment = TMPro.TextAlignmentOptions.BottomLeft;
            label.color = new UnityEngine.Color(0.75f, 0.85f, 1f, 1f);
            // 見出しは押下を受けない（下のタイルの押せる範囲を取らない）
            label.raycastTarget = false;
        }

        // 種類の小窓のタイル（ゲームの丸いボタン）。上に大きく名前、下に小さく説明
        private void ShowTiles(List<(string Label, string Hint, bool Enabled)> tiles) => ShowTiles(tiles, null);

        // groups があれば、見出しが変わるところに見出しの行を入れる（並びは OctantCompass の順のまま）
        private void ShowTiles(List<(string Label, string Hint, bool Enabled)> tiles, List<string> groups)
        {
            if (_choiceGrid == null)
            {
                return;
            }
            foreach (UnityEngine.Transform child in _choiceGrid.transform)
            {
                UnityEngine.Object.Destroy(child.gameObject);
            }
            // 1 列に並べる。名前の列と説明の列の区切りを縦にそろえる（<pos> で説明の始まりを固定）。
            // タイルは下見の行と同じ高さで、小窓に入りきらない分はスクロールでめくる
            var height = Core.UI.Popups.ChoiceTileHeight;
            var fontSize = 3.6f;
            for (var i = 0; i < tiles.Count; i++)
            {
                var index = i;
                var tile = tiles[i];
                var group = groups != null && i < groups.Count ? groups[i] : null;
                if (!string.IsNullOrEmpty(group) && (i == 0 || groups[i - 1] != group))
                {
                    AddHeading(group);
                }
                var text = $"<size=115%>{tile.Label}</size>" + (string.IsNullOrEmpty(tile.Hint) ? "" : $"<pos=34%><size=80%><color=#BBBBBB>{tile.Hint}</color></size>");
                try
                {
                    var made = Tile.Create(_choiceGrid, text, Core.UI.Popups.ChoiceTileWidth, height, () => ChoiceSelect(null, index), TMPro.TextAlignmentOptions.Left, fontSize);
                    made.Interactable = tile.Enabled;
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error($"[mix] could not make the tile {tile.Label}: {ex}");
                }
            }
            // 並べ直したら一番上から見せる
            ChoiceScroll()?.ScrollTo(0f, false);
        }

        // 種類の小窓の scroll-view。小窓が閉じている間は親が非アクティブで、GetComponentInParent は非アクティブの親を見ないので null になった
        // （起動時に「scroll view was not found」）。非アクティブも含めて探す
        private HMUI.ScrollView ChoiceScroll() =>
            _choiceGrid != null ? _choiceGrid.GetComponentsInParent<HMUI.ScrollView>(true).FirstOrDefault() : null;

        // 種類の小窓の scroll-view: 見える欄（viewport）を右の 8 だけ詰め、スクロールの帯はその空きに置く。
        // タイルは下見の一覧の行と同じ幅・同じ左端になる。帯は scroll-view の右の端に寄る（下見の一覧の帯と同じ位置）
        private void FitChoiceScroll()
        {
            var scroll = ChoiceScroll();
            if (scroll == null)
            {
                Plugin.Log.Warn("[popup] the type popup's scroll view was not found");
                return;
            }
            var gap = Core.UI.Popups.ListScrollbarWidth;
            // 見える欄: scroll-view いっぱいから右の帯の分だけ詰める
            var viewport = scroll.viewportTransform;
            viewport.anchorMin = UnityEngine.Vector2.zero;
            viewport.anchorMax = UnityEngine.Vector2.one;
            viewport.offsetMin = UnityEngine.Vector2.zero;
            viewport.offsetMax = new UnityEngine.Vector2(-gap, 0f);
            // 中身: 見える欄と同じ幅にして上から並べる。タイルの幅は見える欄と同じ（BSML は中身の幅をタイルの幅に縮めて真ん中に置くので、欄の幅とずれると左右に寄った）
            var content = scroll.contentTransform;
            var fitter = content.GetComponent<UnityEngine.UI.ContentSizeFitter>();
            if (fitter != null)
            {
                fitter.horizontalFit = UnityEngine.UI.ContentSizeFitter.FitMode.Unconstrained;
            }
            content.anchorMin = new UnityEngine.Vector2(0f, 1f);
            content.anchorMax = new UnityEngine.Vector2(1f, 1f);
            content.pivot = new UnityEngine.Vector2(0f, 1f);
            content.offsetMin = new UnityEngine.Vector2(0f, content.offsetMin.y);
            content.offsetMax = new UnityEngine.Vector2(0f, content.offsetMax.y);
            // スクロールの帯の入れ物: scroll-view の右端に幅 8 で固定する（前は全面に引き伸ばして、帯が真ん中に来た）
            var indicator = scroll.GetComponentInChildren<HMUI.VerticalScrollIndicator>(true);
            if (indicator != null && indicator.transform.parent is UnityEngine.RectTransform bar && bar != scroll.transform)
            {
                bar.anchorMin = new UnityEngine.Vector2(1f, 0f);
                bar.anchorMax = new UnityEngine.Vector2(1f, 1f);
                bar.pivot = new UnityEngine.Vector2(1f, 0.5f);
                bar.anchoredPosition = UnityEngine.Vector2.zero;
                bar.sizeDelta = new UnityEngine.Vector2(gap, 0f);
            }
        }

        [UIValue("draft-detail")]
        public string DraftDetail => _draftMessage;

        // 作成中（取得から曲選択を開き直すまで）。小窓は開いたまま進み具合を見せる
        private bool _creating;

        [UIObject("progress-track")]
        private UnityEngine.GameObject _progressTrack;

        [UIObject("progress-fill")]
        private UnityEngine.GameObject _progressFill;

        // 作成中の取得の進み具合。曲ごとの終わりだけで伸ばすと、1〜2 曲では取り終えるまで伸びず、終わった瞬間に満ちていた（2026-10-18）。
        // なので結果画面の帯と同じく、おもに時間で伸ばし、受け取った割合と終わった曲数の大きい方へ寄せる（TickCreateProgress）
        private readonly SmoothProgress _createSmooth = new SmoothProgress();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> _createReceived = new System.Collections.Concurrent.ConcurrentDictionary<string, double>();
        private int _createDone;
        private int _createTotal;
        private bool _createDownloading;
        private float _createStartedAt;

        // 1 曲を取るのにかかる時間の目安（秒）。推測で 3 秒から始め、作成のたびに実際の時間へ寄せる
        private float _createSecondsPerSong = 3f;

        private void StartCreateProgress(int total)
        {
            _createReceived.Clear();
            _createDone = 0;
            _createTotal = total;
            _createDownloading = total > 0;
            _createStartedAt = UnityEngine.Time.unscaledTime;
            _createSmooth.Stop();
            if (_createDownloading)
            {
                _createSmooth.Start();
            }
            DrawCreateProgress();
            ShowCreateTrack();
        }

        // 帯は行に置いたままにし、作成中だけ見せる（出し入れすると、一覧が帯の高さだけ動いた）
        private void ShowCreateTrack()
        {
            foreach (var part in new[] { _progressTrack, _progressFill })
            {
                var background = part != null ? part.GetComponent<BeatSaberMarkupLanguage.Components.Backgroundable>()?.Background : null;
                if (background != null)
                {
                    background.enabled = _creating;
                }
            }
        }

        // 取り終えた（このあと曲の読み直しが続く）。帯は残りをなめらかに埋めて、満ちたまま残す
        private void FinishCreateDownloads()
        {
            if (!_createDownloading)
            {
                return;
            }
            _createDownloading = false;
            var perSong = (UnityEngine.Time.unscaledTime - _createStartedAt) / _createTotal;
            _createSecondsPerSong = UnityEngine.Mathf.Lerp(_createSecondsPerSong, UnityEngine.Mathf.Max(0.3f, perSong), 0.3f);
        }

        private void TickCreateProgress()
        {
            if (!_creating)
            {
                return;
            }
            float? fraction = null;
            if (_createDownloading)
            {
                fraction = (float)Math.Max(_createDone, _createReceived.Values.Sum()) / _createTotal;
            }
            if (_createSmooth.Update(fraction, _createSecondsPerSong * _createTotal))
            {
                DrawCreateProgress();
            }
        }

        private void DrawCreateProgress()
        {
            if (_progressFill == null)
            {
                return;
            }
            var shown = _createSmooth.Visible ? _createSmooth.Shown : _createDownloading ? 0f : 1f;
            PanelLook.SetFill(_progressFill, shown, Core.UI.Popups.DraftProgressWidth);
        }

        private void SetProgress(int done, int total, string text)
        {
            _createDone = done;
            if (total > 0 && done >= total)
            {
                FinishCreateDownloads();
            }
            _draftMessage = text;
            NotifyPropertyChanged(nameof(DraftDetail));
        }

        [UIValue("draft-idle")]
        public bool DraftIdle => !_busy;

        [UIValue("draft-create-interactable")]
        public bool DraftCreateInteractable => !_busy && _draft != null && _draft.Picked.Count > 0;

        [UIAction("draft-close")]
        private void DraftClose()
        {
            _preview.Stop();
            _preview.StopPrefetch();
            _parserParams?.EmitEvent("hide-draft");
        }

        [UIAction("draft-retype")]
        private void DraftRetype()
        {
            _preview.Stop();
            _preview.StopPrefetch();
            _parserParams?.EmitEvent("hide-draft");
            MixClick();
        }

        // 下見の小窓の左上の戻る: ひとつ前の小窓へ。choice の入力がある種類はその選択へ、なければ種類の小窓へ（ホームからなら閉じるだけ）
        [UIAction("draft-back")]
        private void DraftBack()
        {
            if (_busy)
            {
                return;
            }
            _preview.Stop();
            _preview.StopPrefetch();
            _parserParams?.EmitEvent("hide-draft");
            var preset = _draftingPreset;
            if (preset != null && PresetRules.Choices(preset).Count > 0)
            {
                _menuPresets = null;
                _parserParams?.EmitEvent("show-choice");
                PresetChosen(preset, _draftFromSong);
                return;
            }
            if (_draftFromSong)
            {
                MixClick();
            }
        }

        // 種類の小窓の左上の戻る: choice を選んでいる途中ならひとつ前の選択へ、曲の画面から種類を選んだなら種類の一覧へ、それ以外は閉じる
        [UIAction("choice-back")]
        private void ChoiceBack()
        {
            if (_choosing != null && _choiceInputs != null && _chosen != null && _chosen.Count > 0)
            {
                _chosen.Remove(_choiceInputs[_chosen.Count - 1].Name);
                _chosenLabels.RemoveAt(_chosenLabels.Count - 1);
                ShowNextChoice();
                return;
            }
            if (_choosing != null && _draftFromSong)
            {
                MixClick();
                return;
            }
            _choosing = null;
            _menuPresets = null;
            _parserParams?.EmitEvent("hide-choice");
        }

        [UIAction("draft-create")]
        private void DraftCreate()
        {
            if (!_busy && _draft != null)
            {
                _ = CreateMixAsync();
            }
        }

        // 下見の曲を選ぶ: 取らずに試聴だけを流す。一番下の「曲を足す」の行なら、曲を足す
        // custom-list は選んだ行のデータ（DraftCell）を渡してくる
        [UIAction("draft-select")]
        private void DraftSelectCell(TableView tableView, object cell)
        {
            var items = _draft?.Picked;
            if (cell is DraftCell addRow && addRow.IsAddRow)
            {
                AddItem();
                return;
            }
            if (cell is DraftCell draftCell && items != null)
            {
                DraftSelect(tableView, items.ToList().IndexOf(draftCell.Item));
            }
        }

        private void DraftSelect(TableView tableView, int index)
        {
            var items = _draft?.Picked;
            if (items == null || index < 0 || index >= items.Count)
            {
                return;
            }
            var item = items[index];
            _draftSelected = item;
            if (item.Version?.PreviewUrl != null)
            {
                _preview.Play(item.Version.PreviewUrl);
            }
            _draftMessage = "";
            // 一覧は作り直さず、行の見た目だけを移す（DraftCell.SetPlaying）
            var loading = _preview.Progress(item.Version?.PreviewUrl).HasValue;
            _draftSelectedCell = null;
            foreach (var draftCell in _draftCells.OfType<DraftCell>())
            {
                draftCell.SetPlaying(draftCell.Item == item, loading);
                if (draftCell.Item == item)
                {
                    _draftSelectedCell = draftCell;
                }
            }
            NotifyDraft();
        }

        // 選んだ曲の行（毎フレーム探さないために覚える。一覧を作り直したら FillDraftList が差し替える）
        private DraftCell _draftSelectedCell;

        // 下見で選んだ曲の試聴を取っている間、その行に進み具合を出す（DraftCell.ShowLoading）
        private void TickPreviewLoading()
        {
            var item = _draftSelected;
            var cell = _draftSelectedCell;
            if (item == null || cell == null || cell.Item != item)
            {
                return;
            }
            cell.ShowLoading(_preview.Progress(item.Version?.PreviewUrl), _preview.TypicalSeconds);
        }

        private void NotifyDraft()
        {
            NotifyPropertyChanged(nameof(DraftTitle));
            NotifyPropertyChanged(nameof(DraftSeed));
            NotifyPropertyChanged(nameof(DraftDetail));
            NotifyPropertyChanged(nameof(DraftIdle));
            NotifyPropertyChanged(nameof(DraftCreateInteractable));
        }

        private void ShowDraftModal()
        {
            _parserParams?.EmitEvent("hide-choice");
            _parserParams?.EmitEvent("show-draft");
        }

        private void FillDraftList()
        {
            if (_draftList == null)
            {
                return;
            }
            var items = _draft != null ? _draft.Picked.ToList() : new List<RecommendationItem>();
            var actions = new DraftCell.Actions { Swap = ReplaceItem, Discard = DiscardItem, Download = DownloadOne, Play = PlayDraftItem };
            _draftCells = items.Select(i => (object)new DraftCell(i, i == _draftSelected, !_busy, CanDownloadDraft(i), CanPlayDraft(i), DraftState, _covers, actions)).ToList();
            // 一番下に「曲を足す」の行（並べられる上限までは出す。入れ替え用が尽きていれば、押したときに追加を受け取る）
            if (_draft != null && !_draft.Full)
            {
                _draftCells.Add(DraftCell.AddRow(_covers));
            }
            _draftSelectedCell = _draftSelected != null ? _draftCells.OfType<DraftCell>().FirstOrDefault(c => c.Item == _draftSelected) : null;
            // 試聴を取っている途中の行は、作り直しても取っている途中の見た目から出す（全体の塗りが一瞬出ないように）
            if (_draftSelected != null && _preview.Progress(_draftSelected.Version?.PreviewUrl).HasValue)
            {
                _draftSelectedCell?.StartLoading();
            }
            _draftList.Data = _draftCells;
            // 選び直しても一覧の位置を変えない。ReloadDataKeepingPosition は位置を行の頭へ切り下げるので
            // （一覧の高さが行の倍でないと、一番下では選ぶたびに上へずれた）、位置そのものを覚えて戻す
            var scroll = _draftList.TableView.scrollView;
            var position = scroll != null ? scroll.position : 0f;
            _draftList.TableView.ReloadData();
            _draftList.TableView.ScrollToPosition(position, false);
            var index = _draftSelected != null ? items.IndexOf(_draftSelected) : -1;
            if (index >= 0)
            {
                _draftList.TableView.SelectCellWithIdx(index);
            }
            else
            {
                _draftList.TableView.ClearSelection();
            }
        }

        // カバーが届いたら、行の絵だけを差し替える（一覧を作り直すと BSML を読み直して重い）
        private void RefreshDraftCovers()
        {
            foreach (var draftCell in _draftCells.OfType<DraftCell>())
            {
                draftCell.RefreshCover();
            }
        }

        private System.Threading.Tasks.Task LoadDraftCoversAsync()
        {
            var draft = _draft;
            return LoadCoversAsync(draft?.Picked.ToList() ?? new List<RecommendationItem>(), () => draft == _draft, RefreshDraftCovers);
        }

        // カバーを 1 枚ずつ取り、取れるたびに refresh で表を作り直す。wanted が偽になったらやめる
        private async System.Threading.Tasks.Task LoadCoversAsync(List<RecommendationItem> items, Func<bool> wanted, Action refresh)
        {
            foreach (var item in items)
            {
                if (_lifetime.IsCancellationRequested || !wanted())
                {
                    return;
                }
                var url = item.Version?.CoverUrl;
                if (url == null || _covers.TryGet(url, out _))
                {
                    continue;
                }
                await _covers.GetAsync(url, _lifetime.Token);
                if (wanted())
                {
                    refresh();
                }
            }
        }

        private Core.Mix.MixQueue<RecommendationItem> _mix;
        private List<RecommendationItem> _items = new List<RecommendationItem>();
        private RecommendationItem _selected;
        private string _lastPlayedHash;
        private ChartRef _lastPlayedChart;
        // ミックスのプレイリスト（曲選択を開き直すときの行き先）
        private BeatmapLevelPack _mixPack;
        // 結果画面で選んだ曲を、まだミックスのプレイリストのファイルに書いていない
        private bool _mixPlaylistStale;
        // 遊び終えたあと、曲選択に戻ったら次の曲へ進む。結果画面の間は進まない（結果画面の間に曲選択を開き直すと画面が壊れた）
        private bool _advancePending;
        // 結果画面で選んだ曲（ミックスがないときも、これを次の曲にする）
        private RecommendationItem _pendingPick;
        // 曲選択が続けて出ている時刻。戻った直後、結果画面が出る前の一瞬では進まないように使う
        private float _songListSince = -1f;
        private const float AdvanceDelay = 0.5f;

        // 結果画面のおすすめ
        private readonly ResultsPanel _resultsPanel;

        // ユーザーが曲選択でミックスの外の曲を選んでいる
        private bool _offMix;
        private readonly GameScenesManager _gameScenesManager;
        private readonly PlaylistSync _playlist;
        private bool _busy;
        private string _message = "";

        [UIComponent("list")]
        private CustomListTableData _list;

        public RecommendationTab(RecommendationService recommendations, DownloadService downloads, [InjectOptional] StandardLevelDetailViewController levelDetail, ConnectionService connection, CoverCache covers, Signals.SignalService signals, Trials.TrialService trials, Installers.SettingsHolder settingsHolder, PreviewPlayer preview, PanelPlacement placement, GameScenesManager gameScenesManager, PlaylistSync playlist, SettingsMenu settingsMenu)
        {
            _settingsMenu = settingsMenu;
            _gameScenesManager = gameScenesManager;
            _playlist = playlist;
            _placement = placement;
            _preview = preview;
            _settingsHolder = settingsHolder;
            _trials = trials;
            _signals = signals;
            _covers = covers;
            _recommendations = recommendations;
            _downloads = downloads;
            _levelDetail = levelDetail;
            _connection = connection;
            _resultsPanel = new ResultsPanel(covers, preview, ChooseFromResults, () => PanelScale, () => _settingsHolder.Current.PanelOpacity, PanelBottom, MovePanelBottom, () => !_pinned);
        }

        public void Initialize()
        {
            _connection.StatusChanged += HandleConnectionChanged;
            _settingsHolder.Changed += HandleSettingsChanged;
            _gameScenesManager.transitionDidFinishEvent += HandleTransitionDidFinish;
            _downloads.Changed += HandleDownloadsChanged;
            SongCore.Loader.OnLevelPacksRefreshed += HandleLevelPacksRefreshed;
            _signals.ProblemChanged += HandleSignalProblemChanged;
            _trials.StatusChanged += HandleSignalProblemChanged;
            _connection.PresetsChanged += HandlePresetsChanged;
            if (_levelDetail == null)
            {
                _levelDetail = UnityEngine.Resources.FindObjectsOfTypeAll<StandardLevelDetailViewController>().FirstOrDefault();
            }
            Plugin.Log.Debug($"[panel] level selection {(_levelSelection != null ? "found" : "not found yet")}");
            if (_levelDetail == null)
            {
                Plugin.Log.Warn("StandardLevelDetailViewController was not found; the seed cannot be read");
                return;
            }
            _levelDetail.didChangeDifficultyBeatmapEvent += HandleSelectionChanged;
            _levelDetail.didChangeContentEvent += HandleContentChanged;
        }

        public void Dispose()
        {
            _lifetime.Cancel();
            _resultsPanel.Destroy();
            if (_panel != null)
            {
                _panel.HandleReleased -= HandlePanelReleased;
                UnityEngine.Object.Destroy(_panel.gameObject);
            }
            _connection.StatusChanged -= HandleConnectionChanged;
            _settingsHolder.Changed -= HandleSettingsChanged;
            _gameScenesManager.transitionDidFinishEvent -= HandleTransitionDidFinish;
            _downloads.Changed -= HandleDownloadsChanged;
            SongCore.Loader.OnLevelPacksRefreshed -= HandleLevelPacksRefreshed;
            _signals.ProblemChanged -= HandleSignalProblemChanged;
            _trials.StatusChanged -= HandleSignalProblemChanged;
            _connection.PresetsChanged -= HandlePresetsChanged;
            if (_levelDetail != null)
            {
                _levelDetail.didChangeDifficultyBeatmapEvent -= HandleSelectionChanged;
                _levelDetail.didChangeContentEvent -= HandleContentChanged;
            }
        }

        // 板は、曲選択か OctantPath の設定ページを初めて開いたときに作る（メニューを組み立てている間には作らない）。
        // 置き場所は、その画面（どちらもゲームの真ん中の画面）のすぐ上で、同じ距離・同じ曲がり方にそろえる
        private void CreatePanel(HMUI.Screen screen)
        {
            var rect = (UnityEngine.RectTransform)screen.transform;
            var (position, rotation, radius) = Placement(screen, true);

            _panel = BeatSaberMarkupLanguage.FloatingScreen.FloatingScreen.CreateFloatingScreen(PanelSize, true, position, rotation, radius, true);
            _panelRadius = radius;
            _panelScreen = screen;
            _panel.name = "OctantPathRecommendations";
            _panel.transform.localScale = UnityEngine.Vector3.one * PanelScale;
            // 持ち手は上の縁の真ん中のつまみ。固定している間は出さない（OBSControl と同じ）
            _panel.HandleSide = BeatSaberMarkupLanguage.FloatingScreen.FloatingScreen.Side.Top;
            PanelLook.PlaceKnob(_panel);
            PanelLook.PaintKnob(_panel);
            _panel.ShowHandle = false;
            _panel.HandleReleased += HandlePanelReleased;
            PanelLook.MakeTranslucent(_panel, _settingsHolder.Current.PanelOpacity);
            BeatSaberMarkupLanguage.BSMLParser.Instance.Parse(Lang.Fill(BeatSaberMarkupLanguage.Utilities.GetResourceContent(typeof(RecommendationTab).Assembly, "OctantPath.UI.RecommendationTab.bsml")), _panel.gameObject, this);
            _panel.gameObject.SetActive(false);
            // 固定のボタンの板も、戻るボタンと同じ角にする（大きさは RecommendationTab.bsml の 7 x 7）
            SharpenPlate(_pinPlate, new UnityEngine.Vector2(PinPlateSize, PinPlateSize), "pin");
            SharpenPlate(_resetPlate, new UnityEngine.Vector2(PinPlateSize, PinPlateSize), "reset");
            SharpenPlate(_autoNextPlate, AutoNextPlateSize, "auto-next");
            ShowAutoNext();
            BuildHomeButtons();
            FillList();
            AddStartMixButton();
            Plugin.Log.Debug($"[panel] created at {position} above the song list screen at {rect.position} (radius {radius})");
        }

        // 板の置き場所。既定は screen（曲選択の画面）のすぐ上で、少し手前へ（下向きに）傾ける。
        // useSaved なら、持ち手で動かして残した位置を使う
        private (UnityEngine.Vector3 Position, UnityEngine.Quaternion Rotation, float Radius) Placement(HMUI.Screen screen, bool useSaved)
        {
            var rect = (UnityEngine.RectTransform)screen.transform;
            var halfPanel = PanelSize.y * PanelScale / 2f;
            var (position, rotation, radius) = PanelLook.Above(screen, PanelSize.y * PanelScale);
            if (useSaved && _placement.TryLoad(out var savedPosition, out var savedRotation))
            {
                position = savedPosition;
                rotation = savedRotation;
            }
            // 床より下に来るときは、床に貼り付ける（向きは曲選択の画面にそろえる）
            if (position.y - halfPanel < FloorClearance)
            {
                var yaw = rect.rotation.eulerAngles.y;
                position = new UnityEngine.Vector3(position.x, FloorClearance, position.z);
                rotation = UnityEngine.Quaternion.Euler(90f, yaw, 0f);
            }
            return (position, rotation, radius);
        }

        // 曲の詳細（アルバムアートの板）の左上に「ミックス」（記号 ♪）を置く。PLAY の並びは混んでいてはみ出したので、
        // 並びの外に、レイアウトに加わらない形で重ねる（PLAY の動きは変えない）
        private void AddStartMixButton()
        {
            try
            {
                if (_levelDetail == null)
                {
                    Plugin.Log.Warn("[startmix] the level detail was not found; the type popup and the button are not added");
                    return;
                }
                // 種類の小窓をこの画面（中央）に置く
                _parserParams = BeatSaberMarkupLanguage.BSMLParser.Instance.Parse(Lang.Fill(ChoiceModalBsml), _levelDetail.gameObject, this);
                DrawPopupFrames();
                SharpenBackPlates();
                PanelLook.Brighten(_progressTrack);
                PanelLook.Brighten(_progressFill);
                ShowCreateTrack();
                FitChoiceScroll();
                var view = _levelDetail.GetComponentsInChildren<StandardLevelDetailView>(true).FirstOrDefault();
                if (view == null)
                {
                    Plugin.Log.Warn("[startmix] the level detail view was not found");
                    return;
                }
                var host = new StartMixButton(MixClick);
                BeatSaberMarkupLanguage.BSMLParser.Instance.Parse(Lang.Fill("<button id='button' text='♪' on-click='click' font-size='4.5' hover-hint='{{songButton.hint}}'/>"), view.gameObject, host);
                if (host.Button == null)
                {
                    return;
                }
                var layout = host.Button.GetComponent<UnityEngine.UI.LayoutElement>() ?? host.Button.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
                layout.ignoreLayout = true;
                var rect = (UnityEngine.RectTransform)host.Button.transform;
                rect.anchorMin = new UnityEngine.Vector2(0f, 1f);
                rect.anchorMax = new UnityEngine.Vector2(0f, 1f);
                rect.pivot = new UnityEngine.Vector2(0f, 1f);
                rect.anchoredPosition = new UnityEngine.Vector2(StartMixOffset.x, -StartMixOffset.y);
                rect.sizeDelta = StartMixSize;
                Plugin.Log.Debug($"[startmix] added at the top left of {view.name} ({((UnityEngine.RectTransform)view.transform).rect.size})");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[startmix] could not add the button: {ex}");
            }
        }

        // アルバムアートの左上からの位置と大きさ（UI の単位。推測で決めた値）
        private static readonly UnityEngine.Vector2 StartMixOffset = new UnityEngine.Vector2(2f, 2f);
        private static readonly UnityEngine.Vector2 StartMixSize = new UnityEngine.Vector2(7f, 7f);

        // 曲選択（ソロの曲の一覧）が出ている間だけ板を出す
        public void Tick()
        {
            _resultsPanel.Tick();
            TickAdvance();
            TickPreviewLoading();
            TickCreateProgress();
            if (_panelFailed)
            {
                return;
            }
            if (_levelSelection == null)
            {
                // 見つかるまで、1 秒に 1 回だけ探す（毎フレーム探すと重い）
                if (UnityEngine.Time.unscaledTime < _nextSearch)
                {
                    return;
                }
                _nextSearch = UnityEngine.Time.unscaledTime + 1f;
                _levelSelection = UnityEngine.Resources.FindObjectsOfTypeAll<LevelSelectionNavigationController>().FirstOrDefault();
                if (_levelSelection == null && !_settingsMenu.IsOpen)
                {
                    return;
                }
            }
            var inSongList = _levelSelection != null && _levelSelection.isActivated && _levelSelection.isInViewControllerHierarchy;
            // OctantPath の設定ページを開いている間も出す（板の大きさを変えながら見られるように。2026-10-16）。
            // 曲選択を一度も開いていなくても、設定ページの画面の位置から板を作れる
            var inSettings = _settingsMenu.IsOpen;
            var show = inSongList || inSettings;
            if (_panel == null)
            {
                if (!show)
                {
                    return;
                }
                try
                {
                    var screen = inSongList ? _levelSelection.screen : _settingsMenu.Screen;
                    if (screen == null)
                    {
                        return;
                    }
                    CreatePanel(screen);
                }
                catch (Exception ex)
                {
                    _panelFailed = true;
                    Plugin.Log.Error($"[panel] could not be created: {ex}");
                    return;
                }
            }
            if (_panel.gameObject.activeSelf != show)
            {
                if (!show)
                {
                    HideModals();
                }
                _panel.gameObject.SetActive(show);
                Plugin.Log.Debug($"[panel] {(show ? "shown" : "hidden")}");
            }
        }

        // 設定画面で大きさを変えたら、その場で板に反映する
        private void HandleSettingsChanged()
        {
            if (_panel != null)
            {
                _panel.transform.localScale = UnityEngine.Vector3.one * PanelScale;
                PanelLook.MakeTranslucent(_panel, _settingsHolder.Current.PanelOpacity);
            }
            _resultsPanel.Rescale();
            ShowAutoNext();
        }

        [UIComponent("pin-plate")]
        private HMUI.ImageView _pinPlate;

        [UIValue("pin-hint")]
        public string PinHint => Lang.T(_pinned ? "panel.move" : "panel.pin") + ": " + Lang.T("panel.pinHint");

        [UIAction("pin-click")]
        private void PinClick()
        {
            _pinned = !_pinned;
            if (_panel != null)
            {
                _panel.ShowHandle = !_pinned;
            }
            NotifyPropertyChanged(nameof(PinHint));
            NotifyPropertyChanged(nameof(ResetVisible));
        }

        [UIComponent("reset-plate")]
        private HMUI.ImageView _resetPlate;

        // 置き直すボタンは、移動にしている間だけピンの左に出す（2026-10-24）
        [UIValue("reset-visible")]
        public bool ResetVisible => !_pinned;

        // 板を最初の位置（作ったときの画面のすぐ上）へ戻し、残した置き場所を消す
        [UIAction("reset-click")]
        private void ResetClick()
        {
            if (_panel == null || _panelScreen == null)
            {
                return;
            }
            var (position, rotation, _) = Placement(_panelScreen, false);
            _panel.transform.SetPositionAndRotation(position, rotation);
            _placement.Clear();
            Plugin.Log.Debug($"[panel] put back at {position}");
        }

        private void HandlePanelReleased(object sender, BeatSaberMarkupLanguage.FloatingScreen.FloatingScreenHandleEventArgs args)
        {
            _placement.Save(args.Position, args.Rotation);
            Plugin.Log.Debug($"[panel] moved to {args.Position}");
        }

        // 結果画面の板は、この板と下の縁をそろえて置く（同じ向き・同じ曲がり方）。この板がまだなければ null
        private PanelBase? PanelBottom()
        {
            if (_panel == null)
            {
                return null;
            }
            var t = _panel.transform;
            return new PanelBase(t.position - t.up * (_panel.ScreenSize.y * t.lossyScale.y / 2f), t.rotation, _panelRadius);
        }

        // 結果画面の板を動かした: この板も、下の縁がそこに来るように動かし、置き場所を覚える
        private void MovePanelBottom(UnityEngine.Vector3 bottom, UnityEngine.Quaternion rotation)
        {
            if (_panel == null)
            {
                return;
            }
            var t = _panel.transform;
            var center = bottom + rotation * UnityEngine.Vector3.up * (_panel.ScreenSize.y * t.lossyScale.y / 2f);
            t.SetPositionAndRotation(center, rotation);
            _placement.Save(center, rotation);
            Plugin.Log.Debug($"[panel] moved with the results panel to {center}");
        }

        [UIValue("header-text")]
        public string HeaderText
        {
            get
            {
                var mix = _mix == null ? SeedText
                    : _offMix ? Lang.T("mix.offMix", new { song = _mixLabel })
                    : Lang.T("mix.playing", new { mix = MixName(), filter = FilterName(_mix.Filter) });
                return $"{ConnectionText}\n{mix}";
            }
        }

        public string ConnectionText => $"OctantCompass: {Core.Connection.StatusText.Describe(_connection.Status)}" + (_signals.Problem != null ? $"\n{_signals.Problem}" : "") + (string.IsNullOrEmpty(_trials.Status) ? "" : $"  {_trials.Status}");

        public string SeedText => _seed != null ? Lang.T("seed.from", new { song = _seedLabel }) : Lang.T("seed.nonePick");

        [UIValue("mix-interactable")]
        public bool MixInteractable => !_busy && _mix != null;

        [UIValue("detail-text")]
        public string DetailText
        {
            get
            {
                var item = _selected;
                if (item == null)
                {
                    return _items.Count == 0 ? _message : $"{_message}\n{Lang.T("mix.listHint")}";
                }
                var views = item.Candidate.Reason?.Views;
                var viewText = views == null ? "" : string.Join("  ", views.Select(v => $"{v.Key} {v.Value.ToString("0.00", CultureInfo.InvariantCulture)}"));
                var difficulty = item.Candidate.Reason?.Difficulty;
                var aim = difficulty?.ClearChance != null ? "  " + Lang.T("item.clearChance", new { chance = difficulty.ClearChance.Value.ToString("P0", CultureInfo.InvariantCulture) }) : "";
                return $"{Core.UI.DisplayText.Line(item.Title)} / {Core.UI.DisplayText.Line(item.Artist)} [{Core.UI.DisplayText.Line(item.Mapper)}]  {Lang.T("item.suggested", new { characteristic = item.Candidate.Characteristic, difficulty = item.Candidate.Difficulty })}{aim}\n{_message}";
            }
        }

        private string MixName() => MixName(_mixPreset, _mixChoiceLabel);

        // 「種類の名前: choice の名前」
        private static string TypeName(Preset preset, string choiceLabel) => (preset?.Label ?? "") + (string.IsNullOrEmpty(choiceLabel) ? "" : ": " + choiceLabel);

        private string MixName(Preset preset, string choiceLabel)
        {
            var name = TypeName(preset, choiceLabel);
            return preset != null && PresetRules.UsesSeed(preset) && _mixSeed != null ? Lang.T("mix.nameFrom", new { song = _mixLabel, name }) : Lang.T("mix.name", new { name });
        }


        [UIValue("filter-text")]
        public string FilterText => _mix == null || _mix.Filter == Core.Mix.MixFilter.All ? Lang.T("filter.all") : FilterName(_mix.Filter);

        private static string FilterName(Core.Mix.MixFilter filter) => Lang.T(filter == Core.Mix.MixFilter.Familiar ? "filter.familiar" : filter == Core.Mix.MixFilter.New ? "filter.new" : "filter.all");

        private void HandleSelectionChanged(StandardLevelDetailViewController controller) => ReadSeedAndNotify();

        private void HandleContentChanged(StandardLevelDetailViewController controller, StandardLevelDetailViewController.ContentType type) => ReadSeedAndNotify();

        private void HandleConnectionChanged(Core.Connection.ConnectionStatus status) => NotifyPropertyChanged(nameof(HeaderText));

        private void HandleSignalProblemChanged() => NotifyPropertyChanged(nameof(HeaderText));

        // 置いたものを確かめると「置いた」の印が消えるので、ミックスの並び・板の一覧・下見の行の「導入済み」を付け直す
        // （下見の行を付け直さないと、取った曲に ↓ が戻った）
        private void HandleDownloadsChanged()
        {
            var shown = (_mix?.All ?? Enumerable.Empty<RecommendationItem>()).Concat(_items).Concat(_draft?.Seen ?? Enumerable.Empty<RecommendationItem>());
            foreach (var item in shown.Distinct())
            {
                item.Installed = LevelLookup.IsInstalled(item.Hash);
            }
            FillList();
            FillDraftList();
            Notify();
            NotifyDraft();
        }

        private void ReadSeedAndNotify()
        {
            try
            {
                ReadSeed();
                if (_mix != null && _selected != null)
                {
                    _offMix = _seed != null && _seed.Hash != _selected.Hash;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"could not read the selected chart: {ex.Message}");
                _seed = null;
            }
            NotifyPropertyChanged(nameof(HeaderText));
        }

        private void ReadSeed()
        {
            _seed = null;
            var level = _levelDetail != null ? _levelDetail.beatmapLevel : null;
            if (level == null)
            {
                return;
            }
            var hash = Core.Maps.MapHash.Normalize(SongCore.Collections.GetCustomLevelHash(level.levelID));
            if (hash == null)
            {
                return;
            }
            var key = _levelDetail.beatmapKey;
            if (key.beatmapCharacteristic == null)
            {
                return;
            }
            _seed = new ChartRef { Hash = hash, Characteristic = key.beatmapCharacteristic.serializedName, Difficulty = key.difficulty.ToString() };
            _seedLabel = $"{Core.UI.DisplayText.Line(level.songName)}（{Core.Maps.DifficultyLabel.Chart(_seed.Characteristic, _seed.Difficulty)}）";
        }

        // ミックス: 種類（OctantCompass のプリセット）を小窓で選ぶ。起点は今選んでいる譜面
        private void MixClick()
        {
            if (ToastIfUnavailable())
            {
                return;
            }
            ReadSeed();
            var presets = ShownPresets();
            if (presets.Count == 0)
            {
                _message = _connection.PresetsProblem ?? Lang.T("presets.none");
                Notify();
                return;
            }
            _menuPresets = presets;
            _choosing = null;
            _choiceInputs = null;
            // 起点が要る種類は、譜面を選んでいないときは押せない
            var groups = presets.Any(p => !string.IsNullOrEmpty(p.Group)) ? presets.Select(p => p.Group).ToList() : null;
            ShowTiles(presets.Select(p => (p.Label, PresetRules.NeedsSeed(p) && _seed == null ? Lang.T("choice.needsSeed") : p.Hint, !(PresetRules.NeedsSeed(p) && _seed == null))).ToList(), groups);
            NotifyPropertyChanged(nameof(ChoiceSeed));
            _parserParams?.EmitEvent("show-choice");
        }

        private List<Preset> ShownPresets() => PresetRules.Shown(_connection.Presets?.Presets, true);

        private void HandlePresetsChanged()
        {
            BuildHomeButtons();
            Notify();
        }

        // ホームのボタンを一覧から作り直す。並びは OctantCompass の返した順のまま。文字は縮めずに 2 行に分けて並べる
        private void BuildHomeButtons()
        {
            if (_homeRow == null || _homeRow2 == null)
            {
                return;
            }
            foreach (var row in new[] { _homeRow, _homeRow2 })
            {
                foreach (UnityEngine.Transform child in row.transform)
                {
                    UnityEngine.Object.Destroy(child.gameObject);
                }
            }
            var presets = PresetRules.Shown(_connection.Presets?.Presets, false);
            // 先頭は「ミックス」（選んでいる曲を起点にした種類を選ぶ）。ほかの推薦と同じカードにする
            var tiles = new List<(string Label, string Hint, Action Click, string Id)> { (Lang.T("mix.tile"), Lang.T("mix.tileHint"), (Action)MixClick, "mix") };
            tiles.AddRange(presets.Select(p => (p.Label, p.Hint, (Action)(() => HomeClick(p)), p.Id)));
            var layout = Core.UI.HomeTiles.Arrange(tiles.Select(t => t.Label).ToList());
            for (var i = 0; i < tiles.Count; i++)
            {
                var tile = tiles[i];
                try
                {
                    Tile.Create(layout.RowOf[i] == 0 ? _homeRow : _homeRow2, tile.Label, layout.Widths[i], Core.UI.HomeTiles.Height, tile.Click, fontSize: layout.FontSize, hint: tile.Hint, backing: HomeTileBacking);
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error($"[home] could not make the tile for {tile.Id}: {ex}");
                }
            }
            Plugin.Log.Info($"[home] {presets.Count} tiles: {string.Join(", ", presets.Select(p => p.Id))} (scale {layout.Scale:0.##})");
        }

        // ホームのボタン: 起点なしで、中央の小窓に下見を出す（choice の入力があれば先に選ばせる）
        private void HomeClick(Preset preset)
        {
            if (_busy || ToastIfUnavailable())
            {
                return;
            }
            _menuPresets = null;
            if (PresetRules.Choices(preset).Count > 0)
            {
                _parserParams?.EmitEvent("show-choice");
            }
            PresetChosen(preset, false);
        }

        // 種類を選んだ: 起点は今選んでいる譜面（起点を使わない種類でも、あれば覚えておく）。choice の入力があれば続けて選ばせる
        private void PresetChosen(Preset preset) => PresetChosen(preset, true);

        private void PresetChosen(Preset preset, bool fromSong)
        {
            if (_busy)
            {
                return;
            }
            if (!fromSong)
            {
                // ホームから: 曲を起点にしない
                _mixSeed = null;
                _mixLabel = null;
            }
            if (fromSong && PresetRules.NeedsSeed(preset) && _seed == null)
            {
                _parserParams?.EmitEvent("hide-choice");
                _message = Lang.T("choice.needsSeedFirst");
                Notify();
                return;
            }
            if (fromSong && _seed != null)
            {
                _mixSeed = _seed;
                _mixLabel = _seedLabel;
            }
            var choices = PresetRules.Choices(preset);
            _draftFromSong = fromSong;
            _mixUsesSeed = PresetRules.UsesSeed(preset);
            _draftingPreset = preset;
            if (choices.Count == 0)
            {
                ShowDraftModal();
                BuildMix(preset, null, null);
                return;
            }
            _choosing = preset;
            _choiceInputs = choices;
            _chosen = new Dictionary<string, string>();
            _chosenLabels = new List<string>();
            ShowNextChoice();
        }

        private void ShowNextChoice()
        {
            var input = _choiceInputs[_chosen.Count];
            ShowTiles(input.Options.Select(o => (o.Label ?? o.Value, (string)null, true)).ToList());
            NotifyPropertyChanged(nameof(ChoiceSeed));
        }

        private void ChoiceSelect(TableView tableView, int index)
        {
            if (_menuPresets != null)
            {
                var presets = _menuPresets;
                _menuPresets = null;
                if (index >= 0 && index < presets.Count)
                {
                    PresetChosen(presets[index]);
                }
                return;
            }
            if (_choosing == null || _choiceInputs == null || _chosen == null || _chosen.Count >= _choiceInputs.Count)
            {
                return;
            }
            var input = _choiceInputs[_chosen.Count];
            if (index < 0 || index >= input.Options.Count)
            {
                return;
            }
            _chosen[input.Name] = input.Options[index].Value;
            _chosenLabels.Add(input.Options[index].Label ?? input.Options[index].Value);
            if (_chosen.Count < _choiceInputs.Count)
            {
                ShowNextChoice();
                return;
            }
            ShowDraftModal();
            var preset = _choosing;
            _choosing = null;
            BuildMix(preset, _chosen, string.Join("・", _chosenLabels));
        }

        // 並びの見せ方: 全部 → おなじみ → 新しい曲（並べ替えない）
        [UIAction("filter-click")]
        private void FilterClick()
        {
            if (_mix == null)
            {
                return;
            }
            _mix.Filter = _mix.Filter == Core.Mix.MixFilter.All ? Core.Mix.MixFilter.Familiar
                : _mix.Filter == Core.Mix.MixFilter.Familiar ? Core.Mix.MixFilter.New
                : Core.Mix.MixFilter.All;
            _items = _mix.Visible();
            FillList();
            Notify();
        }

        private async void BuildMix(Preset preset, Dictionary<string, string> choices, string choiceLabel)
        {
            if (_busy || (PresetRules.NeedsSeed(preset) && _mixSeed == null))
            {
                return;
            }
            _draft = null;
            _draftSelected = null;
            _draftMessage = "";
            FillDraftList();
            SetBusy(true, Lang.T("mix.making"));
            try
            {
                var draftCount = Core.Mix.MixDraftRules.Size(_settingsHolder.Current.MixDraftCount);
                var request = Core.Mix.MixDraftRules.FirstRequest(preset, _mixSeed, choices, draftCount);
                var result = await _recommendations.RequestAsync(request, _lifetime.Token);
                if (result.Error != null)
                {
                    // 失敗したら今の並びを残し、下見の小窓は閉じて知らせだけを出す
                    _message = result.Error;
                    _draftMessage = result.Error;
                    HideModals();
                    Toast(result.Error);
                    Plugin.Log.Info($"[mix] {preset.Id} failed: {result.Error}");
                    return;
                }
                _draftPreset = preset;
                _draftChoiceLabel = choiceLabel;
                var displayable = RecommendationDisplay.Shown(result.Items);
                if (displayable.Count < result.Items.Count)
                {
                    Plugin.Log.Info($"[mix] left out {result.Items.Count - displayable.Count} candidates BeatSaver does not know");
                }
                _draft = new Core.Mix.MixDraft<RecommendationItem>(displayable, draftCount);
                _draftSeed = request.Seed;
                _draftChoices = choices != null ? new Dictionary<string, string>(choices) : null;
                _draftSelected = null;
                _draftMessage = Lang.T(_draft.Picked.Count == 0 ? "recommend.none" : "draft.hint");
                Plugin.Log.Info($"[mix] {preset.Id} requestId={result.Response?.RequestId} seed={request.Seed?.Hash} {request.Seed?.Characteristic} {request.Seed?.Difficulty} choices={(choices == null ? "" : string.Join(",", choices.Select(c => c.Key + "=" + c.Value)))} candidates={_draft.Picked.Count} error={result.Error}");
                FillDraftList();
                foreach (var shown in _draft.Picked)
                {
                    _signals.RecordRecommendation(shown.RequestId, shown.Preset, shown.Hash, shown.Candidate.Characteristic, shown.Candidate.Difficulty, "shown");
                }
                _ = LoadDraftCoversAsync();
                // 1 曲目の試聴をすぐ流し、取り終えたら残りの曲の試聴を先読みする
                if (_draft.Picked.Count > 0)
                {
                    DraftSelect(null, 0);
                }
                PrefetchDraftPreviews();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex);
                _message = Lang.T("mix.failed", new { error = ex.Message });
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        [UIAction("select-cell")]
        private void SelectCell(TableView tableView, int index)
        {
            var item = index >= 0 && index < _items.Count ? _items[index] : null;
            if (item == null)
            {
                return;
            }
            // 選んだだけでは取らない。入っている曲（前もって置いたものを含む）は曲選択で選び、ほかは試聴だけを流す
            _selected = item;
            _mix?.Select(item);
            _offMix = false;
            if (item.Installed || _downloads.IsPlaced(item.Hash) || _downloads.IsInFlight(item.Hash))
            {
                _ = PlayItemAsync(item);
                return;
            }
            if (item.Version?.PreviewUrl != null)
            {
                _preview.Play(item.Version.PreviewUrl);
            }
            _message = Lang.T(_downloads.IsAvailable(item) ? "item.previewing" : "item.unavailable");
            FillList();
            Notify();
        }

        [UIAction("play-click")]
        private void PlayClick()
        {
            if (_selected != null)
            {
                _ = PlayItemAsync(_selected);
            }
        }

        [UIValue("play-interactable")]
        public bool PlayInteractable => !_busy && _selected != null && !_selected.Installed && !_downloads.IsPlaced(_selected.Hash) && !_downloads.IsInFlight(_selected.Hash) && _downloads.IsAvailable(_selected);

        // 下見の行の状態の文
        private string DraftState(RecommendationItem i) => i.Installed ? "  " + Lang.T("item.installed") : _downloads.IsInFlight(i.Hash) ? "  " + Lang.T("item.downloading") : _downloads.IsPlaced(i.Hash) ? "  " + Lang.T("item.downloaded") : _downloads.IsAvailable(i) ? "" : "  " + Lang.T("item.cannotDownload");

        // 下見の 1 曲だけを先に取得する。ユーザーが選んだ取得なので「手動」の枠で台帳に残す。
        // 曲選択の読み直しはしない（作成するときか、次に読み直すときにまとめる）
        private async void DownloadOne(RecommendationItem item)
        {
            if (item == null || _busy || item.Installed || _downloads.IsInFlight(item.Hash))
            {
                return;
            }
            _draftMessage = Lang.T("item.downloadingTitle", new { title = item.Title });
            NotifyDraft();
            FillDraftList();
            try
            {
                var outcomes = await _downloads.InstallManyAsync(new[] { item }, Core.Maps.FrameEntry.Manual, false, _lifetime.Token);
                var outcome = outcomes?.FirstOrDefault();
                _draftMessage = outcome == null ? Lang.T("download.notWhilePlaying") : outcome.Error != null ? Lang.T("download.failed", new { error = outcome.Error }) : Lang.T("item.downloadedTitle", new { title = item.Title });
                if (outcome != null && outcome.Error == null)
                {
                    _signals.RecordRecommendation(item.RequestId, item.Preset, item.Hash, item.Candidate.Characteristic, item.Candidate.Difficulty, "selected");
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex);
                _draftMessage = Lang.T("download.failed", new { error = ex.Message });
            }
            FillDraftList();
            NotifyDraft();
        }

        // 下見の行の ↓: 入っておらず、置いてもおらず、取っている途中でもなく、今の取得元から取れる
        private bool CanDownloadDraft(RecommendationItem i) => !_busy && !i.Installed && !_downloads.IsPlaced(i.Hash) && !_downloads.IsInFlight(i.Hash) && _downloads.IsAvailable(i);

        // 下見の行の ▶（↓ と同じ場所）: 入っているか、置いてある（押したら読み直して選ぶ）
        private bool CanPlayDraft(RecommendationItem i) => !_busy && !_downloads.IsInFlight(i.Hash) && (i.Installed || _downloads.IsPlaced(i.Hash));

        // 下見の曲の試聴を、並んだ順に先読みする（選んだ曲を取り終えてから 1 曲ずつ）。下見が閉じるか変わったらやめる
        private void PrefetchDraftPreviews()
        {
            var draft = _draft;
            if (draft == null)
            {
                return;
            }
            var urls = draft.Picked.Select(i => i.Version?.PreviewUrl).Where(u => u != null).ToList();
            _preview.Prefetch(urls, () => draft == _draft && _draftModal != null && _draftModal.isShown);
        }

        // 下見の 1 曲だけを遊ぶ: 置いてあれば読み直してから、曲選択でその曲を選ぶ（PLAY はユーザーが押す）。
        // 下見の小窓は閉じるが下見は残し、その曲を遊んで曲選択に戻ったら小窓を開き直す（TickAdvance）
        private async void PlayDraftItem(RecommendationItem item)
        {
            if (item == null || _busy || _draft == null || _downloads.IsInFlight(item.Hash))
            {
                return;
            }
            var draft = _draft;
            if (!item.Installed)
            {
                SetBusy(true, null);
                _draftMessage = Lang.T("item.loadingTitle", new { title = item.Title });
                NotifyDraft();
                try
                {
                    // 置いてあるので取らない。読み直して、譜面が読めたかを確かめる
                    var outcomes = await _downloads.InstallManyAsync(new[] { item }, Core.Maps.FrameEntry.Manual, true, _lifetime.Token);
                    var outcome = outcomes?.FirstOrDefault();
                    if (outcome == null || outcome.Error != null || !item.Installed)
                    {
                        _draftMessage = outcome == null ? Lang.T("download.notWhilePlaying") : Lang.T("download.failed", new { error = outcome.Error ?? Lang.T("download.notLoaded") });
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error(ex);
                    _draftMessage = Lang.T("download.failed", new { error = ex.Message });
                    return;
                }
                finally
                {
                    SetBusy(false, null);
                    NotifyDraft();
                }
                if (draft != _draft || _draftModal == null || !_draftModal.isShown)
                {
                    // 読み直している間に下見を閉じたか作り直した
                    return;
                }
            }
            _preview.Stop();
            _preview.StopPrefetch();
            HideModals();
            _draftResumeHash = item.Hash;
            _draftResumeArmed = false;
            var selected = SongSelect.Select(item.Hash, item.Candidate.Characteristic, item.Candidate.Difficulty, null);
            SongSelect.RedrawCovers(new[] { item.Hash });
            _message = Lang.T(selected ? "select.done" : "select.failed");
            Plugin.Log.Info($"[mix] playing {item.Hash} from the draft (selected={selected})");
            Notify();
        }

        // 下見から 1 曲だけ遊んだ曲。その曲を始めたら Armed にし、遊び終えて曲選択に戻ったら下見の小窓を開き直す
        private string _draftResumeHash;
        private bool _draftResumeArmed;
        private bool _draftResumePending;

        // 下見の曲を外すだけ（入れ替えない）。下見の曲数が 1 つ減る
        private void DiscardItem(RecommendationItem removed)
        {
            if (removed == null || _draft == null || _busy || !_draft.Remove(removed))
            {
                return;
            }
            _signals.RecordRecommendation(removed.RequestId, removed.Preset, removed.Hash, removed.Candidate.Characteristic, removed.Candidate.Difficulty, "skipped");
            if (removed == _draftSelected)
            {
                _draftSelected = null;
                _preview.Stop();
            }
            _draftMessage = "";
            FillDraftList();
            NotifyDraft();
        }

        // 入れ替え用の次の曲を、下見の一番下に足す（下見の曲数が 1 つ増える）。足した曲を選び、試聴を流す。
        // 入れ替え用が尽きていれば、追加を受け取る（届いたら、もう一度押すと足せる）
        private void AddItem()
        {
            if (_draft == null || _busy)
            {
                FillDraftList();
                return;
            }
            var added = _draft.Add();
            if (added == null)
            {
                _draftMessage = Lang.T(_draft.Full ? "draft.full" : "draft.fetchingSwaps");
                FillDraftList();
                NotifyDraft();
                if (!_draft.Full)
                {
                    _ = TopUpDraftAsync();
                }
                return;
            }
            _signals.RecordRecommendation(added.RequestId, added.Preset, added.Hash, added.Candidate.Characteristic, added.Candidate.Difficulty, "shown");
            _draftSelected = added;
            _draftMessage = "";
            if (added.Version?.PreviewUrl != null)
            {
                _preview.Play(added.Version.PreviewUrl);
            }
            FillDraftList();
            // 足した行と「曲を足す」の行が見えるように、一覧の一番下まで送る
            _draftList?.TableView.ScrollToCellWithIdx(_draftCells.Count - 1, TableView.ScrollPositionType.End, false);
            NotifyDraft();
            _ = LoadDraftCoversAsync();
            if (_draft.RunningLow)
            {
                _ = TopUpDraftAsync();
            }
        }

        private bool _toppingUp;
        private ChartRef _draftSeed;
        private Dictionary<string, string> _draftChoices;

        // 同じ種類・起点・選択肢で、今まで出したものを除いて追加を受け取り、入れ替え用に足す
        private async System.Threading.Tasks.Task TopUpDraftAsync()
        {
            var draft = _draft;
            if (_toppingUp || draft == null || _draftPreset == null)
            {
                return;
            }
            _toppingUp = true;
            try
            {
                var request = Core.Mix.MixDraftRules.TopUpRequest(_draftPreset, _draftSeed, _draftChoices, Core.Mix.MixDraftRules.Size(_settingsHolder.Current.MixDraftCount), draft.Seen.Select(i => i.Hash));
                var result = await _recommendations.RequestAsync(request, _lifetime.Token);
                if (draft != _draft)
                {
                    return;
                }
                var added = result.Error == null ? draft.AddReserve(RecommendationDisplay.Shown(result.Items)) : 0;
                Plugin.Log.Info($"[mix] topped up the draft reserve with {added} (error={result.Error})");
                if (_draftMessage == Lang.T("draft.fetchingSwaps"))
                {
                    _draftMessage = added > 0 ? "" : Lang.T("draft.noMoreSwaps");
                }
                NotifyDraft();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[mix] could not top up the draft: {ex.Message}");
            }
            finally
            {
                _toppingUp = false;
            }
        }

        // 下見の曲を外し、入れ替え用の次の曲をその場所に入れる
        private void ReplaceItem(RecommendationItem removed)
        {
            if (removed == null || _draft == null || _busy)
            {
                return;
            }
            var wasSelected = removed == _draftSelected;
            var next = _draft.Replace(removed);
            _signals.RecordRecommendation(removed.RequestId, removed.Preset, removed.Hash, removed.Candidate.Characteristic, removed.Candidate.Difficulty, "skipped");
            if (next != null)
            {
                _signals.RecordRecommendation(next.RequestId, next.Preset, next.Hash, next.Candidate.Characteristic, next.Candidate.Difficulty, "shown");
            }
            // 入れ替えた曲を選んだ状態にして、その試聴をすぐ流す
            _draftSelected = next;
            _draftMessage = next != null ? "" : Lang.T("draft.fetchingSwaps");
            if (next?.Version?.PreviewUrl != null)
            {
                _preview.Play(next.Version.PreviewUrl);
            }
            else if (wasSelected)
            {
                _preview.Stop();
            }
            FillDraftList();
            NotifyDraft();
            if (next != null)
            {
                _ = LoadDraftCoversAsync();
            }
            // 入れ替え用が少なくなったら、裏で追加を受け取る
            if (_draft.RunningLow)
            {
                _ = TopUpDraftAsync();
            }
        }

        // 下見を確定する: 並んでいる曲だけを取得し、一時的なプレイリストにして曲選択で開く
        private async System.Threading.Tasks.Task CreateMixAsync()
        {
            var picked = _draft.Picked.ToList();
            var targets = picked.Where(i => !i.Installed && _downloads.IsAvailable(i)).ToList();
            // 小窓は開いたまま進み具合を見せ、曲選択を開き直す直前にアニメーションなしで閉じる（アニメーションの途中で開き直すと前の画面の上に残った）
            _preview.Stop();
            _creating = true;
            StartCreateProgress(targets.Count);
            SetProgress(0, targets.Count, targets.Count > 0 ? Lang.T("create.downloading", new { done = 0, total = targets.Count }) : Lang.T("create.makingPlaylist"));
            SetBusy(true, _draftMessage);
            // 受け取った割合が何回届いたか（帯が伸びないと言われたときに、届いていないのかを見分ける）
            var reports = 0;
            try
            {
                _preview.Stop();
                if (targets.Count > 0)
                {
                    var outcomes = await _downloads.InstallManyAsync(targets, Core.Maps.FrameEntry.Mix, true, _lifetime.Token,
                        (done, total) => SetProgress(done, total, done < total ? Lang.T("create.downloading", new { done, total }) : Lang.T("create.loadingSongs")),
                        (item, fraction) =>
                        {
                            System.Threading.Interlocked.Increment(ref reports);
                            _createReceived[item.Hash] = fraction;
                        });
                    FinishCreateDownloads();
                    if (outcomes == null)
                    {
                        _message = _draftMessage = Lang.T("download.notWhilePlaying");
                        return;
                    }
                    foreach (var item in picked)
                    {
                        item.Installed = LevelLookup.IsInstalled(item.Hash);
                    }
                    Plugin.Log.Info($"[mix] created: downloaded {outcomes.Count(o => o.Error == null)} of {targets.Count} in {UnityEngine.Time.unscaledTime - _createStartedAt:0.0} s with {reports} progress reports");
                }
                var ready = picked.Where(i => i.Installed).ToList();
                if (ready.Count == 0)
                {
                    _message = _draftMessage = Lang.T("create.nothingDownloaded");
                    return;
                }
                _mixPreset = _draftPreset;
                _mixChoiceLabel = _draftChoiceLabel;
                _draft = null;
                _mix = new Core.Mix.MixQueue<RecommendationItem>(ready, i => i.Installed);
                _items = _mix.Visible();
                _selected = ready[0];
                _mix.Select(_selected);
                _offMix = false;
                HideModals();
                var pack = _playlist.WriteCurrentMix(MixName(), ready.Select(i => (i.Hash, i.Title, i.Map?.Key, i.Mapper)).ToList());
                _mixPack = pack;
                _mixPlaylistStale = false;
                var opened = pack != null && SongSelect.OpenPack(pack, _selected.Hash, _selected.Candidate.Characteristic, _selected.Candidate.Difficulty);
                var missing = picked.Count - ready.Count;
                _message = Lang.T(opened ? "create.opened" : "create.made")
                    + (missing > 0 ? Lang.T("create.missing", new { missing }) : "");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex);
                _message = _draftMessage = Lang.T("create.failed", new { error = ex.Message });
            }
            finally
            {
                _creating = false;
                ShowCreateTrack();
                SetProgress(0, 0, _draftMessage);
                FillList();
                SetBusy(false, null);
            }
        }

        // 並びの曲にする: 入っていなければ取ってから、曲選択でその曲を選ぶ。そのあと先の曲を前もって取る
        private async System.Threading.Tasks.Task PlayItemAsync(RecommendationItem item)
        {
            _selected = item;
            _mix?.Select(item);
            _offMix = false;
            Notify();
            if (!item.Installed)
            {
                _message = _downloads.IsPlaced(item.Hash) ? Lang.T("item.loadingTitle", new { title = item.Title }) : Lang.T("item.downloadingTitle", new { title = item.Title });
                FillList();
                Notify();
                // 取得中なら、その取得が終わるのを待ってから、今すぐ使うので読み直して確かめる
                var outcomes = await _downloads.InstallManyAsync(new[] { item }, Core.Maps.FrameEntry.Mix, true, _lifetime.Token);
                var outcome = outcomes?.FirstOrDefault();
                if (outcome == null || outcome.Error != null || !item.Installed)
                {
                    _message = outcome == null ? Lang.T("download.notWhilePlaying") : Lang.T("download.failed", new { error = outcome.Error ?? Lang.T("download.notLoaded") });
                    FillList();
                    Notify();
                    return;
                }
                if (_selected != item)
                {
                    // 待っている間に別の曲を選んだ
                    return;
                }
            }
            if (_mix != null && _mixPlaylistStale)
            {
                // 結果画面で選んだ曲を、ミックスのプレイリストにも入れる（入っていないと一覧で選べず、開き直しになる）
                _mixPack = _playlist.WriteCurrentMix(MixName(), _mix.All.Select(i => (i.Hash, i.Title, i.Map?.Key, i.Mapper)).ToList()) ?? _mixPack;
                _mixPlaylistStale = false;
            }
            _message = SongSelect.Select(item.Hash, item.Candidate.Characteristic, item.Candidate.Difficulty, _mix != null ? _mixPack : null) ? Lang.T("select.done") : Lang.T("select.failed");
            SongSelect.RedrawCovers(new[] { item.Hash });
            FillList();
            Notify();
            _ = PrefetchAsync();
        }

        // 前もって取ると SongCore が曲の一覧を作り直し、曲選択の選択が外れる。そのときは今の曲を選び直す
        private async void HandleLevelPacksRefreshed()
        {
            // 曲選択の一覧が作り直されるのを待ってから選ぶ
            await System.Threading.Tasks.Task.Delay(500);
            var item = _selected;
            if (item == null || !item.Installed || _levelSelection == null || !_levelSelection.isActivated)
            {
                return;
            }
            // 選択が外れたときだけ選び直す。ユーザーが別の曲を選んでいたら、それを上書きしない
            var current = _levelSelection.beatmapLevel;
            if (current != null)
            {
                return;
            }
            Plugin.Log.Info($"[mix] reselecting {item.Hash} after the song list was rebuilt");
            SongSelect.SelectInCurrentList(item.Hash);
            SongSelect.RedrawCovers(new[] { item.Hash });
        }

        // 今の曲から先の、まだ入っていない曲を前もって取る
        private async System.Threading.Tasks.Task PrefetchAsync()
        {
            if (_mix == null || !_settingsHolder.Current.AutoDownload)
            {
                return;
            }
            var targets = _mix.ToPrefetch(_settingsHolder.Current.MixPrefetchCount).Where(_downloads.IsAvailable).ToList();
            if (targets.Count == 0)
            {
                return;
            }
            // 置くところまで。読み直しは、遊び終えて戻ったときか、その曲を使うときにまとめて行う（画面を揺らさない）
            var outcomes = await _downloads.InstallManyAsync(targets, Core.Maps.FrameEntry.Mix, false, _lifetime.Token);
            if (outcomes != null)
            {
                Plugin.Log.Info($"[mix] prefetched {outcomes.Count(o => o.Error == null)} of {targets.Count} (placed, not loaded yet)");
                FillList();
            }
        }

        // 遊び終わって曲選択に戻ったら、並びの次の曲へ進む
        private void HandleTransitionDidFinish(GameScenesManager.SceneTransitionType type, ScenesTransitionSetupDataSO setupData, DiContainer container)
        {
            if (setupData is StandardLevelScenesTransitionSetupDataSO standard)
            {
                _lastPlayedHash = Core.Maps.LevelIds.Hash(standard.beatmapKey.levelId);
                _lastPlayedChart = _lastPlayedHash == null || standard.beatmapKey.beatmapCharacteristic == null ? null
                    : new ChartRef { Hash = _lastPlayedHash, Characteristic = standard.beatmapKey.beatmapCharacteristic.serializedName, Difficulty = standard.beatmapKey.difficulty.ToString() };
                // 下見から遊んだ曲を始めた。ほかの曲を始めたら、下見には戻らない
                _draftResumeArmed = _draftResumeHash != null && _draftResumeHash == _lastPlayedHash;
                if (!_draftResumeArmed)
                {
                    _draftResumeHash = null;
                }
                _draftResumePending = false;
                // 前の結果画面のおすすめは出さない
                _resultsPanel.Show("", new List<RecommendationItem>(), null);
                _advancePending = false;
                _pendingPick = null;
                return;
            }
            if (type == GameScenesManager.SceneTransitionType.Pop && _draftResumeArmed)
            {
                _draftResumeArmed = false;
                _draftResumePending = true;
            }
            if (type == GameScenesManager.SceneTransitionType.Pop && _lastPlayedChart != null)
            {
                var played = _lastPlayedChart;
                _lastPlayedChart = null;
                _ = LoadResultsAsync(played);
            }
            if (type != GameScenesManager.SceneTransitionType.Pop || _mix == null || _selected == null || _lastPlayedHash != _selected.Hash)
            {
                return;
            }
            _lastPlayedHash = null;
            // 自動で次へ進むのを切っているときは、結果画面で選んだ曲だけ選ぶ（ChooseFromResults）
            _advancePending = _settingsHolder.Current.MixAutoAdvance;
        }

        [UIValue("auto-next-hint")]
        public string AutoNextHint => Lang.T(_settingsHolder.Current.MixAutoAdvance ? "panel.autoNextOnHint" : "panel.autoNextOffHint");

        [UIComponent("auto-next-plate")]
        private HMUI.ImageView _autoNextPlate;

        [UIComponent("auto-next-label")]
        private ClickableText _autoNextLabel;

        [UIAction("auto-next-click")]
        private void AutoNextClick()
        {
            _settingsHolder.Save("mixAutoAdvance", !_settingsHolder.Current.MixAutoAdvance);
            ShowAutoNext();
        }

        // 札の色を今の設定に合わせる
        private void ShowAutoNext()
        {
            var on = _settingsHolder.Current.MixAutoAdvance;
            if (_autoNextPlate != null)
            {
                _autoNextPlate.color = on ? AutoNextOnColor : AutoNextOffColor;
            }
            if (_autoNextLabel != null)
            {
                _autoNextLabel.DefaultColor = on ? UnityEngine.Color.white : AutoNextOffText;
                _autoNextLabel.color = _autoNextLabel.DefaultColor;
            }
            NotifyPropertyChanged(nameof(AutoNextHint));
        }

        // 札の色: 入は OctantPath の青（下見の試聴中の行と同じ #4C9AFF）、切は固定のボタンの板と同じ淡い白と灰色の文字
        private static readonly UnityEngine.Color AutoNextOnColor = new UnityEngine.Color(0.298f, 0.604f, 1f, 1f);
        private static readonly UnityEngine.Color AutoNextOffColor = new UnityEngine.Color(1f, 1f, 1f, 0.08f);
        private static readonly UnityEngine.Color AutoNextOffText = new UnityEngine.Color(0.733f, 0.733f, 0.733f, 1f);
        private static readonly UnityEngine.Vector2 AutoNextPlateSize = new UnityEngine.Vector2(22f, 5.5f);

        // 下見から 1 曲だけ遊んで曲選択に戻った: 下見の小窓を開き直す。結果画面でおすすめを選んだ（次の曲へ進む）ときは開かない
        private void ResumeDraft()
        {
            _draftResumePending = false;
            if (_draft == null || _busy || _advancePending || _pendingPick != null || _placing != null)
            {
                return;
            }
            Plugin.Log.Info($"[mix] back from {_draftResumeHash}; reopening the draft");
            _draftSelected = null;
            _draftMessage = "";
            ShowDraftModal();
            FillDraftList();
            NotifyDraft();
        }

        // 曲選択が続けて出たら、次の曲へ進む
        private void TickAdvance()
        {
            var inSongList = _levelSelection != null && _levelSelection.isActivated && _levelSelection.isInViewControllerHierarchy;
            if (!inSongList)
            {
                _songListSince = -1f;
                return;
            }
            if (_songListSince < 0f)
            {
                _songListSince = UnityEngine.Time.unscaledTime;
            }
            if (_draftResumePending && UnityEngine.Time.unscaledTime - _songListSince >= AdvanceDelay && !_gameScenesManager.isInTransition)
            {
                ResumeDraft();
            }
            if (!_advancePending || _placing != null || UnityEngine.Time.unscaledTime - _songListSince < AdvanceDelay || _gameScenesManager.isInTransition)
            {
                return;
            }
            _advancePending = false;
            _ = AdvanceAsync();
        }

        // 結果画面のおすすめ: 遊んだ譜面を起点に数曲だけ受け取る（取得はしない）。種類は今のミックスの種類（起点を使うもの）か、一覧で最初の起点を使うもの
        private const int ResultsCount = 3;

        private async System.Threading.Tasks.Task LoadResultsAsync(ChartRef played)
        {
            try
            {
                var presets = ShownPresets();
                var preset = _mixPreset != null && PresetRules.UsesSeed(_mixPreset) && PresetRules.Choices(_mixPreset).Count == 0 ? _mixPreset
                    : presets.FirstOrDefault(p => PresetRules.UsesSeed(p) && PresetRules.Choices(p).Count == 0);
                if (preset == null)
                {
                    return;
                }
                var request = PresetRules.Request(preset, played, null, ResultsCount);
                var exclude = new HashSet<string> { played.Hash };
                if (_mix != null)
                {
                    exclude.UnionWith(_mix.All.Select(i => i.Hash).Where(h => h != null));
                }
                request.Exclude = exclude.Select(h => new ExcludeEntry { Hash = h, Reason = "in_mix" }).ToList();
                var result = await _recommendations.RequestAsync(request, _lifetime.Token);
                var items = result.Error == null ? RecommendationDisplay.Shown(result.Items) : new List<RecommendationItem>();
                if (items.Count == 0)
                {
                    Plugin.Log.Info($"[results] no recommendations: {result.Error ?? (result.Items.Count == 0 ? "empty" : $"BeatSaver does not know all {result.Items.Count}")}");
                    _resultsPanel.Show("", new List<RecommendationItem>(), null);
                    return;
                }
                foreach (var item in items)
                {
                    _signals.RecordRecommendation(item.RequestId, item.Preset, item.Hash, item.Candidate.Characteristic, item.Candidate.Difficulty, "shown");
                }
                var next = _mix?.Next();
                _resultsPanel.Show(Lang.T("next.title", new { preset = preset.Label }), items, next != null ? Lang.T("next.hintWithNext", new { next = next.Title }) : Lang.T("next.hint"));
                Plugin.Log.Info($"[results] {preset.Id} requestId={result.Response?.RequestId} seed={played.Hash} candidates={items.Count} leftOut={result.Items.Count - items.Count}");
                // 押したらすぐ聞けるように、並んだ曲の試聴を先読みする（次の曲を遊び始めて板のおすすめが替わったらやめる）
                _preview.Prefetch(items.Select(i => i.Version?.PreviewUrl).Where(u => u != null).ToList(), () => _resultsPanel.Holds(items));
                await LoadCoversAsync(items, () => true, _resultsPanel.Refresh);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[results] {ex}");
            }
        }

        // 結果画面でおすすめの行の ▶ を押した: 並びの今の曲のすぐ後ろに入れて、次の曲にする（行を押しただけなら試聴だけ。ResultsPanel）。
        // 入っていれば、そのまま Continue を押す。入っていなければ置くところまで取り（進み具合は板の帯に出す）、置けたら Continue を押す。
        // 読み直しと曲選択での選択は、曲選択に戻ってから行う（TickAdvance）。取っている間に選び直しても受け付けない
        private void ChooseFromResults(RecommendationItem item)
        {
            if (_placing != null)
            {
                return;
            }
            _signals.RecordRecommendation(item.RequestId, item.Preset, item.Hash, item.Candidate.Characteristic, item.Candidate.Difficulty, "selected");
            if (_mix != null)
            {
                _mix.InsertNext(item);
                _items = _mix.Visible();
                _mixPlaylistStale = true;
            }
            _pendingPick = item;
            _advancePending = true;
            _resultsPanel.MarkChosen(item);
            var download = !item.Installed && _downloads.IsAvailable(item);
            _resultsPanel.SetDetail(Lang.T(download ? "next.setDownloading" : "next.set", new { title = item.Title }));
            if (download)
            {
                _ = PlaceAsync(item);
            }
            else
            {
                _resultsPanel.PressContinue();
            }
        }

        // 結果画面で選んだ曲を取っているところ。取り終わるまで、曲選択に戻っても次へ進まない
        private RecommendationItem _placing;

        private async System.Threading.Tasks.Task PlaceAsync(RecommendationItem item)
        {
            _placing = item;
            _resultsPanel.SetProgress(0);
            string error = null;
            // 受け取った割合が何回届いたか（帯が伸びないと言われたときに、届いていないのかを見分ける）
            var reports = 0;
            var started = UnityEngine.Time.unscaledTime;
            try
            {
                var outcomes = await _downloads.InstallManyAsync(new[] { item }, Core.Maps.FrameEntry.Mix, false, _lifetime.Token, received: (_, fraction) =>
                {
                    System.Threading.Interlocked.Increment(ref reports);
                    _resultsPanel.SetProgress(fraction);
                });
                var outcome = outcomes?.FirstOrDefault();
                error = outcome == null ? Lang.T("download.notWhilePlaying") : outcome.Path == null && !outcome.AlreadyThere ? outcome.Error ?? "" : null;
                FillList();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            finally
            {
                _placing = null;
                _resultsPanel.SetProgress(null);
            }
            Plugin.Log.Info($"[mix] placing {item.Hash} took {UnityEngine.Time.unscaledTime - started:0.0} s with {reports} progress reports");
            if (error != null)
            {
                _resultsPanel.HideProgress();
            }
            // 取っている間に遊び直した（Restart）なら、選んだ曲は捨てられている。新しい結果画面の Continue を押さない
            if (_pendingPick != item)
            {
                Plugin.Log.Info($"[mix] {item.Hash} was placed but is no longer the pick");
                return;
            }
            if (error != null)
            {
                Plugin.Log.Warn($"[mix] could not place {item.Hash}: {error}");
                _resultsPanel.SetDetail(Lang.T("download.failed", new { error }));
                return;
            }
            _resultsPanel.PressContinue();
        }

        private async System.Threading.Tasks.Task AdvanceAsync()
        {
            var pick = _pendingPick;
            _pendingPick = null;
            if (_mix == null && pick == null)
            {
                return;
            }
            // 曲選択に戻ったところなので、前もって置いた曲をここでまとめて読み直す
            if (_downloads.HasPlaced)
            {
                await _downloads.RefreshAsync(_lifetime.Token);
                foreach (var item in (_mix?.All ?? Enumerable.Empty<RecommendationItem>()).Append(pick).Where(i => i != null))
                {
                    item.Installed = LevelLookup.IsInstalled(item.Hash);
                }
            }
            var next = pick ?? _mix.Next();
            if (next != null)
            {
                Plugin.Log.Info($"[mix] next {next.Hash}");
                await PlayItemAsync(next);
            }
            else
            {
                FillList();
            }
        }

        // 並びをプレイリストに保存する。保存したプレイリストは OctantPath のお試しの表示ではないので、
        // その曲は自動削除から守られる（OctantPath 法 3.4 のプレイリストの扱い）
        [UIAction("save-click")]
        private void SaveClick()
        {
            if (_mix == null)
            {
                return;
            }
            _playlist.SaveMix(MixName(), _mix.All.Select(i => (i.Hash, i.Title, i.Map?.Key, i.Mapper)).ToList());
            _message = Lang.T("save.done");
            Notify();
        }

        private void FillList()
        {
            if (_list == null)
            {
                return;
            }
            _list.Data = _items.Select(i =>
            {
                var playing = i == _selected ? "▶ " : "";
                var state = i.Installed ? "" : _downloads.IsInFlight(i.Hash) ? "  " + Lang.T("item.downloading") : _downloads.IsPlaced(i.Hash) ? "  " + Lang.T("item.waitingToLoad") : _downloads.IsAvailable(i) ? "  " + Lang.T("item.notFetched") : "  " + Lang.T("item.cannotDownload");
                _covers.TryGet(i.Version?.CoverUrl, out var cover);
                return new CustomListTableData.CustomCellInfo($"{playing}{Core.UI.DisplayText.Line(i.Title)}", $"{Core.UI.DisplayText.Line(i.Artist)} [{Core.UI.DisplayText.Line(i.Mapper)}]  {i.Candidate.Difficulty}{state}", cover);
            }).ToList();
            _list.TableView.ReloadData();
            var index = _selected != null ? _items.IndexOf(_selected) : -1;
            if (index >= 0)
            {
                _list.TableView.SelectCellWithIdx(index);
                _list.TableView.ScrollToCellWithIdx(index, TableView.ScrollPositionType.Center, false);
            }
            else
            {
                _list.TableView.ClearSelection();
            }
        }

        private void SetBusy(bool busy, string message)
        {
            var changed = _busy != busy;
            _busy = busy;
            // 下見の行の ⇔ と ↓ は、取得や作成の途中は出さない。行は選んでもカバーが届いても作り直さないので、ここで作り直す
            if (changed && _draft != null)
            {
                FillDraftList();
            }
            if (message != null)
            {
                _message = message;
            }
            Notify();
        }

        private void Notify()
        {
            NotifyPropertyChanged(nameof(HeaderText));
            NotifyPropertyChanged(nameof(MixInteractable));
            NotifyPropertyChanged(nameof(PlayInteractable));
            NotifyPropertyChanged(nameof(DetailText));
            NotifyPropertyChanged(nameof(FilterText));
            NotifyDraft();
        }
    }

    // アルバムアートの左上に重ねる「ミックス」（♪）の BSML の受け手
    internal sealed class StartMixButton
    {
        private readonly Action _click;

        public StartMixButton(Action click)
        {
            _click = click;
        }

        [UIComponent("button")]
        public UnityEngine.UI.Button Button;

        [UIAction("click")]
        private void Click() => _click();
    }
}
