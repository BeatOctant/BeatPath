using System;
using System.Collections.Generic;
using System.Linq;
using OctantPath.Core.Recommendations;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components;
using HMUI;
using OctantPath.Core.Languages;

namespace OctantPath.UI
{
    // 結果画面の上に浮かぶ小さな板。遊んだ譜面を起点にしたおすすめを数曲だけ出す。行を押すと試聴だけを流し、行の ▶ でそれを次の曲にする
    // （押しただけで曲選択へ移ると、聞いてから決められなかった。2026-10-24）。
    // 結果画面が出ている間だけ出す。行は下見の小窓と同じ（DraftCell。外す・入れ替え・取得は出さない）。
    // 選んだ曲を取っている間は、下見の「作成」と同じ帯で進み具合を出す。
    // Continue を押すところだけはゲームの公開のメソッドでは届かず、結果画面の非公開のボタン（_continueButton）を reflection で押す（PressContinue）。
    // 見つからなければ押すのは人に任せるので、ゲームの版が変わっても壊れはしない
    internal sealed class ResultsPanel : NotifiableBase
    {
        private static readonly UnityEngine.Vector2 PanelSize = new UnityEngine.Vector2(100f, 41f);

        private static readonly string Bsml = @"<bg>
  <vertical child-control-height='true' child-expand-height='false' child-align='UpperCenter' spacing='0' pad='2'>
    <text text='~title-text' font-size='3' align='Center' word-wrapping='true' preferred-width='96' preferred-height='7'/>
    <text text='~notice-text' active='~notice-visible' font-size='3' color='#BBBBBB' align='Center' word-wrapping='true' preferred-width='96' preferred-height='" + Core.UI.Popup.F(3 * Core.UI.Popups.DraftCellSize) + @"'/>
    <custom-list id='list' active='~list-visible' contents='cells' select-cell='select-cell' visible-cells='3' cell-size='" + Core.UI.Popup.F(Core.UI.Popups.DraftCellSize) + "' clickable-cells='true' list-width='96' list-height='" + Core.UI.Popup.F(3 * Core.UI.Popups.DraftCellSize) + "' preferred-width='96' preferred-height='" + Core.UI.Popup.F(3 * Core.UI.Popups.DraftCellSize) + @"'>
      " + DraftCell.Template + @"
    </custom-list>
    <text text='~detail-text' font-size='2.4' align='Center' word-wrapping='true' preferred-width='96' preferred-height='4'/>
    <horizontal id='progress-track' bg='round-rect-panel' bg-color='" + Core.UI.Popups.ProgressTrackColor + @"' preferred-width='" + Core.UI.Popup.F(ProgressWidth) + @"' preferred-height='" + Core.UI.Popup.F(Core.UI.Popups.ProgressHeight) + @"' child-align='MiddleLeft' child-expand-width='false' child-control-width='true'>
      <horizontal id='progress-fill' bg='round-rect-panel' bg-color='" + Core.UI.Popups.ProgressFillColor + @"' preferred-width='0' preferred-height='" + Core.UI.Popup.F(Core.UI.Popups.ProgressHeight) + @"'/>
    </horizontal>
  </vertical>
</bg>";

        private const float ProgressWidth = 96f;

        private readonly Action<RecommendationItem> _chosen;
        private readonly Func<float> _scale;
        private readonly Func<double> _opacity;
        private readonly Func<PanelBase?> _base;
        private readonly Action<UnityEngine.Vector3, UnityEngine.Quaternion> _moved;
        private readonly Func<bool> _movable;
        private readonly CoverCache _covers;
        private readonly PreviewPlayer _preview;
        private BeatSaberMarkupLanguage.FloatingScreen.FloatingScreen _panel;
        private ResultsViewController _results;
        private float _nextSearch;
        private bool _failed;
        private List<RecommendationItem> _items = new List<RecommendationItem>();

        // 試聴中か、次の曲に選んだ行。下見の小窓の試聴中の行と同じく、左端の帯と薄い塗りで出す（押したことが分かるように。2026-10-18）
        private RecommendationItem _chosenItem;
        // この板で試聴を流したか（板を隠すときに止める）
        private bool _previewing;
        private string _title = "";
        private string _detail = "";
        // おすすめを出せないときの理由。null でなければ、一覧の場所にこれを出して板（枠）は残す（2026-10-25）
        private string _notice;

        [UIComponent("list")]
        private CustomCellListTableData _list;

