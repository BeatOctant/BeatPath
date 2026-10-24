using OctantPath.Core.Profile;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace OctantPath.Core.Tests
{
    public class ProfileTests
    {
        [Fact]
        public void ReadsTheProfileExample()
        {
            var profile = JsonConvert.DeserializeObject<ProfileResponse>(OctantSpecsExamples.Text("profile.response.json"));
            Assert.Equal(42, profile.LikedMaps);
            Assert.Equal(380, profile.Plays);
            var standard = profile.PlayStyles["Standard"];
            Assert.Equal(200, standard.StrongCharts);
            Assert.Equal(17.5, standard.Features["njs"]);
            Assert.True(standard.ClearCurve);
            var text = ProfileText.Describe(profile);
            Assert.Contains("Strong maps 200", text);
            Assert.Contains("NJS 17.5", text);
        }

        [Fact]
        public void WritesTheProfileRequest()
        {
            var example = OctantSpecsExamples.Json("profile.request.json");
            var request = example.ToObject<ProfileRequest>();
            Assert.True(JToken.DeepEquals(example, JObject.Parse(JsonConvert.SerializeObject(request))));
        }

        [Fact]
        public void SaysWhenThereAreNoPlayStylesYet()
        {
            var profile = JsonConvert.DeserializeObject<ProfileResponse>("{\"dbVersion\":\"x\",\"likedMaps\":309,\"plays\":0,\"playStyles\":{},\"replaced\":[],\"unknownEvents\":6}");
            Assert.Contains("Not enough plays yet", ProfileText.Describe(profile));
        }

        [Fact]
        public void ShowsWeakDirectionsWithTheirWeights()
        {
            var profile = JsonConvert.DeserializeObject<ProfileResponse>("{\"playStyles\":{\"Standard\":{\"weakDirections\":[{\"feature\":\"njs\",\"direction\":\"low\",\"weight\":1.8},{\"feature\":\"nps\",\"direction\":\"high\",\"weight\":1.04}]}}}");
            var text = ProfileText.Describe(profile);
            Assert.Contains("NJS lower ×1.8", text);
            Assert.Contains("NPS higher ×1.04", text);
        }
    }
}
