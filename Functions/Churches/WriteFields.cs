namespace Functions.Churches;

internal readonly record struct WriteFields(decimal Lat, decimal Lng, string Slug, DateTimeOffset Now, Guid? DenominationId);
