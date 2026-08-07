namespace Functions.Tests.Unit;

using System.Data;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class ChurchWriterTests
{
    private static readonly GeocodingRequest FullRequest = new(
        CrawlSourceId: Guid.NewGuid(),
        CanonicalName: "Grace Church",
        Street: "123 Main St",
        City: "Phoenix",
        State: "AZ",
        Zip: "85001",
        PhoneNumber: "602-555-1212",
        Website: "https://grace.example",
        EmailAddress: "info@grace.example",
        WorshipStyle: 2,
        PrimaryLanguage: "English",
        AcceptsLGBTQ: true,
        WheelchairAccessible: false,
        HasNursery: true,
        HasYouthProgram: false,
        Confidence: 0.9m);

    [Fact]
    public async Task UpsertAsync_ExistingChurchConnectionClosed_OpensAndUpdates()
    {
        // Arrange — lookup returns an existing ChurchId, so the row is updated; connection starts Closed
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithScalarResult(Guid.CreateVersion7(DateTimeOffset.UtcNow)));
        var writer = NewWriter(connection);

        // Act
        await writer.UpsertAsync(FullRequest, 33.4484m, -112.0740m, TestContext.Current.CancellationToken);

        // Assert — connection opened; lookup + slug check + UPDATE (no INSERT/link); transaction committed
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(3, connection.ExecutedCommands.Count);
        Assert.Contains("UPDATE [dbo].[Churches]", connection.ExecutedCommands[2].CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpsertAsync_NewChurchConnectionOpen_InsertsAndLinks()
    {
        // Arrange — lookup returns null (no existing ChurchId) so the row is new; connection already Open
        var connection = new FakeDbConnection();
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));
        var writer = NewWriter(connection);

        // Act
        await writer.UpsertAsync(FullRequest, 33.4484m, -112.0740m, TestContext.Current.CancellationToken);

        // Assert — church inserted and linked back to its CrawlSource
        Assert.Contains(connection.ExecutedCommands, c =>
            c.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));
        Assert.Contains(connection.ExecutedCommands, c =>
            c.CommandText.Contains("UPDATE [dbo].[CrawlSources] SET [ChurchId]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpsertAsync_NullCanonicalName_ThrowsBeforeInsert()
    {
        // Arrange — CanonicalName is NOT NULL on the Churches table; the validation gate must reject
        // it before ever reaching the DB rather than let a null silently bind as DBNull.
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithScalarResult(null)); // lookup: new
        connection.Enqueue(FakeDbCommand.WithScalarResult(null)); // slug check: free
        var writer = NewWriter(connection);
        var req = FullRequest with { CanonicalName = null };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            writer.UpsertAsync(req, 0m, 0m, TestContext.Current.CancellationToken));

        Assert.Equal("canonicalName", ex.ParamName);
        Assert.DoesNotContain(connection.ExecutedCommands, c =>
            c.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpsertAsync_NewChurchNullOptionalBools_BindsDbNull()
    {
        // Arrange — null nullable-bools must all coalesce to DBNull (CanonicalName stays populated,
        // since that column is NOT NULL and is covered separately above)
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var req = FullRequest with
        {
            AcceptsLGBTQ = null,
            WheelchairAccessible = null,
            HasNursery = null,
            HasYouthProgram = null
        };

        // Act
        await writer.UpsertAsync(req, 0m, 0m, TestContext.Current.CancellationToken);

        // Assert
        var insert = connection.ExecutedCommands.Single(c =>
            c.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));
        Assert.Equal(DBNull.Value, insert.Parameters["@Lgbtq"].Value);
        Assert.Equal(DBNull.Value, insert.Parameters["@Youth"].Value);
    }

    [Fact]
    public async Task UpsertAsync_NewChurchPopulatedOptionals_BindsValues()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);

        // Act
        await writer.UpsertAsync(FullRequest, 33.4484m, -112.0740m, TestContext.Current.CancellationToken);

        // Assert — populated optionals bind their values; slug joins name-city-state
        var insert = connection.ExecutedCommands.Single(c =>
            c.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));
        Assert.Equal("Grace Church", insert.Parameters["@Name"].Value);
        Assert.Equal("grace-church-phoenix-az", insert.Parameters["@Slug"].Value);
        Assert.True(insert.Parameters["@Lgbtq"].Value is true);
        Assert.True(insert.Parameters["@Youth"].Value is false);
    }

    [Fact]
    public async Task UpsertAsync_NormalizesPhoneZipAndWebsite()
    {
        // Arrange — raw, messy inputs that the Normalizer should clean before insert
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var req = FullRequest with
        {
            PhoneNumber = "(602) 555-1212",
            Zip = "85001-1234",
            Website = "http://grace.example/"
        };

        // Act
        await writer.UpsertAsync(req, 33.4484m, -112.0740m, TestContext.Current.CancellationToken);

        // Assert
        var insert = connection.ExecutedCommands.Single(c =>
            c.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));
        Assert.Equal("+16025551212", insert.Parameters["@Phone"].Value);
        Assert.Equal("85001", insert.Parameters["@Zip"].Value);
        Assert.Equal("https://grace.example", insert.Parameters["@Website"].Value);
    }

    [Fact]
    public async Task UpsertAsync_CanonicalNameOverLimit_TruncatesTo300Chars()
    {
        // Arrange — Churches.CanonicalName is NVARCHAR(300); a longer LLM-extracted name must be
        // truncated before binding rather than let SqlException 8152 fail the whole church write.
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var req = FullRequest with { CanonicalName = new string('n', 350) };

        // Act
        await writer.UpsertAsync(req, 33.4484m, -112.0740m, TestContext.Current.CancellationToken);

        // Assert
        var insert = connection.ExecutedCommands.Single(c =>
            c.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));
        Assert.Equal(new string('n', 300), insert.Parameters["@Name"].Value);
    }

    [Fact]
    public async Task UpsertAsync_StreetEmailAndLanguageOverLimit_TruncateToColumnLength()
    {
        // Arrange — Churches.Street is NVARCHAR(200), EmailAddress is NVARCHAR(254), PrimaryLanguage is
        // NVARCHAR(50); Website is normalized first (Normalizer.NormalizeUrl can grow the raw string) so
        // it is covered separately below.
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var req = FullRequest with
        {
            Street = new string('t', 250),
            EmailAddress = new string('e', 300),
            PrimaryLanguage = new string('l', 80),
        };

        // Act
        await writer.UpsertAsync(req, 33.4484m, -112.0740m, TestContext.Current.CancellationToken);

        // Assert
        var insert = connection.ExecutedCommands.Single(c =>
            c.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));
        Assert.Equal(new string('t', 200), insert.Parameters["@Street"].Value);
        Assert.Equal(new string('e', 254), insert.Parameters["@Email"].Value);
        Assert.Equal(new string('l', 50), insert.Parameters["@Lang"].Value);
    }

    [Fact]
    public async Task UpsertAsync_NormalizedWebsiteOverLimit_TruncatesTo500Chars()
    {
        // Arrange — Churches.Website is NVARCHAR(500); NormalizeUrl prepends "https://" to a schemeless
        // input, so a raw value near the limit must be truncated *after* normalization, not before.
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var req = FullRequest with { Website = new string('w', 500) };

        // Act
        await writer.UpsertAsync(req, 33.4484m, -112.0740m, TestContext.Current.CancellationToken);

        // Assert
        var insert = connection.ExecutedCommands.Single(c =>
            c.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));
        var website = Assert.IsType<string>(insert.Parameters["@Website"].Value);
        Assert.Equal(500, website.Length);
        Assert.StartsWith("https://", website, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpsertAsync_LongNameAndCity_SlugTruncatedToColumnLength()
    {
        // Arrange — Churches.Slug is NVARCHAR(320); a base slug built from an over-limit name and city
        // must be capped (with room for GenerateUniqueSlugAsync's numeric suffix) rather than overflow.
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var req = FullRequest with { CanonicalName = new string('n', 350), City = new string('c', 150) };

        // Act
        await writer.UpsertAsync(req, 33.4484m, -112.0740m, TestContext.Current.CancellationToken);

        // Assert
        var insert = connection.ExecutedCommands.Single(c =>
            c.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));
        var slugValue = Assert.IsType<string>(insert.Parameters["@Slug"].Value);
        Assert.True(slugValue.Length <= 320);
    }

    [Fact]
    public async Task UpsertAsync_KnownDenomination_BindsResolvedId()
    {
        // Arrange — new church carrying a denomination name that resolves to an existing row
        var denominationId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));          // lookup: no existing ChurchId
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));          // slug check: free
        connection.Enqueue(FakeDbCommand.WithScalarResult(denominationId)); // denomination resolve: found
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));          // duplicate check: not a dup
        var writer = NewWriter(connection);
        var req = FullRequest with { DenominationName = "Baptist" };

        // Act
        await writer.UpsertAsync(req, 33.4484m, -112.0740m, TestContext.Current.CancellationToken);

        // Assert
        var insert = connection.ExecutedCommands.Single(c =>
            c.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));
        Assert.Equal(denominationId, insert.Parameters["@Denom"].Value);
    }

    [Fact]
    public async Task UpsertAsync_UnknownDenomination_BindsDbNull()
    {
        // Arrange — denomination name present but not found in the reference table
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithScalarResult(null)); // lookup: new
        connection.Enqueue(FakeDbCommand.WithScalarResult(null)); // slug check: free
        connection.Enqueue(FakeDbCommand.WithScalarResult(null)); // denomination resolve: not found
        connection.Enqueue(FakeDbCommand.WithScalarResult(null)); // duplicate check: not a dup
        var writer = NewWriter(connection);
        var req = FullRequest with { DenominationName = "Pastafarian" };

        // Act
        await writer.UpsertAsync(req, 33.4484m, -112.0740m, TestContext.Current.CancellationToken);

        // Assert
        var insert = connection.ExecutedCommands.Single(c =>
            c.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));
        Assert.Equal(DBNull.Value, insert.Parameters["@Denom"].Value);
    }

    [Fact]
    public async Task UpsertAsync_NoDenominationName_DoesNotQueryDenominations()
    {
        // Arrange — null denomination name must skip the resolution query entirely (no extra command)
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);

        // Act — FullRequest has no DenominationName
        await writer.UpsertAsync(FullRequest, 33.4484m, -112.0740m, TestContext.Current.CancellationToken);

        // Assert — a null denomination name must skip the resolution query entirely
        Assert.DoesNotContain(
            connection.ExecutedCommands,
            c => c.CommandText.Contains("[dbo].[Denominations]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpsertAsync_BlankCity_ThrowsBeforeInsert()
    {
        // Arrange — lookup (new) + slug check (free) run before validation; the point of this test is
        // that the actual INSERT never happens once City fails the Shared.Domain.Church guard.
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithScalarResult(null)); // lookup: new
        connection.Enqueue(FakeDbCommand.WithScalarResult(null)); // slug check: free
        var writer = NewWriter(connection);
        var req = FullRequest with { City = string.Empty };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            writer.UpsertAsync(req, 33.4484m, -112.0740m, TestContext.Current.CancellationToken));

        Assert.Equal("city", ex.ParamName);
        Assert.DoesNotContain(connection.ExecutedCommands, c =>
            c.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpsertAsync_StateNotTwoLetters_ThrowsBeforeInsert()
    {
        // Arrange — same shape as the blank-city case, guarding the State 2-letter-code invariant
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithScalarResult(null)); // lookup: new
        connection.Enqueue(FakeDbCommand.WithScalarResult(null)); // slug check: free
        var writer = NewWriter(connection);
        var req = FullRequest with { State = "Arizona" };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            writer.UpsertAsync(req, 33.4484m, -112.0740m, TestContext.Current.CancellationToken));

        Assert.Equal("state", ex.ParamName);
        Assert.DoesNotContain(connection.ExecutedCommands, c =>
            c.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpsertAsync_SlugCollision_AppendsSuffix()
    {
        // Arrange — new church; the base slug is already taken, the "-2" variant is free.
        // Two distinct congregations sharing name+city+state must both persist with unique slugs.
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithScalarResult(null)); // lookup: no existing ChurchId
        connection.Enqueue(FakeDbCommand.WithScalarResult(1));    // slug check: base slug taken
        connection.Enqueue(FakeDbCommand.WithScalarResult(0));    // slug check: "-2" is free
        var writer = NewWriter(connection);

        // Act
        await writer.UpsertAsync(FullRequest, 33.4484m, -112.0740m, TestContext.Current.CancellationToken);

        // Assert — the INSERT carries the disambiguated slug rather than colliding
        var insert = connection.ExecutedCommands.Single(c =>
            c.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));
        Assert.Equal("grace-church-phoenix-az-2", insert.Parameters["@Slug"].Value);
    }

    [Fact]
    public async Task UpsertAsync_IdenticalRecordExists_SkipsInsert()
    {
        // Arrange — new (no CrawlSources link), slug free, but an identical church already exists.
        // This is the at-least-once redelivery case: the message must be a no-op, not a duplicate insert.
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithScalarResult(null)); // lookup: no existing ChurchId
        connection.Enqueue(FakeDbCommand.WithScalarResult(null)); // slug check: free
        connection.Enqueue(FakeDbCommand.WithScalarResult(1));    // duplicate check: identical row exists
        var writer = NewWriter(connection);

        // Act
        await writer.UpsertAsync(FullRequest, 33.4484m, -112.0740m, TestContext.Current.CancellationToken);

        // Assert — no INSERT and no CrawlSources link issued
        Assert.DoesNotContain(
            connection.ExecutedCommands,
            c => c.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpsertAsync_AttributeFieldsOverLimit_TruncateToColumnLength()
    {
        // Arrange — ChurchAttributes.Key/Source are NVARCHAR(100), Value is NVARCHAR(1000)
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var req = FullRequest with
        {
            Attributes = [new ChurchAttributeData(new string('k', 150), new string('v', 1050), new string('s', 150), 0.5m)],
        };

        // Act
        await writer.UpsertAsync(req, 33.4484m, -112.0740m, TestContext.Current.CancellationToken);

        // Assert
        var insert = connection.ExecutedCommands.Single(c =>
            c.CommandText.Contains("INSERT INTO [dbo].[ChurchAttributes]", StringComparison.Ordinal));
        Assert.Equal(new string('k', 100), insert.Parameters["@Key"].Value);
        Assert.Equal(new string('v', 1000), insert.Parameters["@Value"].Value);
        Assert.Equal(new string('s', 100), insert.Parameters["@Source"].Value);
    }

    [Fact]
    public async Task UpsertAsync_WithAttributes_RefreshesAttributesAndPublishesRecalc()
    {
        // Arrange — a new church carrying one provenance attribute
        var connection = new FakeDbConnection();
        var (factory, sent) = FakeServiceBus.Create();
        var writer = new ChurchWriter(connection, factory);
        var req = FullRequest with { Attributes = [new ChurchAttributeData("ntee_code", "X20", "irs", 0.5m)] };

        // Act
        await writer.UpsertAsync(req, 33.4484m, -112.0740m, TestContext.Current.CancellationToken);

        // Assert — per-source refresh (delete + insert) ran, and one recalc request was published
        Assert.Contains(connection.ExecutedCommands, c =>
            c.CommandText.Contains("DELETE FROM [dbo].[ChurchAttributes]", StringComparison.Ordinal));
        Assert.Contains(connection.ExecutedCommands, c =>
            c.CommandText.Contains("INSERT INTO [dbo].[ChurchAttributes]", StringComparison.Ordinal));
        Assert.Single(sent);
    }

    [Fact]
    public async Task UpsertAsync_DuplicateSkip_DoesNotPublishRecalc()
    {
        // Arrange — identical record already exists, so the write is a no-op
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithScalarResult(null)); // lookup: new
        connection.Enqueue(FakeDbCommand.WithScalarResult(null)); // slug check: free
        connection.Enqueue(FakeDbCommand.WithScalarResult(1));    // duplicate check: identical exists
        var (factory, sent) = FakeServiceBus.Create();
        var writer = new ChurchWriter(connection, factory);

        // Act
        await writer.UpsertAsync(FullRequest, 33.4484m, -112.0740m, TestContext.Current.CancellationToken);

        // Assert — nothing written, nothing to recalc
        Assert.Empty(sent);
    }

    [Fact]
    public async Task UpsertAsync_NewChurchWithWebsite_RegistersCrawlSource()
    {
        // Arrange — new church with a website and no existing crawl source for that URL
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithScalarResult(null)); // lookup: new
        connection.Enqueue(FakeDbCommand.WithScalarResult(null)); // slug check: free
        connection.Enqueue(FakeDbCommand.WithScalarResult(null)); // duplicate check: not a dup
        var writer = NewWriter(connection); // FullRequest has Website set; crawl-source exists check defaults to none

        // Act
        await writer.UpsertAsync(FullRequest, 33.4484m, -112.0740m, TestContext.Current.CancellationToken);

        // Assert — a crawl source was registered for the church's website
        Assert.Contains(connection.ExecutedCommands, c =>
            c.CommandText.Contains("INSERT INTO [dbo].[CrawlSources]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpsertAsync_NewChurchNoWebsite_DoesNotRegisterCrawlSource()
    {
        // Arrange — no website means nothing to crawl
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var req = FullRequest with { Website = null };

        // Act
        await writer.UpsertAsync(req, 0m, 0m, TestContext.Current.CancellationToken);

        // Assert
        Assert.DoesNotContain(connection.ExecutedCommands, c =>
            c.CommandText.Contains("INSERT INTO [dbo].[CrawlSources]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpsertAsync_WithServiceSchedules_ReplacesAndInsertsThem()
    {
        // Arrange — new church carrying two valid service schedules (and one invalid that is dropped)
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var req = FullRequest with
        {
            ServiceSchedules =
            [
                new ServiceScheduleData(0, "10:30", "Sunday Worship"),
                new ServiceScheduleData(3, "19:00", "Bible Study"),
                new ServiceScheduleData(9, "bad-time", "ignored"),
            ],
        };

        // Act
        await writer.UpsertAsync(req, 33.4484m, -112.0740m, TestContext.Current.CancellationToken);

        // Assert — schedules are replaced then the two valid ones inserted
        Assert.Contains(connection.ExecutedCommands, c =>
            c.CommandText.Contains("DELETE FROM [dbo].[ServiceSchedules]", StringComparison.Ordinal));
        Assert.Equal(2, connection.ExecutedCommands.Count(c =>
            c.CommandText.Contains("INSERT INTO [dbo].[ServiceSchedules]", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task UpsertAsync_ServiceScheduleDescriptionOverLimit_TruncatesTo200Chars()
    {
        // Arrange — ServiceSchedules.Description is NVARCHAR(200); a longer scraped description must be
        // truncated before binding rather than let SqlException 8152 fail the whole church write.
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var overLong = new string('x', 250);
        var req = FullRequest with
        {
            ServiceSchedules = [new ServiceScheduleData(0, "10:30", overLong)],
        };

        // Act
        await writer.UpsertAsync(req, 33.4484m, -112.0740m, TestContext.Current.CancellationToken);

        // Assert
        var insert = connection.ExecutedCommands.Single(c =>
            c.CommandText.Contains("INSERT INTO [dbo].[ServiceSchedules]", StringComparison.Ordinal));
        Assert.Equal(new string('x', 200), insert.Parameters["@Desc"].Value);
    }

    [Fact]
    public async Task UpsertAsync_WithMinistries_ReplacesAndInsertsNamedOnes()
    {
        // Arrange — two named ministries plus one blank-name entry that is dropped
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var req = FullRequest with
        {
            Ministries =
            [
                new MinistryData("Youth Group", "Teens"),
                new MinistryData("Food Bank", null),
                new MinistryData("  ", "ignored"),
            ],
        };

        // Act
        await writer.UpsertAsync(req, 33.4484m, -112.0740m, TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains(connection.ExecutedCommands, c =>
            c.CommandText.Contains("DELETE FROM [dbo].[Ministries]", StringComparison.Ordinal));
        Assert.Equal(2, connection.ExecutedCommands.Count(c =>
            c.CommandText.Contains("INSERT INTO [dbo].[Ministries]", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task UpsertAsync_MinistryFieldsOverLimit_TruncateToColumnLength()
    {
        // Arrange — Ministries.Name is NVARCHAR(200), Description is NVARCHAR(1000)
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var req = FullRequest with
        {
            Ministries = [new MinistryData(new string('m', 250), new string('d', 1050))],
        };

        // Act
        await writer.UpsertAsync(req, 33.4484m, -112.0740m, TestContext.Current.CancellationToken);

        // Assert
        var insert = connection.ExecutedCommands.Single(c =>
            c.CommandText.Contains("INSERT INTO [dbo].[Ministries]", StringComparison.Ordinal));
        Assert.Equal(new string('m', 200), insert.Parameters["@Name"].Value);
        Assert.Equal(new string('d', 1000), insert.Parameters["@Desc"].Value);
    }

    [Fact]
    public async Task UpsertAsync_WithCampuses_ReplacesAndInsertsCompleteOnes()
    {
        // Arrange — one complete campus plus one missing required address parts (dropped)
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var req = FullRequest with
        {
            Campuses =
            [
                new CampusData("North Campus", "1 N St", "Denver", "CO", "80201", 39.7m, -104.9m),
                new CampusData("Bad Campus", null, string.Empty, "CO", "80201", 0m, 0m),
            ],
        };

        // Act
        await writer.UpsertAsync(req, 33.4484m, -112.0740m, TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains(connection.ExecutedCommands, c =>
            c.CommandText.Contains("DELETE FROM [dbo].[Campuses]", StringComparison.Ordinal));
        Assert.Equal(1, connection.ExecutedCommands.Count(c =>
            c.CommandText.Contains("INSERT INTO [dbo].[Campuses]", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task UpsertAsync_CampusFieldsOverLimit_TruncateToColumnLength()
    {
        // Arrange — Campuses.Name/Street are NVARCHAR(200), City is NVARCHAR(100), Zip is NVARCHAR(10)
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var req = FullRequest with
        {
            Campuses =
            [
                new CampusData(new string('n', 250), new string('s', 250), new string('c', 150), "CO", "802010000000", 39.7m, -104.9m),
            ],
        };

        // Act
        await writer.UpsertAsync(req, 33.4484m, -112.0740m, TestContext.Current.CancellationToken);

        // Assert
        var insert = connection.ExecutedCommands.Single(c =>
            c.CommandText.Contains("INSERT INTO [dbo].[Campuses]", StringComparison.Ordinal));
        Assert.Equal(new string('n', 200), insert.Parameters["@Name"].Value);
        Assert.Equal(new string('s', 200), insert.Parameters["@Street"].Value);
        Assert.Equal(new string('c', 100), insert.Parameters["@City"].Value);
        Assert.Equal("8020100000", insert.Parameters["@Zip"].Value);
    }

    [Fact]
    public async Task UpdateCoordinatesAsync_RowAffected_UpdatesAndPublishesConfidence()
    {
        // Arrange
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var (factory, sent) = FakeServiceBus.Create();
        var writer = new ChurchWriter(connection, factory);

        // Act
        var updated = await writer.UpdateCoordinatesAsync(Guid.NewGuid(), 39.7m, -104.9m, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(updated);
        Assert.Contains(connection.ExecutedCommands, c =>
            c.CommandText.Contains("UPDATE [dbo].[Churches]", StringComparison.Ordinal));
        Assert.Single(sent); // confidence-requests publish
    }

    [Fact]
    public async Task UpdateCoordinatesAsync_NoRow_ReturnsFalseAndPublishesNothing()
    {
        // Arrange — default nonquery result is 0 (no row matched)
        var connection = new FakeDbConnection();
        var (factory, sent) = FakeServiceBus.Create();
        var writer = new ChurchWriter(connection, factory);

        // Act
        var updated = await writer.UpdateCoordinatesAsync(Guid.NewGuid(), 39.7m, -104.9m, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(updated);
        Assert.Empty(sent);
    }

    private static ChurchWriter NewWriter(FakeDbConnection connection) =>
        new(connection, FakeServiceBus.Create().Factory);
}