        [UIValue("cells")]
        private List<object> _cells = new List<object>();

        [UIObject("progress-track")]
        private UnityEngine.GameObject _progressTrack;

        [UIObject("progress-fill")]
        private UnityEngine.GameObject _progressFill;

        // 取得の進み具合（0〜1）。取っていないときは負。取得の途中で別のスレッドから書かれるので、Tick で画面に移す
        private volatile float _progress = -1f;
        private volatile bool _hideProgress;
        // 見せている帯（伸び方は下見の試聴の行と同じ）。取るのにかかった時間の目安は、取り終えるたびに混ぜて更新する（最初の値は推測）
        private readonly SmoothProgress _smooth = new SmoothProgress();
        private float _typicalSeconds = 3f;
        private float _startedAt;
        private bool _trackShown = true;

        // scale は板の大きさ（推薦の板と同じ、設定の倍率をかけたもの）、opacity は背景の不透明度（設定の値）。
        // 置き場所は曲選択の板と同じ: baseOf はその板の下の縁（まだなければ null で、そのときは結果画面のすぐ上）。
        // movable が真の間（曲選択の板の固定を外している間）はつまみを出し、動かしたら moved に下の縁を渡す
        public ResultsPanel(CoverCache covers, PreviewPlayer preview, Action<RecommendationItem> chosen, Func<float> scale, Func<double> opacity, Func<PanelBase?> baseOf, Action<UnityEngine.Vector3, UnityEngine.Quaternion> moved, Func<bool> movable)
        {
            _covers = covers;
            _preview = preview;
            _chosen = chosen;
            _scale = scale;
            _opacity = opacity;
            _base = baseOf;
            _moved = moved;
            _movable = movable;
        }

        // いま出している（出そうとしている）おすすめが items か。次に遊び始めると空に替わる
        public bool Holds(List<RecommendationItem> items) => _items == items;

        [UIValue("title-text")]
        public string TitleText => _title;

        [UIValue("detail-text")]
        public string DetailText => _detail;

        [UIValue("notice-text")]
        public string NoticeText => _notice ?? "";

        [UIValue("notice-visible")]
        public bool NoticeVisible => _notice != null;

        [UIValue("list-visible")]
        public bool ListVisible => _notice == null;

        public void Show(string title, List<RecommendationItem> items, string detail)
        {
            SetNotice(null);
            _title = title;
            _items = items ?? new List<RecommendationItem>();
            _chosenItem = null;
            // 前の結果画面で埋めている途中だった帯は残さない
            _smooth.Stop();
            _detail = detail ?? "";
            Fill();
            NotifyPropertyChanged(nameof(TitleText));
            NotifyPropertyChanged(nameof(DetailText));
        }

        // おすすめを出せない譜面でも板は出し、一覧の場所に「出せない」と理由を出す（結果画面の間だけ。次に遊び始めると Show で消える）
        public void ShowUnavailable(string title, string reason)
        {
            Show(title, new List<RecommendationItem>(), "");
            SetNotice(Lang.T("next.unavailable") + (string.IsNullOrEmpty(reason) ? "" : $"\n<size=85%>{reason}</size>"));
        }

        private void SetNotice(string notice)
        {
            _notice = notice;
            NotifyPropertyChanged(nameof(NoticeText));
            NotifyPropertyChanged(nameof(NoticeVisible));
            NotifyPropertyChanged(nameof(ListVisible));
        }

        public void SetDetail(string detail)
        {
            _detail = detail ?? "";
            NotifyPropertyChanged(nameof(DetailText));
        }

        // カバーが届いたら、行の絵だけを差し替える（一覧は作り直さない）
        public void Refresh()
        {
            foreach (var cell in _cells.OfType<DraftCell>())
            {
                cell.RefreshCover();
            }
        }

        // 進み具合の帯を出す（0〜1）。null で隠す。どのスレッドから呼んでもよい
        public void SetProgress(double? fraction)
        {
            _progress = fraction.HasValue ? (float)Math.Max(0.0, Math.Min(1.0, fraction.Value)) : -1f;
        }

        // 取れなかったときは、残りを埋めずにすぐ消す。メインスレッドで呼ぶ
        public void HideProgress()
        {
            _hideProgress = true;
        }

