namespace Functions.Curator.Catalog;

public static class MergeService
{
    public static IReadOnlyList<IReadOnlyList<GroupedEntry>> MergeByProductIdAndName(
        IReadOnlyList<KeyValuePair<string, IReadOnlyList<GroupedEntry>>> groups)
    {
        var keysByProductId = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var entriesByKey = new Dictionary<string, IReadOnlyList<GroupedEntry>>(StringComparer.Ordinal);
        var keyOrder = new List<string>();

        foreach (var (key, entries) in groups)
        {
            entriesByKey[key] = entries;
            keyOrder.Add(key);

            var productId = entries[0].ProductId;
            if (!string.IsNullOrWhiteSpace(productId))
            {
                if (!keysByProductId.TryGetValue(productId, out var keys))
                {
                    keys = [];
                    keysByProductId[productId] = keys;
                }

                keys.Add(key);
            }
        }

        var merged = new List<IReadOnlyList<GroupedEntry>>();
        var absorbed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (_, keys) in keysByProductId)
        {
            if (keys.Count < 2)
            {
                continue;
            }

            var keysByName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var nameOrder = new List<string>();
            foreach (var key in keys)
            {
                var name = entriesByKey[key][0].Name.ToLowerInvariant();
                if (!keysByName.TryGetValue(name, out var sameName))
                {
                    sameName = [];
                    keysByName[name] = sameName;
                    nameOrder.Add(name);
                }

                sameName.Add(key);
            }

            foreach (var name in nameOrder)
            {
                var sameNameKeys = keysByName[name];
                if (sameNameKeys.Count <= 1)
                {
                    continue;
                }

                merged.Add(sameNameKeys.SelectMany(key => entriesByKey[key]).ToList());
                absorbed.UnionWith(sameNameKeys);
            }
        }

        foreach (var key in keyOrder.Where(key => !absorbed.Contains(key)))
        {
            merged.Add(entriesByKey[key]);
        }

        return merged;
    }
}
