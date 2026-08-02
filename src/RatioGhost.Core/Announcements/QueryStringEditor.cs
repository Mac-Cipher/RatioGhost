using System.Text;

namespace RatioGhost.Core.Announcements;

public sealed class QueryStringEditor
{
    private readonly string _prefix;
    private readonly string _fragment;
    private readonly bool _hadQuery;
    private readonly List<QueryPair> _pairs;

    private QueryStringEditor(string prefix, string fragment, bool hadQuery, List<QueryPair> pairs)
    {
        _prefix = prefix;
        _fragment = fragment;
        _hadQuery = hadQuery;
        _pairs = pairs;
    }

    public static QueryStringEditor Parse(string resource)
    {
        var fragmentIndex = resource.IndexOf('#');
        var fragment = fragmentIndex >= 0 ? resource[fragmentIndex..] : string.Empty;
        var withoutFragment = fragmentIndex >= 0 ? resource[..fragmentIndex] : resource;
        var queryIndex = withoutFragment.IndexOf('?');
        if (queryIndex < 0)
            return new QueryStringEditor(withoutFragment, fragment, false, []);

        var pairs = withoutFragment[(queryIndex + 1)..]
            .Split('&', StringSplitOptions.None)
            .Select(QueryPair.Parse)
            .ToList();
        return new QueryStringEditor(withoutFragment[..queryIndex], fragment, true, pairs);
    }

    public bool Contains(string name) =>
        _pairs.Any(pair => pair.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public string? GetLast(string name) =>
        _pairs.LastOrDefault(pair => pair.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    public string Rewrite(IReadOnlyDictionary<string, string> updates, IReadOnlySet<string>? removals = null)
    {
        if (!_hadQuery)
            return ToString();

        var output = new List<string>(_pairs.Count);
        foreach (var pair in _pairs)
        {
            if (removals?.Contains(pair.Name) == true)
                continue;

            var update = updates.FirstOrDefault(item =>
                item.Key.Equals(pair.Name, StringComparison.OrdinalIgnoreCase));
            output.Add(update.Key is null ? pair.Raw : $"{pair.Name}={update.Value}");
        }

        return $"{_prefix}?{string.Join('&', output)}{_fragment}";
    }

    public override string ToString() =>
        _hadQuery ? $"{_prefix}?{string.Join('&', _pairs.Select(pair => pair.Raw))}{_fragment}" : $"{_prefix}{_fragment}";

    private sealed record QueryPair(string Name, string Value, string Raw)
    {
        public static QueryPair Parse(string raw)
        {
            var equalsIndex = raw.IndexOf('=');
            return equalsIndex < 0
                ? new QueryPair(raw, string.Empty, raw)
                : new QueryPair(raw[..equalsIndex], raw[(equalsIndex + 1)..], raw);
        }
    }
}
