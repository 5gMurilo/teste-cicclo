using Cicclo.Api.Tests.Support;
using Xunit;

namespace Cicclo.Api.Tests.Integration.Bootstrap;

[Collection(PostgresCollection.Name)]
[Trait("Increment", "E01")]
[Trait("Task", "T2")]
public sealed class BootstrapImageTests(PostgresFixture postgres)
{
    [Fact]
    public async Task B12_RuntimeImageRunsBootstrapWithoutPsqlSdkOrListener()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var image = $"cicclo-api-bootstrap-test:{suffix}";
        var database = await postgres.CreateDatabaseAsync();
        try
        {
            var build = await Docker(["build", "--tag", image, RepositoryPaths.ApiRoot], TimeSpan.FromMinutes(15));
            Assert.True(build.ExitCode == 0, build.Output);

            var run = await Docker(
                [
                    "run", "--rm", "--name", $"cicclo-api-bootstrap-test-{suffix}",
                    "--env", "ASPNETCORE_ENVIRONMENT=Development",
                    "--env", "Laundry__DemoMode=true",
                    "--env", $"ConnectionStrings__Cicclo={postgres.ContainerNetworkConnectionString(database)}",
                    image, "--bootstrap-demo",
                ],
                TimeSpan.FromMinutes(2));
            var tools = await Docker(
                ["run", "--rm", "--entrypoint", "sh", image, "-c", "command -v psql || echo no-psql; dotnet --list-sdks | wc -l; id -u"],
                TimeSpan.FromMinutes(1));

            Assert.True(run.ExitCode == 0, run.Output);
            Assert.DoesNotContain("Now listening on", run.Output);
            Assert.DoesNotContain(PostgresFixture.Password, run.Output);
            Assert.Equal(DemoData.InitialSnapshot, await DemoData.SnapshotAsync(database));
            var lines = tools.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Equal("no-psql", lines[0]);
            Assert.Equal("0", lines[1]);
            Assert.NotEqual("0", lines[2]);
        }
        finally
        {
            await Docker(["image", "rm", "--force", image], TimeSpan.FromMinutes(1));
        }
    }

    private static Task<ProcessResult> Docker(string[] arguments, TimeSpan timeout) =>
        ProcessRunner.RunAsync("docker", arguments, RepositoryPaths.ApiRoot, timeout: timeout);
}
