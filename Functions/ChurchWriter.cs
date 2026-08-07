namespace Functions;

using System.Data;
using System.Data.Common;
using System.Globalization;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Azure;

public sealed class ChurchWriter
{
    private const string ChurchIdParam = "@ChurchId";
    private const string NameParam = "@Name";

    private const int CanonicalNameMaxLength = 300;
    private const int SlugMaxLength = 320;
    private const int SlugSuffixReserve = 10;
    private const int StreetMaxLength = 200;
    private const int CityMaxLength = 100;
    private const int ZipMaxLength = 10;
    private const int PhoneMaxLength = 20;
    private const int WebsiteMaxLength = 500;
    private const int EmailMaxLength = 254;
    private const int PrimaryLanguageMaxLength = 50;
    private const int CampusNameMaxLength = 200;
    private const int MinistryNameMaxLength = 200;
    private const int MinistryDescriptionMaxLength = 1000;
    private const int AttributeKeyMaxLength = 100;
    private const int AttributeValueMaxLength = 1000;
    private const int AttributeSourceMaxLength = 100;
    private const int ServiceScheduleDescriptionMaxLength = 200;

    private readonly DbConnection _dbConnection;
    private readonly ServiceBusClient _serviceBusClient;

    public ChurchWriter(DbConnection dbConnection, IAzureClientFactory<ServiceBusClient> serviceBusClientFactory)
    {
        _dbConnection = dbConnection;
        _serviceBusClient = serviceBusClientFactory.CreateClient("crgolden");
    }

