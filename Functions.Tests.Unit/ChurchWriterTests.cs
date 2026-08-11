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
        // Arrange
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithScalarResult(Guid.CreateVersion7(DateTimeOffset.UtcNow)));
        var writer = NewWriter(connection);

        // Act
        await writer.UpsertAsync(FullRequest, 33.4484m, -112.0740m, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(3, connection.ExecutedCommands.Count);
        Assert.Contains("UPDATE [dbo].[Churches]", connection.ExecutedCommands[2].CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpsertAsync_NewChurchConnectionOpen_InsertsAndLinks()
    {
        // Arrange
        var connection = new FakeDbConnection();
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));
        var writer = NewWriter(connection);

        // Act
        await writer.UpsertAsync(FullRequest, 33.4484m, -112.0740m, TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains(connection.ExecutedCommands, c =>
            c.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));
        Assert.Contains(connection.ExecutedCommands, c =>
            c.CommandText.Contains("UPDATE [dbo].[CrawlSources] SET [ChurchId]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpsertAsync_NullCanonicalName_ThrowsBeforeInsert()
    {
        // Arrange
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));
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
        // Arrange
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

        // Assert
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
        // Arrange
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
        // Arrange
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
        // Arrange
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
        // Arrange
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
        // Arrange
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
        // Arrange
        var denominationId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));
        connection.Enqueue(FakeDbCommand.WithScalarResult(denominationId));
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));
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
        // Arrange
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));
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
        // Arrange
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);

        // Act
        await writer.UpsertAsync(FullRequest, 33.4484m, -112.0740m, TestContext.Current.CancellationToken);

        // Assert
        Assert.DoesNotContain(
            connection.ExecutedCommands,
            c => c.CommandText.Contains("[dbo].[Denominations]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpsertAsync_BlankCity_ThrowsBeforeInsert()
    {
        // Arrange
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));
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
        // Arrange
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));
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
        // Arrange
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));
        connection.Enqueue(FakeDbCommand.WithScalarResult(1));
        connection.Enqueue(FakeDbCommand.WithScalarResult(0));
        var writer = NewWriter(connection);

        // Act
        await writer.UpsertAsync(FullRequest, 33.4484m, -112.0740m, TestContext.Current.CancellationToken);

        // Assert
        var insert = connection.ExecutedCommands.Single(c =>
            c.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));
        Assert.Equal("grace-church-phoenix-az-2", insert.Parameters["@Slug"].Value);
    }

    [Fact]
    public async Task UpsertAsync_IdenticalRecordExists_SkipsInsert()
    {
        // Arrange
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));
        connection.Enqueue(FakeDbCommand.WithScalarResult(1));
        var writer = NewWriter(connection);

        // Act
        await writer.UpsertAsync(FullRequest, 33.4484m, -112.0740m, TestContext.Current.CancellationToken);

        // Assert
        Assert.DoesNotContain(
            connection.ExecutedCommands,
            c => c.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpsertAsync_AttributeFieldsOverLimit_TruncateToColumnLength()
    {
        // Arrange
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
        // Arrange
        var connection = new FakeDbConnection();
        var (factory, sent) = FakeServiceBus.Create();
        var writer = new ChurchWriter(connection, factory);
        var req = FullRequest with { Attributes = [new ChurchAttributeData("ntee_code", "X20", "irs", 0.5m)] };

        // Act
        await writer.UpsertAsync(req, 33.4484m, -112.0740m, TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains(connection.ExecutedCommands, c =>
            c.CommandText.Contains("DELETE FROM [dbo].[ChurchAttributes]", StringComparison.Ordinal));
        Assert.Contains(connection.ExecutedCommands, c =>
            c.CommandText.Contains("INSERT INTO [dbo].[ChurchAttributes]", StringComparison.Ordinal));
        Assert.Single(sent);
    }

    [Fact]
    public async Task UpsertAsync_DuplicateSkip_DoesNotPublishRecalc()
    {
        // Arrange
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));
        connection.Enqueue(FakeDbCommand.WithScalarResult(1));
        var (factory, sent) = FakeServiceBus.Create();
        var writer = new ChurchWriter(connection, factory);

        // Act
        await writer.UpsertAsync(FullRequest, 33.4484m, -112.0740m, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(sent);
    }

    [Fact]
    public async Task UpsertAsync_NewChurchWithWebsite_RegistersCrawlSource()
    {
        // Arrange
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));
        var writer = NewWriter(connection);

        // Act
        await writer.UpsertAsync(FullRequest, 33.4484m, -112.0740m, TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains(connection.ExecutedCommands, c =>
            c.CommandText.Contains("INSERT INTO [dbo].[CrawlSources]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpsertAsync_NewChurchNoWebsite_DoesNotRegisterCrawlSource()
    {
        // Arrange
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
        // Arrange
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

        // Assert
        Assert.Contains(connection.ExecutedCommands, c =>
            c.CommandText.Contains("DELETE FROM [dbo].[ServiceSchedules]", StringComparison.Ordinal));
        Assert.Equal(2, connection.ExecutedCommands.Count(c =>
            c.CommandText.Contains("INSERT INTO [dbo].[ServiceSchedules]", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task UpsertAsync_ServiceScheduleDescriptionOverLimit_TruncatesTo200Chars()
    {
        // Arrange
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
        // Arrange
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
        // Arrange
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
        // Arrange
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
        // Arrange
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
        Assert.Single(sent);
    }

    [Fact]
    public async Task UpdateCoordinatesAsync_NoRow_ReturnsFalseAndPublishesNothing()
    {
        // Arrange
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