using System;
using System.IO;
using OctantPath.Core.Maps;
using Xunit;

namespace OctantPath.Core.Tests
{
    public class PlacedStoreTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "octantpath-placed-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, true);
            }
        }

        [Fact]
        public void KeepsPlacedMapsUntilRemoved()
        {
            var store = new PlacedStore(_dir);
            var a = new PlacedMap { Hash = "a", Folder = "A", Frame = new FrameEntry { Frame = "mix" } };
            store.Add(a);
            store.Add(new PlacedMap { Hash = "b", Folder = "B" });
            store.Add(new PlacedMap { Hash = "a", Folder = "A" });
            Assert.Equal(2, new PlacedStore(_dir).Load().Count);
            store.Remove(new[] { a });
            Assert.Equal(new[] { "b" }, new PlacedStore(_dir).Load().ConvertAll(m => m.Hash));
        }

        [Fact]
        public void TreatsABrokenFileAsNothingPlaced()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, PlacedStore.FileName), "{ broken");
            Assert.Empty(new PlacedStore(_dir).Load());
        }
    }
}
