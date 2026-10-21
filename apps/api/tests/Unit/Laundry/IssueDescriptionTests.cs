using Cicclo.Api.Laundry.ReportIssue;
using Xunit;

namespace Cicclo.Api.Tests.Unit.Laundry;

/// <summary>Regra pura da descrição do relato: opcional, até 1000 caracteres, vazia ou só espaços vira null, aparada.</summary>
[Trait("Increment", "E06")]
[Trait("Task", "T4")]
public sealed class IssueDescriptionTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(" \t\n ", null)]
    [InlineData("  parou de girar ", "parou de girar")]
    [InlineData("água no chão", "água no chão")]
    public void U01_AbsentBlankOrPaddedDescriptionsAreNormalized(string? raw, string? expected)
    {
        Assert.True(IssueDescription.TryNormalize(raw, out var description));
        Assert.Equal(expected, description);
    }

    [Fact]
    public void U02_TheLimitIs1000CharactersOfTheReceivedText()
    {
        var max = new string('a', 1000);

        Assert.True(IssueDescription.TryNormalize(max, out var accepted));
        Assert.Equal(max, accepted);
        Assert.False(IssueDescription.TryNormalize(max + "a", out var tooLong));
        Assert.Null(tooLong);
        Assert.False(IssueDescription.TryNormalize(" " + max, out _));
    }
}
