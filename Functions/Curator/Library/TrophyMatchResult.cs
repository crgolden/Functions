namespace Functions.Curator.Library;

public sealed record TrophyMatchResult(
    int ExactMatchedCount,
    int FuzzyMatchedCount,
    int AttemptedCount,
    int ProgressUpdatedCount = 0);
