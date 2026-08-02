using RatioGhost.Core.Announcements;

namespace RatioGhost.Core.Tests;

public sealed class QueryStringEditorTests
{
    [Fact]
    public void Parse_UsesLastDuplicateAndPreservesEncodedValue()
    {
        var query = QueryStringEditor.Parse(
            "/announce?uploaded=1&empty=&info_hash=abc%20def&uploaded=2");

        Assert.Equal("2", query.GetLast("uploaded"));
        Assert.Equal(string.Empty, query.GetLast("empty"));
        Assert.Equal("abc%20def", query.GetLast("INFO_HASH"));
    }

    [Fact]
    public void Rewrite_IsCaseInsensitiveAndPreservesFragment()
    {
        var result = QueryStringEditor.Parse(
                "/announce?downloaded=12&event=completed&left=44#frag")
            .Rewrite(
                new Dictionary<string, string> { ["downloaded"] = "0", ["left"] = "99" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "event" });

        Assert.Equal("/announce?downloaded=0&left=99#frag", result);
    }
}
