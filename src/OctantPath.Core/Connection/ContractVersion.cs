using System;

namespace OctantPath.Core.Connection
{
    // 契約の版（"メジャー.マイナー"）。比べ方は OctantSpecs の「契約の版と食い違いの知らせ方」
    public readonly struct ContractVersion
    {
        // OctantPath が話す契約3（OctantPath to OctantCompass）の版
        public static readonly ContractVersion Api = new ContractVersion(2, 0);

        public ContractVersion(int major, int minor)
        {
            Major = major;
            Minor = minor;
        }

        public int Major { get; }
        public int Minor { get; }

        public static bool TryParse(string text, out ContractVersion version)
        {
            version = default;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            var parts = text.Split('.');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor) || major < 0 || minor < 0)
            {
                return false;
            }
            version = new ContractVersion(major, minor);
            return true;
        }

        public override string ToString() => $"{Major}.{Minor}";
    }
}
