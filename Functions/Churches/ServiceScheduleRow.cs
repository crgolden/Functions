namespace Functions.Churches;

using JetBrains.Annotations;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal readonly record struct ServiceScheduleRow(Guid Id, byte DayOfWeek, TimeSpan StartTime, string? Description);
