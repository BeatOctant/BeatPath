using System;
using System.Linq;
using OctantPath.Maps;
using UnityEngine;

namespace OctantPath.UI
{
    // 曲選択で、ある曲を選んだ状態にする。ゲームの公開のメソッドだけを使う。
    // 1. 今の一覧にあれば、そのまま選ぶ
    // 2. なければ、曲選択の始まりの状態（ミックスのプレイリスト、なければカテゴリ「すべて」、その曲）を渡して、曲選択を開き直す（BetterSongSearch と同じやり方）。
    //    結果画面が出ている間に開き直すと画面が壊れる（曲詳細が切り替わらなくなった）ので、曲選択が出ているときだけ呼ぶ
    internal static class SongSelect
    {
        private const SelectLevelCategoryViewController.LevelCategory AllSongs = SelectLevelCategoryViewController.LevelCategory.All;

        // pack はミックスのプレイリスト（ミックスがなければ null）
        public static bool Select(string hash, string characteristic, string difficulty, BeatmapLevelPack pack)
        {
            try
            {
                var level = SongCore.Loader.GetLevelByHash(hash);
                if (level == null)
                {
                    Plugin.Log.Warn($"[songselect] {hash} is not loaded");
                    return false;
                }
                if (SelectInList(level))
                {
                    Plugin.Log.Info($"[songselect] selected {hash} in the current list");
                    return true;
                }
                return pack != null
                    ? Reopen(level, characteristic, difficulty, SelectLevelCategoryViewController.LevelCategory.CustomSongs, pack)
                    : Reopen(level, characteristic, difficulty, AllSongs, SongCore.Loader.CustomLevelsPack);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[songselect] {hash}: {ex}");
                return false;
            }
        }

        // 今の一覧にあるときだけ選ぶ（開き直さない）
        public static bool SelectInCurrentList(string hash)
        {
            try
            {
                var level = SongCore.Loader.GetLevelByHash(hash);
                return level != null && SelectInList(level);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[songselect] {hash}: {ex.Message}");
                return false;
            }
        }

        // 今の一覧で選んでみて、選べたかを返す（例外は呼び出し側で受ける）
        private static bool SelectInList(BeatmapLevel level)
        {
            var collection = Resources.FindObjectsOfTypeAll<LevelCollectionNavigationController>().FirstOrDefault();
            if (collection == null)
            {
                return false;
            }
            collection.SelectLevel(level);
            return collection.beatmapLevel == level;
        }

        // パック（OctantPath のミックスのプレイリスト）を開き、その中の曲を選んだ状態にする
        public static bool OpenPack(BeatmapLevelPack pack, string hash, string characteristic, string difficulty)
        {
            try
            {
                var level = SongCore.Loader.GetLevelByHash(hash);
                if (level == null)
                {
                    Plugin.Log.Warn($"[songselect] {hash} is not loaded");
                    return false;
                }
                return Reopen(level, characteristic, difficulty, SelectLevelCategoryViewController.LevelCategory.CustomSongs, pack);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[songselect] could not open the pack: {ex}");
                return false;
            }
        }

        // ゲームの一覧の行は、作り直し（ReloadData）で使い回されるとカバーの読み込みを取り消す。その読み込みが終わる前に同じ曲を渡されると、
        // 読み込み中とみなして何もしない（LevelListTableCell.SetDataFromLevelAsync）。取ったばかりの曲はカバーの読み込みが長いので、
        // 曲選択に戻ってから一覧の作り直し（SongCore の読み直し、プレイリストの書き直し、開き直し）が続くと、その曲のカバーだけ
        // 次にめくるまで出なかった（2026-10-18）。一覧が落ち着くのを待ち、その曲のカバーを読み終えてから、見えている行を描き直す
        public static async void RedrawCovers(System.Collections.Generic.IEnumerable<string> hashes)
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(RedrawDelayMs);
                foreach (var level in hashes.Distinct().Select(SongCore.Loader.GetLevelByHash).Where(l => l != null).ToList())
                {
                    await level.previewMediaData.GetCoverSpriteAsync();
                }
                // 取り消された行の読み込みが終わるのを待つ（終わる前に描き直すと、また何もしない）
                await System.Threading.Tasks.Task.Delay(100);
                var collection = Resources.FindObjectsOfTypeAll<LevelCollectionTableView>().FirstOrDefault(c => c.isActiveAndEnabled);
                var table = collection != null ? collection.GetComponentInChildren<HMUI.TableView>() : null;
                if (table == null)
                {
                    return;
                }
                table.RefreshCellsContent();
                Plugin.Log.Debug("[songselect] redrew the visible song cells");
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[songselect] could not redraw the song cells: {ex.Message}");
            }
        }

        // 一覧の作り直しが落ち着くまで（開き直しの動きと、SongCore の読み直しの後の作り直しを含む。推測で決めた値）
        private const int RedrawDelayMs = 1000;

        private static bool Reopen(BeatmapLevel level, string characteristic, string difficulty, SelectLevelCategoryViewController.LevelCategory category, BeatmapLevelPack pack)
        {
            var main = Resources.FindObjectsOfTypeAll<MainFlowCoordinator>().FirstOrDefault();
            var solo = Resources.FindObjectsOfTypeAll<SoloFreePlayFlowCoordinator>().FirstOrDefault();
            if (main == null || solo == null)
            {
                Plugin.Log.Warn($"[songselect] cannot reopen: main={main != null} solo={solo != null}");
                return false;
            }
            var key = level.GetBeatmapKeys().FirstOrDefault(k => LevelLookup.IsChart(k, characteristic, difficulty));
            if (key.beatmapCharacteristic == null)
            {
                key = level.GetBeatmapKeys().First();
            }
            solo.Setup(new LevelSelectionFlowCoordinator.State(category, pack, in key, level));
            var parent = main;
            parent.DismissFlowCoordinator(solo, HMUI.ViewController.AnimationDirection.Horizontal, () =>
            {
                parent.PresentFlowCoordinator(solo, null, HMUI.ViewController.AnimationDirection.Horizontal, true);
                Plugin.Log.Info($"[songselect] reopened song select on {level.levelID}");
            }, true);
            return true;
        }
    }
}
