namespace Functions.Churches;

using JetBrains.Annotations;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal readonly record struct ChurchAttributeRow(Guid Id, string Key, string Value, string Source, decimal Confidence);
