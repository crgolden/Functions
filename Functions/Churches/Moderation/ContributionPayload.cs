namespace Functions.Churches.Moderation;

internal sealed record ContributionPayload(
    Guid ChurchId,
    Guid UserId,
    string Field,
    string? OldValue,
    string NewValue);
