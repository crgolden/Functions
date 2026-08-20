namespace Functions.Curator.Catalog;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Library;
using Psn;

public static partial class CanonicalizationService
{
    public const int UnrankedEdition = 99;

    private static readonly HashSet<string> NonGamePackageTypes = new(StringComparer.Ordinal)
    {
        "PS4MISC", "PS4AC", "PS4AL", "PSAC", "PSAL", "PSTRACK", "PSMEDIA", "PSCONS", "PSSUBS",
    };

    public static string? NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var decomposed = name.Normalize(NormalizationForm.FormKD);
        var withoutCombining = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                withoutCombining.Append(character);
            }
        }

        var normalized = TrademarkSymbols().Replace(withoutCombining.ToString(), string.Empty);
        normalized = TrademarkLetters().Replace(normalized, string.Empty);
        normalized = EmptyParentheses().Replace(normalized, string.Empty);
        var collapsed = Whitespace().Replace(normalized, " ").Trim();
        return collapsed.Length == 0 ? null : collapsed;
    }

    public static int EditionRank(string name, IReadOnlyDictionary<string, int> ranks)
    {
        var lower = name.ToLowerInvariant();
        foreach (var (keyword, rank) in ranks.OrderBy(entry => entry.Value))
        {
            if (lower.Contains(keyword, StringComparison.Ordinal))
            {
                return rank;
            }
        }

        return UnrankedEdition;
    }

    public static IReadOnlyList<CanonicalGame> Canonicalize(
        IReadOnlyList<EntitlementSnapshot> snapshots,
        IReadOnlyList<ExclusionRule> exclusionRules,
        IReadOnlyList<FranchiseRule> franchiseRules,
        IReadOnlyDictionary<string, int> editionRanks,
        IReadOnlyDictionary<string, string> nameOverrides) =>
        Canonicalize(
            snapshots,
            exclusionRules,
            franchiseRules,
            editionRanks,
            nameOverrides,
            new HashSet<string>(StringComparer.Ordinal));

    public static IReadOnlyList<CanonicalGame> Canonicalize(
        IReadOnlyList<EntitlementSnapshot> snapshots,
        IReadOnlyList<ExclusionRule> exclusionRules,
        IReadOnlyList<FranchiseRule> franchiseRules,
        IReadOnlyDictionary<string, int> editionRanks,
        IReadOnlyDictionary<string, string> nameOverrides,
        IReadOnlySet<string> excludedConceptIds)
    {
        var nonGameTitles = TitlesClassifiedEntirelyAsNonGame(snapshots);
        var groups = new List<KeyValuePair<string, IReadOnlyList<GroupedEntry>>>();
        var groupIndexByKey = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var snapshot in snapshots)
        {
            if (!string.IsNullOrWhiteSpace(snapshot.ConceptId) && excludedConceptIds.Contains(snapshot.ConceptId))
            {
                continue;
            }

            if (TitlePlatform.IsNonTitleEntitlement(snapshot.TitleId)
                || snapshot.IsGame == false
                || (snapshot.PackageType is { } packageType && NonGamePackageTypes.Contains(packageType))
                || (snapshot.PackageType is null && snapshot.TitleId is not null && nonGameTitles.Contains(snapshot.TitleId)))
            {
                continue;
            }

            var exclusionName = NormalizeName(snapshot.GameMetaName)
                ?? NormalizeName(snapshot.TitleMetaName);
            if (exclusionName is not null && ExclusionRules.ShouldExclude(exclusionName, exclusionRules))
            {
                continue;
            }

            var conceptId = snapshot.ConceptId;
            var overriddenName = conceptId is null ? null : nameOverrides.GetValueOrDefault(conceptId);
            var name = NormalizeName(
                FirstNonEmpty(overriddenName, snapshot.TitleMetaName, snapshot.GameMetaName));
            if (name is null)
            {
                continue;
            }

            var entry = new GroupedEntry(
                name,
                snapshot.PackageType,
                conceptId,
                snapshot.ProductId,
                snapshot.EntitlementId,
                snapshot.Active,
                snapshot.TitleId)
            {
                Platforms = ResolvePlatforms(snapshot),
            };

            var key = conceptId ?? name;
            if (groupIndexByKey.TryGetValue(key, out var existing))
            {
                ((List<GroupedEntry>)groups[existing].Value).Add(entry);
            }
            else
            {
                groupIndexByKey[key] = groups.Count;
                groups.Add(new KeyValuePair<string, IReadOnlyList<GroupedEntry>>(key, new List<GroupedEntry> { entry }));
            }
        }

        return MergeService.MergeByProductIdAndName(groups)
            .Select(entries => ToCanonicalGame(entries, franchiseRules, editionRanks))
            .OrderBy(game => game.CanonicalTitle.ToLowerInvariant(), StringComparer.Ordinal)
            .ToList();
    }

    [GeneratedRegex("[™®©]")]
    private static partial Regex TrademarkSymbols();

    [GeneratedRegex(@"TM\b")]
    private static partial Regex TrademarkLetters();

    [GeneratedRegex(@"\(\s*\)")]
    private static partial Regex EmptyParentheses();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private static string? FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));

    private static HashSet<string> TitlesClassifiedEntirelyAsNonGame(IReadOnlyList<EntitlementSnapshot> snapshots)
    {
        var typesByTitleId = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
        {
            if (string.IsNullOrWhiteSpace(snapshot.TitleId) || snapshot.PackageType is null)
            {
                continue;
            }

            if (!typesByTitleId.TryGetValue(snapshot.TitleId, out var types))
            {
                types = new HashSet<string>(StringComparer.Ordinal);
                typesByTitleId[snapshot.TitleId] = types;
            }

            types.Add(snapshot.PackageType);
        }

        return typesByTitleId
            .Where(entry => entry.Value.IsSubsetOf(NonGamePackageTypes))
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IReadOnlySet<string> ResolvePlatforms(EntitlementSnapshot snapshot)
    {
        var fromAttributes = snapshot.PlatformIds
            .Select(TitlePlatform.NormalizePlatformId)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        if (fromAttributes.Count > 0)
        {
            return fromAttributes;
        }

        var fromTitleId = TitlePlatform.PlatformForTitleId(snapshot.TitleId);
        return fromTitleId is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal) { fromTitleId };
    }

    private static CanonicalGame ToCanonicalGame(
        IReadOnlyList<GroupedEntry> entries,
        IReadOnlyList<FranchiseRule> franchiseRules,
        IReadOnlyDictionary<string, int> editionRanks)
    {
        var hasPs4 = entries.Any(entry =>
            string.Equals(entry.PackageType, "PS4GD", StringComparison.Ordinal));
        var winner = entries[0];
        var winningKey = EditionSortKey(winner, editionRanks);
        foreach (var candidate in entries.Skip(1))
        {
            var key = EditionSortKey(candidate, editionRanks);
            if (key.CompareTo(winningKey) < 0)
            {
                winner = candidate;
                winningKey = key;
            }
        }

        return new CanonicalGame(
            winner.Name,
            string.Equals(winner.PackageType, "PSGD", StringComparison.Ordinal),
            hasPs4,
            FranchiseAssigner.AssignFranchise(winner.Name, franchiseRules),
            winner.ProductId,
            entries
                .Select(entry => entry.ConceptId)
                .OfType<string>()
                .Where(conceptId => conceptId.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(conceptId => conceptId, StringComparer.Ordinal)
                .ToList(),
            winner.EntitlementId)
        {
            Active = entries.Any(entry => entry.Active != false),
            WinningTitleId = winner.TitleId,
            Platforms = entries
                .SelectMany(entry => entry.Platforms)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(platform => platform, StringComparer.Ordinal)
                .ToList(),
        };
    }

    private static (int Inactive, int NotPs5Native, int Edition) EditionSortKey(
        GroupedEntry entry,
        IReadOnlyDictionary<string, int> editionRanks) =>
        (entry.Active != false ? 0 : 1,
         string.Equals(entry.PackageType, "PSGD", StringComparison.Ordinal) ? 0 : 1,
         EditionRank(entry.Name, editionRanks));
}
