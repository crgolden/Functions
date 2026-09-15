namespace Functions.Churches;

using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Extensions;
using Shared.Domain;

public sealed class ChurchWriter
{
    internal const int CanonicalNameMaxLength = 300;
    internal const int SlugMaxLength = 320;
    internal const int SlugSuffixReserve = 10;
    internal const int StreetMaxLength = 200;
    internal const int CityMaxLength = 100;
    internal const int ZipMaxLength = 10;
    internal const int PhoneMaxLength = 20;
    internal const int WebsiteMaxLength = 500;
    internal const int EmailMaxLength = 254;
    internal const int PrimaryLanguageMaxLength = 50;
    internal const int CampusNameMaxLength = 200;
    internal const int MinistryNameMaxLength = 200;
    internal const int MinistryDescriptionMaxLength = 1000;
    internal const int AttributeKeyMaxLength = 100;
    internal const int AttributeValueMaxLength = 1000;
    internal const int AttributeSourceMaxLength = 100;
    internal const int ServiceScheduleDescriptionMaxLength = 200;

    private const string ChurchIdParam = "@ChurchId";
    private const string NameParam = "@Name";
    private const string MissingChurchFieldMessage = "A church record requires this field.";
    private const string RowsParam = "@Rows";
    private const string SourcesParam = "@Sources";

    private const string ReplaceAttributesSql = """
        DELETE FROM [dbo].[ChurchAttributes]
        WHERE [ChurchId] = @ChurchId AND [Source] IN (SELECT [value] FROM OPENJSON(@Sources));
        INSERT INTO [dbo].[ChurchAttributes] ([Id], [ChurchId], [Key], [Value], [Source], [Confidence], [CreatedAt], [UpdatedAt])
        SELECT [Row].[Id], @ChurchId, [Row].[Key], [Row].[Value], [Row].[Source], [Row].[Confidence], @Now, @Now
        FROM OPENJSON(@Rows)
        WITH ([Id] UNIQUEIDENTIFIER, [Key] NVARCHAR (100), [Value] NVARCHAR (1000), [Source] NVARCHAR (100), [Confidence] DECIMAL (5, 4)) AS [Row];
        """;

    private const string ReplaceServiceSchedulesSql = """
        DELETE FROM [dbo].[ServiceSchedules] WHERE [ChurchId] = @ChurchId;
        INSERT INTO [dbo].[ServiceSchedules] ([Id], [ChurchId], [DayOfWeek], [StartTime], [Description], [CreatedAt], [UpdatedAt])
        SELECT [Row].[Id], @ChurchId, [Row].[DayOfWeek], [Row].[StartTime], [Row].[Description], @Now, @Now
        FROM OPENJSON(@Rows)
        WITH ([Id] UNIQUEIDENTIFIER, [DayOfWeek] TINYINT, [StartTime] TIME (0), [Description] NVARCHAR (200)) AS [Row];
        """;

    private const string ReplaceMinistriesSql = """
        DELETE FROM [dbo].[Ministries] WHERE [ChurchId] = @ChurchId;
        INSERT INTO [dbo].[Ministries] ([Id], [ChurchId], [Name], [Description], [CreatedAt], [UpdatedAt])
        SELECT [Row].[Id], @ChurchId, [Row].[Name], [Row].[Description], @Now, @Now
        FROM OPENJSON(@Rows)
        WITH ([Id] UNIQUEIDENTIFIER, [Name] NVARCHAR (200), [Description] NVARCHAR (1000)) AS [Row];
        """;

    private const string ReplaceCampusesSql = """
        DELETE FROM [dbo].[Campuses] WHERE [ChurchId] = @ChurchId;
        INSERT INTO [dbo].[Campuses] ([Id], [ChurchId], [Name], [Street], [City], [State], [Zip], [Latitude], [Longitude], [CreatedAt], [UpdatedAt])
        SELECT [Row].[Id], @ChurchId, [Row].[Name], [Row].[Street], [Row].[City], [Row].[State], [Row].[Zip], [Row].[Latitude], [Row].[Longitude], @Now, @Now
        FROM OPENJSON(@Rows)
        WITH ([Id] UNIQUEIDENTIFIER, [Name] NVARCHAR (200), [Street] NVARCHAR (200), [City] NVARCHAR (100), [State] NCHAR (2), [Zip] NVARCHAR (10), [Latitude] FLOAT, [Longitude] FLOAT) AS [Row];
        """;

