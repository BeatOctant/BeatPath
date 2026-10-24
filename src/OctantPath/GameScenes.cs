namespace OctantPath
{
    internal static class GameScenes
    {
        // プレイ中（ゲームのシーン GameCore が読み込まれている）か
        public static bool InGameplay(this GameScenesManager scenes) => scenes != null && scenes.GetCurrentlyLoadedSceneNames().Contains("GameCore");
    }
}