        // 結果画面の Continue を押す（押したのと同じ流れで曲選択に戻る）。結果画面が出ていなければ何もしない。
        // ボタンはゲームの非公開のもの（_continueButton）。見つからなければ、押すのは人に任せる
        public void PressContinue()
        {
            if (_results == null || !_results.isActivated || !_results.isInViewControllerHierarchy)
            {
                return;
            }
            var field = typeof(ResultsViewController).GetField("_continueButton", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (!(field?.GetValue(_results) is UnityEngine.UI.Button button) || !button.interactable)
            {
                Plugin.Log.Warn("[results] the continue button was not found");
                return;
            }
            Plugin.Log.Debug("[results] pressing continue");
            button.onClick.Invoke();
        }

        public void Tick()
        {
            if (_failed)
            {
                return;
            }
            if (_results == null)
            {
                if (UnityEngine.Time.unscaledTime < _nextSearch)
                {
                    return;
                }
                _nextSearch = UnityEngine.Time.unscaledTime + 1f;
                _results = UnityEngine.Resources.FindObjectsOfTypeAll<ResultsViewController>().FirstOrDefault();
                if (_results == null)
                {
                    return;
                }
            }
            var show = _results.isActivated && _results.isInViewControllerHierarchy && (_items.Count > 0 || _notice != null);
            if (_panel == null)
            {
                if (!show)
                {
                    return;
                }
                try
                {
                    Create();
                }
                catch (Exception ex)
                {
                    _failed = true;
                    Plugin.Log.Error($"[results] the panel could not be created: {ex}");
                    return;
                }
            }
            if (_panel.gameObject.activeSelf != show)
            {
                if (show)
                {
                    // 曲選択の板を動かしたかもしれないので、出すたびに置き直す
                    Place();
                }
                _panel.gameObject.SetActive(show);
                Plugin.Log.Debug($"[results] panel {(show ? "shown" : "hidden")}");
                if (!show && _previewing)
                {
                    // 結果画面を離れたら、板で流した試聴は止める（次の曲は曲選択に戻ってから選ぶ）
                    _previewing = false;
                    _preview.Stop();
                }
            }
            if (show)
            {
                TickPreviewLoading();
            }
            if (show && _panel.ShowHandle != _movable())
            {
                _panel.ShowHandle = _movable();
            }
            ShowProgress();
        }

        private void ShowProgress()
        {
            if (_progressTrack == null || _progressFill == null)
            {
                return;
            }
            var progress = _progress;
            if (_hideProgress)
            {
                _hideProgress = false;
                _smooth.Stop();
            }
            else
            {
                var wasVisible = _smooth.Visible;
                var finished = progress < 0f;
                if (!_smooth.Update(finished ? (float?)null : progress, _typicalSeconds) && _trackShown == _smooth.Visible)
                {
                    return;
                }
                if (!wasVisible && _smooth.Visible)
                {
                    _startedAt = UnityEngine.Time.unscaledTime;
                }
                else if (wasVisible && finished && !_smooth.Visible)
                {
                    _typicalSeconds = UnityEngine.Mathf.Lerp(_typicalSeconds, UnityEngine.Mathf.Max(0.3f, UnityEngine.Time.unscaledTime - _startedAt - SmoothFinishGuess), 0.3f);
                }
            }
            _trackShown = _smooth.Visible;
            _progressTrack.SetActive(_trackShown);
            PanelLook.SetFill(_progressFill, _smooth.Shown, ProgressWidth);
        }

        // 取り終えてから帯を埋め終わるまでのおよその時間（目安の時間から引く）
        private const float SmoothFinishGuess = 0.2f;

        // 設定画面で大きさや不透明度を変えたら、その場で板に反映する。下の縁がそろうように置き直す
        public void Rescale()
        {
            if (_panel == null || _results == null)
            {
                return;
            }
            _panel.transform.localScale = UnityEngine.Vector3.one * _scale();
            PanelLook.MakeTranslucent(_panel, _opacity());
            Place();
        }

        // 曲選択の板と下の縁をそろえて置く。曲選択の板がまだなければ、結果画面のすぐ上
        private (UnityEngine.Vector3 Position, UnityEngine.Quaternion Rotation, float Radius) Where()
        {
            var height = PanelSize.y * _scale();
            var below = _base();
            if (below == null)
            {
                return PanelLook.Above(_results.screen, height);
            }
            var b = below.Value;
            return (b.Bottom + b.Rotation * UnityEngine.Vector3.up * (height / 2f), b.Rotation, b.Radius);
        }

        private void Place()
        {
            var (position, rotation, _) = Where();
            _panel.transform.SetPositionAndRotation(position, rotation);
        }

        private void HandleReleased(object sender, BeatSaberMarkupLanguage.FloatingScreen.FloatingScreenHandleEventArgs args)
        {
            var bottom = args.Position - args.Rotation * UnityEngine.Vector3.up * (PanelSize.y * _scale() / 2f);
            _moved(bottom, args.Rotation);
            Plugin.Log.Debug($"[results] moved to {args.Position}");
        }

        public void Destroy()
        {
            if (_panel != null)
            {
                _panel.HandleReleased -= HandleReleased;
                UnityEngine.Object.Destroy(_panel.gameObject);
            }
        }

        // 曲選択の板と同じ場所（下の縁をそろえる）に、同じ向き・同じ曲がり方で置く。持ち手は曲選択の板と同じつまみ
        private void Create()
        {
            var rect = (UnityEngine.RectTransform)_results.screen.transform;
            var panelScale = _scale();
            var (position, rotation, radius) = Where();
            _panel = BeatSaberMarkupLanguage.FloatingScreen.FloatingScreen.CreateFloatingScreen(PanelSize, true, position, rotation, radius, true);
            _panel.name = "OctantPathResults";
            _panel.HandleSide = BeatSaberMarkupLanguage.FloatingScreen.FloatingScreen.Side.Top;
            PanelLook.PlaceKnob(_panel);
            PanelLook.PaintKnob(_panel);
            _panel.ShowHandle = false;
            _panel.HandleReleased += HandleReleased;
            PanelLook.MakeTranslucent(_panel, _opacity());
            _panel.transform.localScale = UnityEngine.Vector3.one * panelScale;
            BeatSaberMarkupLanguage.BSMLParser.Instance.Parse(Lang.Fill(Bsml), _panel.gameObject, this);
            PanelLook.Brighten(_progressTrack);
            PanelLook.Brighten(_progressFill);
            _panel.gameObject.SetActive(false);
            Fill();
            Plugin.Log.Debug($"[results] panel created at {position} (results screen at {rect.position})");
        }

        private void Fill()
        {
            if (_list == null)
            {
                return;
            }
            var actions = new DraftCell.Actions { Play = item => _chosen(item), PlayHint = "next.playHint" };
            _cells = _items.Select(i => (object)new DraftCell(i, i == _chosenItem, false, false, true, State, _covers, actions)).ToList();
            _list.Data = _cells;
            _list.TableView.ReloadData();
            _list.TableView.ClearSelection();
            _trackShown = !_smooth.Visible;
            ShowProgress();
        }

        private static string State(RecommendationItem item) => item.Installed ? "" : "  " + Lang.T("item.notFetchedPick");

        // custom-list は選んだ行のデータ（DraftCell）を渡してくる。押した行は試聴だけを流す（次の曲にするのは行の ▶）
        // 選んだ見た目は MarkChosen で出すので、一覧そのものの選択は戻す（受け付けなかった行が選ばれたままにならないように）
        [UIAction("select-cell")]
        private void SelectCell(TableView tableView, object cell)
        {
            tableView.ClearSelection();
            if (!(cell is DraftCell draftCell) || draftCell.Item == null)
            {
                return;
            }
            var url = draftCell.Item.Version?.PreviewUrl;
            if (url != null)
            {
                _preview.Play(url);
                _previewing = true;
            }
            Mark(draftCell.Item, _preview.Progress(url).HasValue);
        }

        // 選んだ曲を受け付けたら、その行を選んだ見た目にする
        public void MarkChosen(RecommendationItem item) => Mark(item, false);

        private void Mark(RecommendationItem item, bool loading)
        {
            _chosenItem = item;
            foreach (var cell in _cells.OfType<DraftCell>())
            {
                cell.SetPlaying(cell.Item == item, loading);
            }
        }

        // 試聴の音源を取っている間、その行に進み具合を出す（下見の小窓と同じ。DraftCell.ShowLoading）
        private void TickPreviewLoading()
        {
            if (_chosenItem == null)
            {
                return;
            }
            foreach (var cell in _cells.OfType<DraftCell>())
            {
                if (cell.Item == _chosenItem)
                {
                    cell.ShowLoading(_preview.Progress(_chosenItem.Version?.PreviewUrl), _preview.TypicalSeconds);
                }
            }
        }
    }
}