    private static readonly ConcurrentDictionary<string, Guid> DenominationIdsByName = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions ChildRowFormat = new();

    private readonly DbConnection _dbConnection;
    private readonly ChurchQueueSenders _senders;

    public ChurchWriter(DbConnection dbConnection, ChurchQueueSenders senders)
    {
        _dbConnection = dbConnection;
        _senders = senders;
    }

    public async Task UpsertAsync(GeocodingRequest req, decimal lat, decimal lng, CancellationToken ct)
    {
        req = SanitizeLengths(req);
        if (_dbConnection.State == ConnectionState.Closed)
        {
            await _dbConnection.OpenAsync(ct);
        }

        Guid writtenChurchId;
        await using var tx = await _dbConnection.BeginTransactionAsync(ct);
        try
        {
            await using var lookupCmd = _dbConnection.CreateCommand();
            lookupCmd.Transaction = tx;
            lookupCmd.CommandText = "SELECT [ChurchId] FROM [dbo].[CrawlSources] WHERE [Id] = @Id";
            lookupCmd.AddParam("@Id", req.CrawlSourceId);
            var existingIdObj = await lookupCmd.ExecuteScalarAsync(ct);
            var isNew = existingIdObj is not Guid;
            var churchId = existingIdObj is Guid g ? g : Guid.CreateVersion7(DateTimeOffset.UtcNow);
            var now = DateTimeOffset.UtcNow;
            var text = RequireTextFields(req);
            var rawBaseSlug = SlugHelper.ToSlug(text.CanonicalName)
                           + "-" + SlugHelper.ToSlug(text.City)
                           + "-" + text.State.ToLowerInvariant().Trim();
            var baseSlug = Truncate(rawBaseSlug, SlugMaxLength - SlugSuffixReserve);
            var slug = await GenerateUniqueSlugAsync(tx, baseSlug, churchId, ct);
            var denominationId = await ResolveDenominationIdAsync(tx, req.DenominationName, ct);
            var fields = new WriteFields(lat, lng, slug, now, denominationId);
            EnsureValid(churchId, req, text, fields);

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
                linkCmd.AddParam(ChurchIdParam, churchId);
                linkCmd.AddParam("@Id", req.CrawlSourceId);
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
            cmd.AddParam("@Lat", lat);
            cmd.AddParam("@Lng", lng);
            cmd.AddParam("@Now", DateTimeOffset.UtcNow);
            cmd.AddParam("@Id", churchId);
            affected = await cmd.ExecuteNonQueryAsync(ct);
        }

        if (affected > 0)
        {
            await PublishConfidenceRequestAsync(churchId, ct);
        }

        return affected > 0;
    }

    private static ChurchTextFields RequireTextFields(GeocodingRequest req) =>
        new(
            RequireText(req.CanonicalName, nameof(GeocodingRequest.CanonicalName)),
            RequireText(req.City, nameof(GeocodingRequest.City)),
            RequireText(req.State, nameof(GeocodingRequest.State)),
            RequireText(Normalizer.NormalizeZip(req.Zip) ?? req.Zip, nameof(GeocodingRequest.Zip)));

