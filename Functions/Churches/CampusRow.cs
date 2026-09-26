namespace Functions.Churches;

using JetBrains.Annotations;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal readonly record struct CampusRow(Guid Id, string Name, string? Street, string City, string State, string Zip, double Latitude, double Longitude);
