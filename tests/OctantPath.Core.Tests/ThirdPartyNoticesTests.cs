using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace OctantPath.Core.Tests
{
    // OctantPath Desktop の実行ファイルに入るパッケージ（Desktop と Core の PackageReference）が、
    // 一緒に配る src/OctantPath.Desktop/THIRD-PARTY-NOTICES.txt に名前と版で書かれていること
    public sealed class ThirdPartyNoticesTests
    {
        [Theory]
        [InlineData("src/OctantPath.Desktop/OctantPath.Desktop.csproj")]
        [InlineData("src/OctantPath.Core/OctantPath.Core.csproj")]
        public void NamesEveryPackageDesktopShips(string project)
        {
            var root = OctantSpecsExamples.RepositoryRoot();
            var notices = File.ReadAllText(Path.Combine(root, "src", "OctantPath.Desktop", "THIRD-PARTY-NOTICES.txt"));
            var packages = XDocument.Load(Path.Combine(root, project)).Descendants("PackageReference")
                .Where(p => (string)p.Attribute("PrivateAssets") != "all")
                .Select(p => $"{(string)p.Attribute("Include")} {(string)p.Attribute("Version")}")
                .ToList();
            Assert.NotEmpty(packages);
            Assert.All(packages, package => Assert.Contains(package, notices));
        }
    }
}