    private static string RequireText(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException(MissingChurchFieldMessage, parameterName)
            : value;

    private static void EnsureValid(Guid id, GeocodingRequest req, ChurchTextFields text, WriteFields fields) =>
        new ChurchBuilder()
            .WithId(id)
            .WithCanonicalName(text.CanonicalName)
            .WithSlug(fields.Slug)
            .WithLatitude((double)fields.Lat)
            .WithLongitude((double)fields.Lng)
            .WithStreet(req.Street)
            .WithCity(text.City)
            .WithState(text.State)
            .WithZip(text.Zip)
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
        cmd.AddParam("@Id", id);
        cmd.AddParam("@Denom", fields.DenominationId);
        cmd.AddParam(NameParam, (object?)req.CanonicalName ?? DBNull.Value);
        cmd.AddParam("@Slug", fields.Slug);
        cmd.AddParam("@Lat", fields.Lat);
        cmd.AddParam("@Lng", fields.Lng);
        cmd.AddParam("@Street", (object?)req.Street ?? DBNull.Value);
        cmd.AddParam("@City", (object?)req.City ?? DBNull.Value);
        cmd.AddParam("@State", (object?)req.State ?? DBNull.Value);

        cmd.AddParam("@Zip", (object?)(Normalizer.NormalizeZip(req.Zip) ?? req.Zip) ?? DBNull.Value);
        cmd.AddParam("@Phone", (object?)Normalizer.NormalizePhone(req.PhoneNumber) ?? DBNull.Value);
        cmd.AddParam("@Website", (object?)Normalizer.NormalizeUrl(req.Website) ?? DBNull.Value);
        cmd.AddParam("@Email", (object?)req.EmailAddress ?? DBNull.Value);
        cmd.AddParam("@Ws", req.WorshipStyle);
        cmd.AddParam("@Lang", req.PrimaryLanguage);
        cmd.AddParam("@Lgbtq", req.AcceptsLGBTQ);
        cmd.AddParam("@Wa", req.WheelchairAccessible);
        cmd.AddParam("@Nursery", req.HasNursery);
        cmd.AddParam("@Youth", req.HasYouthProgram);
        cmd.AddParam("@Score", req.Confidence);
        cmd.AddParam("@Now", fields.Now);
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
            .Select(m => new MinistryData(
                Truncate(m.Name, MinistryNameMaxLength),
                TruncateNullable(m.Description, MinistryDescriptionMaxLength)))
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

    private async Task WriteAttributesAsync(DbTransaction tx, Guid churchId, IReadOnlyList<ChurchAttributeData> attributes, DateTimeOffset now, CancellationToken ct)
    {
        if (attributes.Count == 0)
        {
            return;
        }

        var sources = attributes.Select(a => a.Source).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var rows = attributes
            .Select(a => new ChurchAttributeRow(Guid.CreateVersion7(DateTimeOffset.UtcNow), a.Key, a.Value, a.Source, a.Confidence))
            .ToList();
        await using var cmd = ChildRowsCommand(tx, ReplaceAttributesSql, churchId, now, rows);
        cmd.AddParam(SourcesParam, JsonSerializer.Serialize(sources, ChildRowFormat));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task WriteServiceSchedulesAsync(DbTransaction tx, Guid churchId, IReadOnlyList<ServiceScheduleData> schedules, DateTimeOffset now, CancellationToken ct)
    {
        if (schedules.Count == 0)
        {
            return;
        }

        var rows = new List<ServiceScheduleRow>();
        foreach (var schedule in schedules)
        {
            if (schedule.DayOfWeek <= 6 && TimeOnly.TryParse(schedule.StartTime, CultureInfo.InvariantCulture, out var time))
            {
                rows.Add(new ServiceScheduleRow(Guid.CreateVersion7(DateTimeOffset.UtcNow), schedule.DayOfWeek, time.ToTimeSpan(), schedule.Description));
            }
        }

        if (rows.Count == 0)
        {
            return;
        }

        await using var cmd = ChildRowsCommand(tx, ReplaceServiceSchedulesSql, churchId, now, rows);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task WriteMinistriesAsync(DbTransaction tx, Guid churchId, IReadOnlyList<MinistryData> ministries, DateTimeOffset now, CancellationToken ct)
    {
        if (ministries.Count == 0)
        {
            return;
        }

        var rows = ministries
            .Where(m => !string.IsNullOrWhiteSpace(m.Name))
            .Select(m => new MinistryRow(Guid.CreateVersion7(DateTimeOffset.UtcNow), m.Name, m.Description))
            .ToList();
        if (rows.Count == 0)
        {
            return;
        }

        await using var cmd = ChildRowsCommand(tx, ReplaceMinistriesSql, churchId, now, rows);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task WriteCampusesAsync(DbTransaction tx, Guid churchId, IReadOnlyList<CampusData> campuses, DateTimeOffset now, CancellationToken ct)
    {
        if (campuses.Count == 0)
        {
            return;
        }

        var rows = campuses
            .Where(c => !string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.City)
                && !string.IsNullOrWhiteSpace(c.State) && !string.IsNullOrWhiteSpace(c.Zip))
            .Select(c => new CampusRow(
                Guid.CreateVersion7(DateTimeOffset.UtcNow),
                c.Name,
                c.Street,
                c.City,
                c.State,
                c.Zip,
                (double)(c.Latitude ?? 0m),
                (double)(c.Longitude ?? 0m)))
            .ToList();
        if (rows.Count == 0)
        {
            return;
        }

        await using var cmd = ChildRowsCommand(tx, ReplaceCampusesSql, churchId, now, rows);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private DbCommand ChildRowsCommand<TRow>(DbTransaction tx, string replaceSql, Guid churchId, DateTimeOffset now, IReadOnlyList<TRow> rows)
    {
        var cmd = _dbConnection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = replaceSql;
        cmd.AddParam(ChurchIdParam, churchId);
        cmd.AddParam("@Now", now);
        cmd.AddParam(RowsParam, JsonSerializer.Serialize(rows, ChildRowFormat));
        return cmd;
    }

    private async Task RegisterCrawlSourceAsync(DbTransaction tx, Guid churchId, string? website, DateTimeOffset now, CancellationToken ct)
    {
        var url = Normalizer.NormalizeUrl(website);
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        await using var existsCmd = _dbConnection.CreateCommand();
        existsCmd.Transaction = tx;
        existsCmd.CommandText = """
            SELECT COUNT(1) FROM [dbo].[CrawlSources]
            WHERE [UrlHash] = CAST(HASHBYTES('SHA2_256', @Url) AS BINARY (32)) AND [Url] = @Url
            """;
        existsCmd.AddParam("@Url", url);
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
        insertCmd.AddParam("@Id", Guid.CreateVersion7(DateTimeOffset.UtcNow));
        insertCmd.AddParam(ChurchIdParam, churchId);
        insertCmd.AddParam("@Url", url);
        insertCmd.AddParam("@Now", now);
        await insertCmd.ExecuteNonQueryAsync(ct);
    }

    private async Task PublishConfidenceRequestAsync(Guid churchId, CancellationToken ct)
    {
        var body = BinaryData.FromObjectAsJson(new ConfidenceRequest(churchId));
        await _senders.For(ChurchQueueNames.ConfidenceRequests).SendMessageAsync(new ServiceBusMessage(body), ct);
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

        if (DenominationIdsByName.TryGetValue(name, out var cachedDenominationId))
        {
            return cachedDenominationId;
        }

        await using var cmd = _dbConnection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT [Id] FROM [dbo].[Denominations] WHERE [Name] = @Name";
        cmd.AddParam(NameParam, name);
        if (await cmd.ExecuteScalarAsync(ct) is not Guid denominationId)
        {
            return null;
        }

        DenominationIdsByName[name] = denominationId;
        return denominationId;
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
        cmd.AddParam(NameParam, (object?)req.CanonicalName ?? DBNull.Value);
        cmd.AddParam("@City", (object?)req.City ?? DBNull.Value);
        cmd.AddParam("@State", (object?)req.State ?? DBNull.Value);
        cmd.AddParam("@Lat", lat);
        cmd.AddParam("@Lng", lng);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is > 0;
    }

    private async Task<bool> SlugExistsAsync(DbTransaction tx, string slug, Guid excludeChurchId, CancellationToken ct)
    {
        await using var cmd = _dbConnection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(1) FROM [dbo].[Churches] WHERE [Slug] = @Slug AND [Id] <> @ExcludeId";
        cmd.AddParam("@Slug", slug);
        cmd.AddParam("@ExcludeId", excludeChurchId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is > 0;
    }
}

internal readonly record struct WriteFields(decimal Lat, decimal Lng, string Slug, DateTimeOffset Now, Guid? DenominationId);

internal readonly record struct ChurchTextFields(string CanonicalName, string City, string State, string Zip);

internal readonly record struct ChurchAttributeRow(Guid Id, string Key, string Value, string Source, decimal Confidence);

internal readonly record struct ServiceScheduleRow(Guid Id, byte DayOfWeek, TimeSpan StartTime, string? Description);

internal readonly record struct MinistryRow(Guid Id, string Name, string? Description);

internal readonly record struct CampusRow(Guid Id, string Name, string? Street, string City, string State, string Zip, double Latitude, double Longitude);