    public async Task UpsertAsync(GeocodingRequest req, decimal lat, decimal lng, CancellationToken ct)
    {
        req = SanitizeLengths(req);
        if (_dbConnection.State == ConnectionState.Closed)
        {
            await _dbConnection.OpenAsync(ct);
        }

        var writtenChurchId = Guid.Empty;
        await using var tx = await _dbConnection.BeginTransactionAsync(ct);
        try
        {
            await using var lookupCmd = _dbConnection.CreateCommand();
            lookupCmd.Transaction = tx;
            lookupCmd.CommandText = "SELECT [ChurchId] FROM [dbo].[CrawlSources] WHERE [Id] = @Id";
            AddParam(lookupCmd, "@Id", req.CrawlSourceId);
            var existingIdObj = await lookupCmd.ExecuteScalarAsync(ct);
            var isNew = existingIdObj is not Guid;
            var churchId = existingIdObj is Guid g ? g : Guid.CreateVersion7(DateTimeOffset.UtcNow);
            var now = DateTimeOffset.UtcNow.UtcDateTime;
            var rawBaseSlug = SlugHelper.ToSlug(req.CanonicalName ?? string.Empty)
                           + "-" + SlugHelper.ToSlug(req.City ?? string.Empty)
                           + "-" + (req.State ?? string.Empty).ToLowerInvariant().Trim();
            var baseSlug = Truncate(rawBaseSlug, SlugMaxLength - SlugSuffixReserve);
            var slug = await GenerateUniqueSlugAsync(tx, baseSlug, churchId, ct);
            var denominationId = await ResolveDenominationIdAsync(tx, req.DenominationName, ct);
            var fields = new WriteFields(lat, lng, slug, now, denominationId);
            EnsureValid(churchId, req, fields);

            if (isNew)
            {
                if (await DuplicateExistsAsync(tx, req, lat, lng, ct))
                {
                    await tx.CommitAsync(ct);
                    return;
                }

                await using var insertCmd = _dbConnection.CreateCommand();
                insertCmd.Transaction = tx;
                insertCmd.CommandText = """
                    INSERT INTO [dbo].[Churches]
                        ([Id], [CanonicalName], [Slug], [Latitude], [Longitude], [Street], [City], [State], [Zip],
                         [PhoneNumber], [Website], [EmailAddress], [DenominationId], [WorshipStyle], [PrimaryLanguage],
                         [AcceptsLGBTQ], [WheelchairAccessible], [HasNursery], [HasYouthProgram],
                         [ConfidenceScore], [CreatedAt], [UpdatedAt], [IsActive])
                    VALUES (@Id, @Name, @Slug, @Lat, @Lng, @Street, @City, @State, @Zip,
                            @Phone, @Website, @Email, @Denom, @Ws, @Lang, @Lgbtq, @Wa, @Nursery, @Youth, @Score, @Now, @Now, 1)
                    """;
                BindAll(insertCmd, churchId, req, fields);
                await insertCmd.ExecuteNonQueryAsync(ct);

                await using var linkCmd = _dbConnection.CreateCommand();
                linkCmd.Transaction = tx;
                linkCmd.CommandText = "UPDATE [dbo].[CrawlSources] SET [ChurchId] = @ChurchId WHERE [Id] = @Id";
                AddParam(linkCmd, ChurchIdParam, churchId);
                AddParam(linkCmd, "@Id", req.CrawlSourceId);
                await linkCmd.ExecuteNonQueryAsync(ct);

                await WriteAttributesAsync(tx, churchId, req.Attributes, now, ct);
                await RegisterCrawlSourceAsync(tx, churchId, req.Website, now, ct);
                await WriteServiceSchedulesAsync(tx, churchId, req.ServiceSchedules, now, ct);
                await WriteMinistriesAsync(tx, churchId, req.Ministries, now, ct);
                await WriteCampusesAsync(tx, churchId, req.Campuses, now, ct);
                writtenChurchId = churchId;
            }
            else
            {
                await using var updateCmd = _dbConnection.CreateCommand();
                updateCmd.Transaction = tx;
                updateCmd.CommandText = """
                    UPDATE [dbo].[Churches]
                    SET [CanonicalName] = @Name, [Slug] = @Slug, [Latitude] = @Lat, [Longitude] = @Lng,
                        [Street] = @Street, [City] = @City, [State] = @State, [Zip] = @Zip,
                        [PhoneNumber] = @Phone, [Website] = @Website, [EmailAddress] = @Email,
                        [DenominationId] = @Denom, [WorshipStyle] = @Ws, [PrimaryLanguage] = @Lang, [AcceptsLGBTQ] = @Lgbtq,
                        [WheelchairAccessible] = @Wa, [HasNursery] = @Nursery, [HasYouthProgram] = @Youth,
                        [ConfidenceScore] = @Score, [UpdatedAt] = @Now
                    WHERE [Id] = @Id
                    """;
                BindAll(updateCmd, churchId, req, fields);
                await updateCmd.ExecuteNonQueryAsync(ct);

                await WriteAttributesAsync(tx, churchId, req.Attributes, now, ct);
                await WriteServiceSchedulesAsync(tx, churchId, req.ServiceSchedules, now, ct);
                await WriteMinistriesAsync(tx, churchId, req.Ministries, now, ct);
                await WriteCampusesAsync(tx, churchId, req.Campuses, now, ct);
                writtenChurchId = churchId;
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        if (writtenChurchId != Guid.Empty)
        {
            await PublishConfidenceRequestAsync(writtenChurchId, ct);
        }
    }

    public async Task<bool> UpdateCoordinatesAsync(Guid churchId, decimal lat, decimal lng, CancellationToken ct)
    {
        if (_dbConnection.State == ConnectionState.Closed)
        {
            await _dbConnection.OpenAsync(ct);
        }

        int affected;
        await using (var cmd = _dbConnection.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE [dbo].[Churches]
                SET [Latitude] = @Lat, [Longitude] = @Lng, [UpdatedAt] = @Now
                WHERE [Id] = @Id
                """;
            AddParam(cmd, "@Lat", lat);
            AddParam(cmd, "@Lng", lng);
            AddParam(cmd, "@Now", DateTimeOffset.UtcNow.UtcDateTime);
            AddParam(cmd, "@Id", churchId);
            affected = await cmd.ExecuteNonQueryAsync(ct);
        }

        if (affected > 0)
        {
            await PublishConfidenceRequestAsync(churchId, ct);
        }

        return affected > 0;
    }

    private static void EnsureValid(Guid id, GeocodingRequest req, WriteFields fields) =>
        new Shared.Domain.ChurchBuilder()
            .WithId(id)
            .WithCanonicalName(req.CanonicalName ?? string.Empty)
            .WithSlug(fields.Slug)
            .WithLatitude((double)fields.Lat)
            .WithLongitude((double)fields.Lng)
            .WithStreet(req.Street)
            .WithCity(req.City ?? string.Empty)
            .WithState(req.State ?? string.Empty)
            .WithZip(Normalizer.NormalizeZip(req.Zip) ?? req.Zip ?? string.Empty)
            .WithPhoneNumber(Normalizer.NormalizePhone(req.PhoneNumber))
            .WithWebsite(Normalizer.NormalizeUrl(req.Website))
            .WithEmailAddress(req.EmailAddress)
            .WithDenominationId(fields.DenominationId)
            .WithWorshipStyle(req.WorshipStyle)
            .WithPrimaryLanguage(req.PrimaryLanguage)
            .WithAcceptsLGBTQ(req.AcceptsLGBTQ)
            .WithWheelchairAccessible(req.WheelchairAccessible)
            .WithHasNursery(req.HasNursery)
            .WithHasYouthProgram(req.HasYouthProgram)
            .WithConfidenceScore(req.Confidence)
            .WithCreatedAt(fields.Now)
            .WithUpdatedAt(fields.Now)
            .Build();

    private static void BindAll(DbCommand cmd, Guid id, GeocodingRequest req, WriteFields fields)
    {
        AddParam(cmd, "@Id", id);
        AddParam(cmd, "@Denom", fields.DenominationId.HasValue ? fields.DenominationId.Value : DBNull.Value);
        AddParam(cmd, NameParam, (object?)req.CanonicalName ?? DBNull.Value);
        AddParam(cmd, "@Slug", fields.Slug);
        AddParam(cmd, "@Lat", fields.Lat);
        AddParam(cmd, "@Lng", fields.Lng);
        AddParam(cmd, "@Street", (object?)req.Street ?? DBNull.Value);
        AddParam(cmd, "@City", (object?)req.City ?? DBNull.Value);
        AddParam(cmd, "@State", (object?)req.State ?? DBNull.Value);

        AddParam(cmd, "@Zip", (object?)(Normalizer.NormalizeZip(req.Zip) ?? req.Zip) ?? DBNull.Value);
        AddParam(cmd, "@Phone", (object?)Normalizer.NormalizePhone(req.PhoneNumber) ?? DBNull.Value);
        AddParam(cmd, "@Website", (object?)Normalizer.NormalizeUrl(req.Website) ?? DBNull.Value);
        AddParam(cmd, "@Email", (object?)req.EmailAddress ?? DBNull.Value);
        AddParam(cmd, "@Ws", req.WorshipStyle);
        AddParam(cmd, "@Lang", req.PrimaryLanguage);
        AddParam(cmd, "@Lgbtq", req.AcceptsLGBTQ.HasValue ? req.AcceptsLGBTQ.Value : DBNull.Value);
        AddParam(cmd, "@Wa", req.WheelchairAccessible.HasValue ? req.WheelchairAccessible.Value : DBNull.Value);
        AddParam(cmd, "@Nursery", req.HasNursery.HasValue ? req.HasNursery.Value : DBNull.Value);
        AddParam(cmd, "@Youth", req.HasYouthProgram.HasValue ? req.HasYouthProgram.Value : DBNull.Value);
        AddParam(cmd, "@Score", req.Confidence);
        AddParam(cmd, "@Now", fields.Now);
    }

    private static void AddParam(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    private static GeocodingRequest SanitizeLengths(GeocodingRequest req) => req with
    {
        CanonicalName = TruncateNullable(req.CanonicalName, CanonicalNameMaxLength),
        Street = TruncateNullable(req.Street, StreetMaxLength),
        City = TruncateNullable(req.City, CityMaxLength),
        Zip = TruncateNullable(Normalizer.NormalizeZip(req.Zip) ?? req.Zip, ZipMaxLength),
        PhoneNumber = TruncateNullable(Normalizer.NormalizePhone(req.PhoneNumber), PhoneMaxLength),
        Website = TruncateNullable(Normalizer.NormalizeUrl(req.Website), WebsiteMaxLength),
        EmailAddress = TruncateNullable(req.EmailAddress, EmailMaxLength),
        PrimaryLanguage = Truncate(req.PrimaryLanguage, PrimaryLanguageMaxLength),
        Attributes = req.Attributes
            .Select(a => a with
            {
                Key = Truncate(a.Key, AttributeKeyMaxLength),
                Value = Truncate(a.Value, AttributeValueMaxLength),
                Source = Truncate(a.Source, AttributeSourceMaxLength),
            })
            .ToList(),
        ServiceSchedules = req.ServiceSchedules
            .Select(s => s with { Description = TruncateNullable(s.Description, ServiceScheduleDescriptionMaxLength) })
            .ToList(),
        Ministries = req.Ministries
            .Select(m => m with
            {
                Name = Truncate(m.Name, MinistryNameMaxLength),
                Description = TruncateNullable(m.Description, MinistryDescriptionMaxLength),
            })
            .ToList(),
        Campuses = req.Campuses
            .Select(c => c with
            {
                Name = Truncate(c.Name, CampusNameMaxLength),
                Street = TruncateNullable(c.Street, StreetMaxLength),
                City = Truncate(c.City, CityMaxLength),
                Zip = Truncate(c.Zip, ZipMaxLength),
            })
            .ToList(),
    };

    private static string Truncate(string value, int maxLength) =>
        value.Length > maxLength ? value[..maxLength] : value;

    private static string? TruncateNullable(string? value, int maxLength) =>
        value is null ? null : Truncate(value, maxLength);

    private async Task WriteAttributesAsync(DbTransaction tx, Guid churchId, IReadOnlyList<ChurchAttributeData> attributes, DateTime now, CancellationToken ct)
    {
        if (attributes.Count == 0)
        {
            return;
        }

        foreach (var src in attributes.Select(a => a.Source).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await using var deleteCmd = _dbConnection.CreateCommand();
            deleteCmd.Transaction = tx;
            deleteCmd.CommandText = "DELETE FROM [dbo].[ChurchAttributes] WHERE [ChurchId] = @Id AND [Source] = @Source";
            AddParam(deleteCmd, "@Id", churchId);
            AddParam(deleteCmd, "@Source", src);
            await deleteCmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var attribute in attributes)
        {
            await using var insertCmd = _dbConnection.CreateCommand();
            insertCmd.Transaction = tx;
            insertCmd.CommandText = """
                INSERT INTO [dbo].[ChurchAttributes] ([Id], [ChurchId], [Key], [Value], [Source], [Confidence], [CreatedAt], [UpdatedAt])
                VALUES (@Id, @ChurchId, @Key, @Value, @Source, @Confidence, @Now, @Now)
                """;
            AddParam(insertCmd, "@Id", Guid.CreateVersion7(DateTimeOffset.UtcNow));
            AddParam(insertCmd, ChurchIdParam, churchId);
            AddParam(insertCmd, "@Key", attribute.Key);
            AddParam(insertCmd, "@Value", attribute.Value);
            AddParam(insertCmd, "@Source", attribute.Source);
            AddParam(insertCmd, "@Confidence", attribute.Confidence);
            AddParam(insertCmd, "@Now", now);
            await insertCmd.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task WriteServiceSchedulesAsync(DbTransaction tx, Guid churchId, IReadOnlyList<ServiceScheduleData> schedules, DateTime now, CancellationToken ct)
    {
        if (schedules.Count == 0)
        {
            return;
        }

        var parsed = new List<(byte Day, TimeSpan Time, string? Description)>();
        foreach (var schedule in schedules)
        {
            if (schedule.DayOfWeek <= 6 && TimeOnly.TryParse(schedule.StartTime, CultureInfo.InvariantCulture, out var time))
            {
                parsed.Add((schedule.DayOfWeek, time.ToTimeSpan(), schedule.Description));
            }
        }

        if (parsed.Count == 0)
        {
            return;
        }

        await using (var deleteCmd = _dbConnection.CreateCommand())
        {
            deleteCmd.Transaction = tx;
            deleteCmd.CommandText = "DELETE FROM [dbo].[ServiceSchedules] WHERE [ChurchId] = @Id";
            AddParam(deleteCmd, "@Id", churchId);
            await deleteCmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var (day, time, description) in parsed)
        {
            await using var insertCmd = _dbConnection.CreateCommand();
            insertCmd.Transaction = tx;
            insertCmd.CommandText = """
                INSERT INTO [dbo].[ServiceSchedules] ([Id], [ChurchId], [DayOfWeek], [StartTime], [Description], [CreatedAt], [UpdatedAt])
                VALUES (@Id, @ChurchId, @Day, @Start, @Desc, @Now, @Now)
                """;
            AddParam(insertCmd, "@Id", Guid.CreateVersion7(DateTimeOffset.UtcNow));
            AddParam(insertCmd, ChurchIdParam, churchId);
            AddParam(insertCmd, "@Day", day);
            AddParam(insertCmd, "@Start", time);
            AddParam(insertCmd, "@Desc", (object?)description ?? DBNull.Value);
            AddParam(insertCmd, "@Now", now);
            await insertCmd.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task WriteMinistriesAsync(DbTransaction tx, Guid churchId, IReadOnlyList<MinistryData> ministries, DateTime now, CancellationToken ct)
    {
        if (ministries.Count == 0)
        {
            return;
        }

        var named = ministries.Where(m => !string.IsNullOrWhiteSpace(m.Name)).ToList();
        if (named.Count == 0)
        {
            return;
        }

        await using (var deleteCmd = _dbConnection.CreateCommand())
        {
            deleteCmd.Transaction = tx;
            deleteCmd.CommandText = "DELETE FROM [dbo].[Ministries] WHERE [ChurchId] = @Id";
            AddParam(deleteCmd, "@Id", churchId);
            await deleteCmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var ministry in named)
        {
            await using var insertCmd = _dbConnection.CreateCommand();
            insertCmd.Transaction = tx;
            insertCmd.CommandText = """
                INSERT INTO [dbo].[Ministries] ([Id], [ChurchId], [Name], [Description], [CreatedAt], [UpdatedAt])
                VALUES (@Id, @ChurchId, @Name, @Desc, @Now, @Now)
                """;
            AddParam(insertCmd, "@Id", Guid.CreateVersion7(DateTimeOffset.UtcNow));
            AddParam(insertCmd, ChurchIdParam, churchId);
            AddParam(insertCmd, NameParam, ministry.Name);
            AddParam(insertCmd, "@Desc", (object?)ministry.Description ?? DBNull.Value);
            AddParam(insertCmd, "@Now", now);
            await insertCmd.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task WriteCampusesAsync(DbTransaction tx, Guid churchId, IReadOnlyList<CampusData> campuses, DateTime now, CancellationToken ct)
    {
        if (campuses.Count == 0)
        {
            return;
        }

        var valid = campuses
            .Where(c => !string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.City)
                && !string.IsNullOrWhiteSpace(c.State) && !string.IsNullOrWhiteSpace(c.Zip))
            .ToList();
        if (valid.Count == 0)
        {
            return;
        }

        await using (var deleteCmd = _dbConnection.CreateCommand())
        {
            deleteCmd.Transaction = tx;
            deleteCmd.CommandText = "DELETE FROM [dbo].[Campuses] WHERE [ChurchId] = @Id";
            AddParam(deleteCmd, "@Id", churchId);
            await deleteCmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var campus in valid)
        {
            await using var insertCmd = _dbConnection.CreateCommand();
            insertCmd.Transaction = tx;
            insertCmd.CommandText = """
                INSERT INTO [dbo].[Campuses] ([Id], [ChurchId], [Name], [Street], [City], [State], [Zip], [Latitude], [Longitude], [CreatedAt], [UpdatedAt])
                VALUES (@Id, @ChurchId, @Name, @Street, @City, @State, @Zip, @Lat, @Lng, @Now, @Now)
                """;
            AddParam(insertCmd, "@Id", Guid.CreateVersion7(DateTimeOffset.UtcNow));
            AddParam(insertCmd, ChurchIdParam, churchId);
            AddParam(insertCmd, NameParam, campus.Name);
            AddParam(insertCmd, "@Street", (object?)campus.Street ?? DBNull.Value);
            AddParam(insertCmd, "@City", campus.City);
            AddParam(insertCmd, "@State", campus.State);
            AddParam(insertCmd, "@Zip", campus.Zip);
            AddParam(insertCmd, "@Lat", (double)(campus.Latitude ?? 0m));
            AddParam(insertCmd, "@Lng", (double)(campus.Longitude ?? 0m));
            AddParam(insertCmd, "@Now", now);
            await insertCmd.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task RegisterCrawlSourceAsync(DbTransaction tx, Guid churchId, string? website, DateTime now, CancellationToken ct)
    {
        var url = Normalizer.NormalizeUrl(website);
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        await using var existsCmd = _dbConnection.CreateCommand();
        existsCmd.Transaction = tx;
        existsCmd.CommandText = "SELECT COUNT(1) FROM [dbo].[CrawlSources] WHERE [Url] = @Url";
        AddParam(existsCmd, "@Url", url);
        if (await existsCmd.ExecuteScalarAsync(ct) is > 0)
        {
            return;
        }

        await using var insertCmd = _dbConnection.CreateCommand();
        insertCmd.Transaction = tx;
        insertCmd.CommandText = """
            INSERT INTO [dbo].[CrawlSources] ([Id], [ChurchId], [Url], [LastStatus], [CreatedAt], [UpdatedAt])
            VALUES (@Id, @ChurchId, @Url, 0, @Now, @Now)
            """;
        AddParam(insertCmd, "@Id", Guid.CreateVersion7(DateTimeOffset.UtcNow));
        AddParam(insertCmd, ChurchIdParam, churchId);
        AddParam(insertCmd, "@Url", url);
        AddParam(insertCmd, "@Now", now);
        await insertCmd.ExecuteNonQueryAsync(ct);
    }

    private async Task PublishConfidenceRequestAsync(Guid churchId, CancellationToken ct)
    {
        await using var sender = _serviceBusClient.CreateSender("confidence-requests");
        var body = BinaryData.FromObjectAsJson(new ConfidenceRequest(churchId));
        await sender.SendMessageAsync(new ServiceBusMessage(body), ct);
    }

    private async Task<string> GenerateUniqueSlugAsync(DbTransaction tx, string baseSlug, Guid churchId, CancellationToken ct)
    {
        var candidate = baseSlug;
        var suffix = 2;
        while (await SlugExistsAsync(tx, candidate, churchId, ct))
        {
            candidate = $"{baseSlug}-{suffix}";
            suffix++;
        }

        return candidate;
    }

    private async Task<Guid?> ResolveDenominationIdAsync(DbTransaction tx, string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        await using var cmd = _dbConnection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT [Id] FROM [dbo].[Denominations] WHERE [Name] = @Name";
        AddParam(cmd, NameParam, name);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is Guid g ? g : null;
    }

    private async Task<bool> DuplicateExistsAsync(DbTransaction tx, GeocodingRequest req, decimal lat, decimal lng, CancellationToken ct)
    {
        await using var cmd = _dbConnection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT COUNT(1) FROM [dbo].[Churches]
            WHERE [CanonicalName] = @Name AND [City] = @City AND [State] = @State
              AND [Latitude] = @Lat AND [Longitude] = @Lng
            """;
        AddParam(cmd, NameParam, (object?)req.CanonicalName ?? DBNull.Value);
        AddParam(cmd, "@City", (object?)req.City ?? DBNull.Value);
        AddParam(cmd, "@State", (object?)req.State ?? DBNull.Value);
        AddParam(cmd, "@Lat", lat);
        AddParam(cmd, "@Lng", lng);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is > 0;
    }

    private async Task<bool> SlugExistsAsync(DbTransaction tx, string slug, Guid excludeChurchId, CancellationToken ct)
    {
        await using var cmd = _dbConnection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(1) FROM [dbo].[Churches] WHERE [Slug] = @Slug AND [Id] <> @ExcludeId";
        AddParam(cmd, "@Slug", slug);
        AddParam(cmd, "@ExcludeId", excludeChurchId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is > 0;
    }
}

internal readonly record struct WriteFields(decimal Lat, decimal Lng, string Slug, DateTime Now, Guid? DenominationId);