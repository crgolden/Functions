namespace Functions.Churches.Moderation;

using JetBrains.Annotations;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal readonly record struct MergeSuggestionRow(Guid Id, Guid ChurchId, string NewValue);
