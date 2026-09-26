namespace Functions.Churches;

using JetBrains.Annotations;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal readonly record struct MinistryRow(Guid Id, string Name, string? Description);
