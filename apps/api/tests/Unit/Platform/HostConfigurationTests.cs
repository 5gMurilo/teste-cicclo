using System.Text.Json;
using System.Text.Json.Serialization;
using Cicclo.Api.Platform;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cicclo.Api.Tests.Unit.Platform;

[Trait("Increment", "E01")]
[Trait("Task", "T3")]
public sealed class HostConfigurationTests
{
    private const string SecretConnection = "Host=db;Database=cicclo;Username=cicclo;Password=nao-vazar-123";

    [Theory]
    [InlineData("ConfirmationTimeoutSeconds", "0")]
    [InlineData("ConfirmationTimeoutSeconds", "-1")]
    [InlineData("QueueWorkerIntervalSeconds", "0")]
    [InlineData("QueueWorkerIntervalSeconds", "-5")]
    [InlineData("MaxServicesPerBooking", "0")]
    [InlineData("MaxServicesPerBooking", "11")]
    public void P07_InvalidLaundryOptionsPreventStartupWithoutSecrets(string option, string value)
    {
        using var factory = Factory(new Dictionary<string, string> { [$"Laundry:{option}"] = value });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var validation = Assert.IsType<OptionsValidationException>(Flatten(error).OfType<OptionsValidationException>().First());
        Assert.Equal(typeof(LaundryOptions), validation.OptionsType);
        Assert.Contains(option, validation.Message);
        Assert.DoesNotContain("nao-vazar-123", validation.Message);
    }

    [Theory]
    [InlineData(null, null, null, 60, 5, 10)]
    [InlineData("1", "1", "1", 1, 1, 1)]
    [InlineData("120", "30", "10", 120, 30, 10)]
    public void P08_DefaultsAndBoundaryValuesAreAccepted(
        string? timeout, string? worker, string? batch, int expectedTimeout, int expectedWorker, int expectedBatch)
    {
        var settings = new Dictionary<string, string>();
        if (timeout is not null)
        {
            settings["Laundry:ConfirmationTimeoutSeconds"] = timeout;
            settings["Laundry:QueueWorkerIntervalSeconds"] = worker!;
            settings["Laundry:MaxServicesPerBooking"] = batch!;
        }

        using var factory = Factory(settings);
        using var client = factory.CreateClient();

        var options = factory.Services.GetRequiredService<IOptions<LaundryOptions>>().Value;
        Assert.Equal((expectedTimeout, expectedWorker, expectedBatch), (options.ConfirmationTimeoutSeconds, options.QueueWorkerIntervalSeconds, options.MaxServicesPerBooking));
        Assert.False(options.DemoMode);
    }

    [Fact]
    public void P09_JsonContractAndRequestBodyLimitAreConfiguredOnTheRealHost()
    {
        using var factory = Factory([]);
        using var client = factory.CreateClient();

        var json = factory.Services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        var kestrel = factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;

        Assert.Same(JsonNamingPolicy.CamelCase, json.PropertyNamingPolicy);
        Assert.Equal(JsonIgnoreCondition.Never, json.DefaultIgnoreCondition);
        Assert.Equal(JsonUnmappedMemberHandling.Disallow, json.UnmappedMemberHandling);
        Assert.Equal(JsonNumberHandling.Strict, json.NumberHandling);
        Assert.Equal(16 * 1024, kestrel.Limits.MaxRequestBodySize);
    }

    private static WebApplicationFactory<Program> Factory(Dictionary<string, string> settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseSetting("ConnectionStrings:Cicclo", SecretConnection);
            foreach (var (key, value) in settings)
            {
                host.UseSetting(key, value);
            }
        });

    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        yield return exception;
        var inner = exception is AggregateException aggregate ? aggregate.InnerExceptions : exception.InnerException is { } single ? [single] : [];
        foreach (var child in inner.SelectMany(Flatten))
        {
            yield return child;
        }
    }
}
