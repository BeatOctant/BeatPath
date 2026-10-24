using System.Linq;
using OctantPath.Core.Connection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace OctantPath.Core.Tests
{
    // 契約3（OctantPath to OctantCompass）の例（OctantSpecs）を OctantPath の型で読み書きできるか
    public class Contract3ExampleTests
    {
        [Fact]
        public void ReadsReadyHealth()
        {
            var health = JsonConvert.DeserializeObject<HealthResponse>(OctantSpecsExamples.Text("health.ready.json"));
            Assert.Equal("2.0", health.ApiVersion);
            Assert.Equal(new[] { "clap_htsat_unfused@1" }, health.Db.Roles["music"]);
            Assert.Equal(HealthStatus.Ready, health.Status);
            Assert.True(health.Paired);
            Assert.Equal("4f1c2a9d8b7e6f50", health.InstanceId);
            Assert.Equal(9, health.Signals);
            Assert.Equal("2026.10.04", health.Db.DbVersion);
            Assert.NotEmpty(health.Db.Genres);
        }

        [Fact]
        public void TurnsRolesIntoTheRoleOfEachView()
        {
            var health = JsonConvert.DeserializeObject<HealthResponse>(OctantSpecsExamples.Text("health.ready.json"));
            var viewRoles = HostConnection.ViewRolesOf(health.Db.Roles);
            Assert.Equal("music", viewRoles["clap_htsat_unfused@1"]);
            Assert.Equal(health.Db.Roles.Values.Sum(v => v.Count), viewRoles.Count);
            Assert.Null(HostConnection.ViewRolesOf(null));
        }

        [Fact]
        public void ReadsDegradedHealthWithoutToken()
        {
            var health = JsonConvert.DeserializeObject<HealthResponse>(OctantSpecsExamples.Text("health.degraded.json"));
            Assert.Equal(HealthStatus.Degraded, health.Status);
            Assert.Equal("schema_unsupported", health.Reason);
            Assert.Null(health.Db);
            Assert.Null(health.Paired);
            Assert.Null(health.InstanceId);
            Assert.Null(health.Signals);
        }

        [Fact]
        public void ReadsUpdateReason()
        {
            var health = JsonConvert.DeserializeObject<HealthResponse>(OctantSpecsExamples.Text("health.update_unsupported.json"));
            Assert.Equal(HealthStatus.Ready, health.Status);
            Assert.Equal("index_unsupported", health.Update.Reason);
        }

        [Fact]
        public void WritesPairRequestInTheExampleShape()
        {
            var example = OctantSpecsExamples.Json("pair.request.json");
            var request = new PairRequest { Secret = (string)example["secret"], Name = (string)example["name"] };
            Assert.True(JToken.DeepEquals(example, JObject.FromObject(request)));
        }

        [Fact]
        public void ReadsPairResponse()
        {
            var response = JsonConvert.DeserializeObject<PairResponse>(OctantSpecsExamples.Text("pair.response.json"));
            Assert.False(string.IsNullOrEmpty(response.Token));
        }

        [Theory]
        [InlineData("error.json", "not_ready")]
        [InlineData("error.unauthorized.json", "unauthorized")]
        [InlineData("error.version_mismatch.json", "version_mismatch")]
        public void ReadsErrors(string file, string code)
        {
            var error = JsonConvert.DeserializeObject<ErrorBody>(OctantSpecsExamples.Text(file)).Error;
            Assert.Equal(code, error.Code);
            Assert.False(string.IsNullOrEmpty(error.Message));
        }

        [Fact]
        public void ReadsTheHostVersionFromVersionMismatch()
        {
            var error = JsonConvert.DeserializeObject<ErrorBody>(OctantSpecsExamples.Text("error.version_mismatch.json")).Error;
            Assert.Equal("2.0", error.ApiVersion);
        }
    }
}
