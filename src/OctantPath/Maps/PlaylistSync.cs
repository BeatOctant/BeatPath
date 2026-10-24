using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OctantPath.Core.Maps;
using OctantPath.Core.Settings;
using OctantPath.Core.Trials;
using BeatSaberPlaylistsLib;
using BeatSaberPlaylistsLib.Types;
using OctantPath.Core.Languages;

namespace OctantPath.Maps
{
    // 枠ごとの専用プレイリスト。台帳の「お試し中」と「残す」から作る表示で、所有の証拠にはしない。
    // OctantPath の印がないファイル、読めないファイルは書き換えない（OctantPath 法 3.7）
    internal sealed class PlaylistSync
    {
        public void Rebuild(OwnershipLedger ledgerStore, OctantPathSettings settings)
        {
            var ledger = ledgerStore.Load();
            if (ledger == null)
            {
                Plugin.Log.Warn("[playlist] ledger.json could not be read; playlists are left as they are");
                return;
            }
            var frames = new List<(string Id, string Title)> { (FrameEntry.Manual, Lang.T("playlist.manual")), (FrameEntry.Mix, Lang.T("playlist.mixFrame")) };
            frames.AddRange(settings.TrialFrames.Select(f => (f.Id, f.DisplayName)));
            var manager = Folder();
            foreach (var (id, title) in frames)
            {
                var maps = ledger.Maps.Where(m => (m.State == MapStates.Trial || m.State == MapStates.Kept) && m.FrameIds().Contains(id)).ToList();
                var fileName = id == FrameEntry.Manual ? "OctantPath" : $"OctantPath-{id}";
                var error = Write(manager, fileName, $"OctantPath {title}", maps);
                if (error != null)
                {
                    Plugin.Log.Warn($"[playlist] {error}");
                }
            }
            PlaylistManager.DefaultManager.RequestRefresh("OctantPath");
        }

        // ミックスを保存する。OctantPath のお試しの印（PlaylistGuard.MarkKey）は付けないので、
        // ほかのプレイリストと同じように、入っている曲は自動削除から守られる
        public void SaveMix(string title, List<(string Hash, string Name, string Key, string Mapper)> songs)
        {
            var manager = Folder();
            var fileName = "OctantPath-mix-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var playlist = manager.CreatePlaylist(fileName, title, "OctantPath", (string)null, Lang.T("playlist.savedDescription"));
            playlist.SetCustomData(PlaylistFolder.SavedMixKey, true);
            foreach (var song in songs.Where(s => s.Hash != null))
            {
                playlist.Add(song.Hash, song.Name, song.Key, song.Mapper);
            }
            manager.StorePlaylist(playlist);
            PlaylistManager.DefaultManager.RequestRefresh("OctantPath");
        }

        // ミックスの作成で開く一時的なプレイリスト。作るたびに中身を置き換える。
        // OctantPath の印を付けるので、このプレイリストに入っているだけでは自動削除から守らない（保存したミックスとは違う）
        public BeatmapLevelPack WriteCurrentMix(string title, List<(string Hash, string Name, string Key, string Mapper)> songs)
        {
            const string fileName = "OctantPath-mix-current";
            var manager = Folder();
            var playlist = manager.GetPlaylist(fileName, false);
            if (playlist == null && File.Exists(Path.Combine(manager.PlaylistPath, fileName + ".bplist")))
            {
                Plugin.Log.Warn($"[playlist] {fileName}.bplist が読めないので書き換えません");
                return null;
            }
            if (playlist != null && !playlist.TryGetCustomData(PlaylistGuard.MarkKey, out _))
            {
                Plugin.Log.Warn($"[playlist] {fileName}.bplist は OctantPath が作ったものではないので書き換えません");
                return null;
            }
            if (playlist == null)
            {
                playlist = manager.CreatePlaylist(fileName, title, "OctantPath", (string)null, Lang.T("playlist.mixDescription"));
                playlist.SetCustomData(PlaylistGuard.MarkKey, "mix");
            }
            playlist.Title = title;
            foreach (var song in playlist.ToList())
            {
                playlist.TryRemove(song);
            }
            foreach (var song in songs.Where(s => s.Hash != null))
            {
                playlist.Add(song.Hash, song.Name, song.Key, song.Mapper);
            }
            manager.StorePlaylist(playlist);
            PlaylistManager.DefaultManager.RequestRefresh("OctantPath");
            return playlist.PlaylistLevelPack;
        }

        // OctantPath のプレイリストは Playlists/OctantPath/ に置き、ほかのプレイリストと混ぜない（推測: PlaylistManager では 1 つのフォルダに見える）
        private static PlaylistManager Folder() => PlaylistManager.DefaultManager.CreateChildManager(PlaylistFolder.Name);

        private static string Write(PlaylistManager manager, string fileName, string title, List<OwnedMap> maps)
        {
            var playlist = manager.GetPlaylist(fileName, false);
            if (playlist == null && File.Exists(Path.Combine(manager.PlaylistPath, fileName + ".bplist")))
            {
                return Lang.T("playlist.unreadable", new { file = fileName });
            }
            if (playlist == null)
            {
                if (maps.Count == 0)
                {
                    return null;
                }
                playlist = manager.CreatePlaylist(fileName, title, "OctantPath", (string)null, Lang.T("playlist.trialDescription"));
                playlist.SetCustomData(PlaylistGuard.MarkKey, "trial");
            }
            else if (!playlist.TryGetCustomData(PlaylistGuard.MarkKey, out _))
            {
                return Lang.T("playlist.notOurs", new { file = fileName });
            }

            var wanted = new HashSet<string>(maps.Select(m => m.Hash));
            foreach (var song in playlist.ToList())
            {
                if (song.Hash == null || !wanted.Contains(song.Hash.ToLowerInvariant()))
                {
                    playlist.TryRemove(song);
                }
            }
            var present = new HashSet<string>(playlist.Where(s => s.Hash != null).Select(s => s.Hash.ToLowerInvariant()));
            foreach (var map in maps.Where(m => !present.Contains(m.Hash)))
            {
                playlist.Add(map.Hash, map.Folder, map.MapKey, null);
            }
            manager.StorePlaylist(playlist);
            return null;
        }
    }
}
