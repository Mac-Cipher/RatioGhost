using System.Text;
using RatioGhost.Core.Announcements;

namespace RatioGhost.Core.Tests;

public sealed class TrackerResponseParserTests
{
    [Fact]
    public void Parse_ExtractsLegacyBencodeFields()
    {
        var response = TrackerResponseParser.Parse(
            Encoding.ASCII.GetBytes("d8:completei12e10:incompletei3e8:intervali1800ee"));

        Assert.Equal(12, response.Complete);
        Assert.Equal(3, response.Incomplete);
        Assert.Equal(1800, response.Interval);
    }

    [Fact]
    public void Parse_ExtractsBoundedFailureReason()
    {
        var response = TrackerResponseParser.Parse(
            Encoding.ASCII.GetBytes("d14:failure reason9:try lateree"));

        Assert.Equal("try later", response.FailureReason);
    }
}
