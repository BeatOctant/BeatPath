using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace OctantPath.Core.Tests
{
    // OctantSpecs の contracts/examples/mod-api/（契約3（OctantPath to OctantCompass）の例）を読む。scripts/fetch-octantspecs.sh で .octantspecs/ に取ってくる。
    // OCTANTSPECS_DIR で別の場所を指せる
    internal static class OctantSpecsExamples
    {
        public static string Contract3Dir
        {
            get
            {
                var root = Environment.GetEnvironmentVariable("OCTANTSPECS_DIR");
                if (string.IsNullOrEmpty(root))
                {
                    root = Path.Combine(RepositoryRoot(), ".octantspecs");
                }
                var dir = Path.Combine(root, "contracts", "examples", "mod-api");
                if (!Directory.Exists(dir))
                {
                    throw new InvalidOperationException($"{dir} がありません。scripts/fetch-octantspecs.sh を実行してください");
                }
                return dir;
            }
        }

        public static string Text(string name) => File.ReadAllText(Path.Combine(Contract3Dir, name));

        public static JObject Json(string name) => JObject.Parse(Text(name));

        internal static string RepositoryRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "OCTANTSPECS_REF")))
            {
                dir = dir.Parent;
            }
            if (dir == null)
            {
                throw new InvalidOperationException("OCTANTSPECS_REF が見つかりません");
            }
            return dir.FullName;
        }
    }
}
