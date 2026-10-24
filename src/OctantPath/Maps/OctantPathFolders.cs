using System.IO;
using IPA.Utilities;

namespace OctantPath.Maps
{
    // OctantPath が使う場所。専用フォルダと作業用の場所は同じファイルシステムに置く（OctantPath 法 2.4）
    internal sealed class OctantPathFolders
    {
        // ゲームの UserData/OctantPath
        public static OctantPathFolders InUserData() => new OctantPathFolders(Path.Combine(UnityGame.UserDataPath, "OctantPath"));

        public OctantPathFolders(string root)
        {
            Root = root;
            Levels = Path.Combine(root, "Levels");
            Work = Path.Combine(root, "Work");
        }

        public string Root { get; }

        // 「お試し中」のマップを置く専用フォルダ。SongCore に別のパックとして登録する
        public string Levels { get; }

        public string Work { get; }

        // 言語ファイルの差し替えと追加を置く場所（lang/<言語>.json）
        public string Languages => Path.Combine(Root, "lang");
    }
}
