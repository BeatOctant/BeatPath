namespace OctantPath.Core.Trials
{
    // OctantPath が作るプレイリストは Playlists/OctantPath/ にまとめる（ほかのプレイリストと混ぜない）
    public static class PlaylistFolder
    {
        public const string Name = "OctantPath";

        // 保存したミックスの印（PlaylistGuard.MarkKey とは別。こちらは自動削除から守る側）
        public const string SavedMixKey = "OctantPathMix";
    }
}
