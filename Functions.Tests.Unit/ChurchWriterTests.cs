namespace Functions.Tests.Unit;

using System.Data;
using System.Globalization;
using System.Text.Json;
using Functions.Churches;
using Functions.Tests.Unit.TestSupport;
using static Functions.Tests.Unit.ChurchWriterFixtureConstants;

[Trait("Category", "Unit")]
public sealed class ChurchWriterTests
{
    [Fact]
    public async Task UpsertAsync_ExistingChurchConnectionClosed_OpensAndUpdates()
    {
        // Arrange
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithScalarResult(Guid.CreateVersion7(DateTimeOffset.UtcNow)));
        var writer = NewWriter(connection);

        // Act
        await writer.UpsertAsync(NewFullRequest(), Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(LookupThenInsertThenConfidence, connection.ExecutedCommands.Count);
        Assert.Contains(connection.ExecutedCommands, command => command.CommandText.Contains("UPDATE [dbo].[Churches]", StringComparison.Ordinal));
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
        await writer.UpsertAsync(NewFullRequest(), Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

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
        var req = NewFullRequest() with { CanonicalName = null };

        // Act
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            writer.UpsertAsync(req, Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude(), TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(nameof(GeocodingRequest.CanonicalName), ex.ParamName);
        Assert.DoesNotContain(connection.ExecutedCommands, c =>
            c.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpsertAsync_NewChurchNullOptionalBools_BindsDbNull()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var req = NewFullRequest() with
        {
            AcceptsLGBTQ = null,
            WheelchairAccessible = null,
            HasNursery = null,
            HasYouthProgram = null,
        };

        // Act
        await writer.UpsertAsync(req, Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        Assert.Equal(DBNull.Value, insert.Parameters[ChurchSqlParameters.Lgbtq].Value);
        Assert.Equal(DBNull.Value, insert.Parameters[ChurchSqlParameters.Youth].Value);
    }

    [Fact]
    public async Task UpsertAsync_NewChurch_BindsCreatedAtAsDateTimeOffset()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var req = NewFullRequest();

        // Act
        await writer.UpsertAsync(req, Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        Assert.IsType<DateTimeOffset>(insert.Parameters[ChurchSqlParameters.Now].Value);
    }

    [Fact]
    public async Task UpsertAsync_NewChurch_BindsCreatedAtInUtc()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var req = NewFullRequest();

        // Act
        await writer.UpsertAsync(req, Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        Assert.Equal(TimeSpan.Zero, Assert.IsType<DateTimeOffset>(insert.Parameters[ChurchSqlParameters.Now].Value).Offset);
    }

    [Fact]
    public async Task UpsertAsync_NewChurchPopulatedOptionals_BindsValues()
    {
        // Arrange
        var canonicalName = Generated.NewChurchName();
        var city = Generated.NewCity();
        var state = Generated.NewStateCodeText();
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var req = NewFullRequest(canonicalName, city, state);

        // Act
        await writer.UpsertAsync(req, Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        Assert.Equal(canonicalName, insert.Parameters[ChurchSqlParameters.Name].Value);
        Assert.Equal(ExpectedSlug(canonicalName, city, state), insert.Parameters[ChurchSqlParameters.Slug].Value);
        Assert.True(insert.Parameters[ChurchSqlParameters.Lgbtq].Value is true);
        Assert.True(insert.Parameters[ChurchSqlParameters.Youth].Value is false);
    }

    [Fact]
    public async Task UpsertAsync_NormalizesPhoneZipAndWebsite()
    {
        // Arrange
        var areaCode = Random.Shared.Next(200, 1000);
        var exchange = Random.Shared.Next(200, 1000);
        var lineNumber = Random.Shared.Next(1000, 10000);
        var zipFiveDigits = Random.Shared.Next(10000, 100000).ToString(CultureInfo.InvariantCulture);
        var zipPlusFour = Random.Shared.Next(1000, 10000).ToString(CultureInfo.InvariantCulture);
        var websiteHost = Generated.NewHost();
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var req = NewFullRequest() with
        {
            PhoneNumber = $"({areaCode}) {exchange}-{lineNumber}",
            Zip = $"{zipFiveDigits}-{zipPlusFour}",
            Website = $"{Uri.UriSchemeHttp}://{websiteHost}/",
        };

        // Act
        await writer.UpsertAsync(req, Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        Assert.Equal(
            $"{NorthAmericanCountryCode}{areaCode}{exchange}{lineNumber}",
            insert.Parameters[ChurchSqlParameters.Phone].Value);
        Assert.Equal(zipFiveDigits, insert.Parameters[ChurchSqlParameters.Zip].Value);
        Assert.Equal($"{Uri.UriSchemeHttps}://{websiteHost}", insert.Parameters[ChurchSqlParameters.Website].Value);
    }

    [Fact]
    public async Task UpsertAsync_CanonicalNameOverLimit_TruncatesToColumnLength()
    {
        // Arrange
        var namePadding = Generated.NewPaddingChar();
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var req = NewFullRequest() with
        {
            CanonicalName = new string(namePadding, ChurchWriter.CanonicalNameMaxLength + Generated.NewOverflowMargin()),
        };

        // Act
        await writer.UpsertAsync(req, Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        Assert.Equal(new string(namePadding, ChurchWriter.CanonicalNameMaxLength), insert.Parameters[ChurchSqlParameters.Name].Value);
    }

    [Fact]
    public async Task UpsertAsync_StreetEmailAndLanguageOverLimit_TruncateToColumnLength()
    {
        // Arrange
        var streetPadding = Generated.NewPaddingChar();
        var emailPadding = Generated.NewPaddingChar();
        var languagePadding = Generated.NewPaddingChar();
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var req = NewFullRequest() with
        {
            Street = new string(streetPadding, ChurchWriter.StreetMaxLength + Generated.NewOverflowMargin()),
            EmailAddress = new string(emailPadding, ChurchWriter.EmailMaxLength + Generated.NewOverflowMargin()),
            PrimaryLanguage = new string(languagePadding, ChurchWriter.PrimaryLanguageMaxLength + Generated.NewOverflowMargin()),
        };

        // Act
        await writer.UpsertAsync(req, Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        Assert.Equal(new string(streetPadding, ChurchWriter.StreetMaxLength), insert.Parameters[ChurchSqlParameters.Street].Value);
        Assert.Equal(new string(emailPadding, ChurchWriter.EmailMaxLength), insert.Parameters[ChurchSqlParameters.Email].Value);
        Assert.Equal(new string(languagePadding, ChurchWriter.PrimaryLanguageMaxLength), insert.Parameters[ChurchSqlParameters.Lang].Value);
    }

    [Fact]
    public async Task UpsertAsync_NormalizedWebsiteOverLimit_TruncatesToColumnLength()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var req = NewFullRequest() with
        {
            Website = new string(Generated.NewPaddingChar(), ChurchWriter.WebsiteMaxLength),
        };

        // Act
        await writer.UpsertAsync(req, Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        var website = Assert.IsType<string>(insert.Parameters[ChurchSqlParameters.Website].Value);
        Assert.Equal(ChurchWriter.WebsiteMaxLength, website.Length);
        Assert.StartsWith($"{Uri.UriSchemeHttps}://", website, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpsertAsync_LongNameAndCity_SlugTruncatedToColumnLength()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var req = NewFullRequest() with
        {
            CanonicalName = new string(Generated.NewPaddingChar(), ChurchWriter.CanonicalNameMaxLength + Generated.NewOverflowMargin()),
            City = new string(Generated.NewPaddingChar(), ChurchWriter.CityMaxLength + Generated.NewOverflowMargin()),
        };

        // Act
        await writer.UpsertAsync(req, Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        var slugValue = Assert.IsType<string>(insert.Parameters[ChurchSqlParameters.Slug].Value);
        Assert.True(slugValue.Length <= ChurchWriter.SlugMaxLength);
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
        var req = NewFullRequest() with { DenominationName = Generated.NewDenominationName() };

        // Act
        await writer.UpsertAsync(req, Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        Assert.Equal(denominationId, insert.Parameters[ChurchSqlParameters.Denom].Value);
    }

    [Fact]
    public async Task UpsertAsync_DenominationResolvedByAnEarlierWrite_IsNotQueriedAgain()
    {
        // Arrange
        var denominationName = Generated.NewDenominationName();
        var denominationId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var firstConnection = new FakeDbConnection();
        firstConnection.Enqueue(FakeDbCommand.WithScalarResult(null));
        firstConnection.Enqueue(FakeDbCommand.WithScalarResult(null));
        firstConnection.Enqueue(FakeDbCommand.WithScalarResult(denominationId));
        firstConnection.Enqueue(FakeDbCommand.WithScalarResult(null));
        await NewWriter(firstConnection).UpsertAsync(
            NewFullRequest() with { DenominationName = denominationName },
            Generated.NewGeocodedLatitude(),
            Generated.NewGeocodedLongitude(),
            TestContext.Current.CancellationToken);
        var secondConnection = new FakeDbConnection();
        var secondWriter = NewWriter(secondConnection);
        var secondRequest = NewFullRequest() with { DenominationName = denominationName };

        // Act
        await secondWriter.UpsertAsync(secondRequest, Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        Assert.DoesNotContain(
            secondConnection.ExecutedCommands,
            c => c.CommandText.Contains("[dbo].[Denominations]", StringComparison.Ordinal));
        Assert.Equal(denominationId, SingleChurchInsert(secondConnection).Parameters[ChurchSqlParameters.Denom].Value);
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
        var req = NewFullRequest() with { DenominationName = Generated.NewDenominationName() };

        // Act
        await writer.UpsertAsync(req, Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        Assert.Equal(DBNull.Value, insert.Parameters[ChurchSqlParameters.Denom].Value);
    }

    [Fact]
    public async Task UpsertAsync_NoDenominationName_DoesNotQueryDenominations()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);

        // Act
        await writer.UpsertAsync(NewFullRequest(), Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

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
        var req = NewFullRequest() with { City = string.Empty };

        // Act
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            writer.UpsertAsync(req, Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude(), TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(nameof(GeocodingRequest.City), ex.ParamName);
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
        var req = NewFullRequest() with { State = Generated.NewFullStateName() };

        // Act
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            writer.UpsertAsync(req, Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude(), TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(nameof(GeocodingRequest.State), ex.ParamName);
        Assert.DoesNotContain(connection.ExecutedCommands, c =>
            c.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpsertAsync_SlugCollision_AppendsSuffix()
    {
        // Arrange
        var canonicalName = Generated.NewChurchName();
        var city = Generated.NewCity();
        var state = Generated.NewStateCodeText();
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));
        connection.Enqueue(FakeDbCommand.WithScalarResult(1));
        connection.Enqueue(FakeDbCommand.WithScalarResult(0));
        var writer = NewWriter(connection);

        // Act
        await writer.UpsertAsync(
            NewFullRequest(canonicalName, city, state),
            Generated.NewGeocodedLatitude(),
            Generated.NewGeocodedLongitude(),
            TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        Assert.Equal(
            $"{ExpectedSlug(canonicalName, city, state)}-{SecondSlugOrdinal}",
            insert.Parameters[ChurchSqlParameters.Slug].Value);
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
        await writer.UpsertAsync(NewFullRequest(), Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        Assert.DoesNotContain(
            connection.ExecutedCommands,
            c => c.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpsertAsync_AttributeFieldsOverLimit_TruncateToColumnLength()
    {
        // Arrange
        var keyPadding = Generated.NewPaddingChar();
        var valuePadding = Generated.NewPaddingChar();
        var sourcePadding = Generated.NewPaddingChar();
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var overlongAttribute = new ChurchAttributeData(
            new string(keyPadding, ChurchWriter.AttributeKeyMaxLength + Generated.NewOverflowMargin()),
            new string(valuePadding, ChurchWriter.AttributeValueMaxLength + Generated.NewOverflowMargin()),
            new string(sourcePadding, ChurchWriter.AttributeSourceMaxLength + Generated.NewOverflowMargin()),
            Generated.NewConfidence());
        var req = NewFullRequest() with { Attributes = [overlongAttribute] };

        // Act
        await writer.UpsertAsync(req, Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var replace = SingleReplacement(connection, "[dbo].[ChurchAttributes]");
        Assert.Contains(new string(keyPadding, ChurchWriter.AttributeKeyMaxLength), RowValues(replace));
        Assert.Contains(new string(valuePadding, ChurchWriter.AttributeValueMaxLength), RowValues(replace));
        Assert.Contains(new string(sourcePadding, ChurchWriter.AttributeSourceMaxLength), RowValues(replace));
    }

    [Fact]
    public async Task UpsertAsync_WithAttributes_ReplacesTheWritingSourcesAttributesInOneCommandAndPublishesRecalc()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var (senders, sent) = FakeServiceBus.CreateSenders();
        var writer = new ChurchWriter(connection, senders);
        var nteeAttribute = new ChurchAttributeData(
            ChurchAttributeKeys.NteeCode,
            Generated.NewNteeCode(),
            ChurchImportSources.Irs,
            ChurchImportConfidence.Irs);
        var req = NewFullRequest() with { Attributes = [nteeAttribute] };

        // Act
        await writer.UpsertAsync(req, Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var replace = SingleReplacement(connection, "[dbo].[ChurchAttributes]");
        Assert.Contains("DELETE FROM [dbo].[ChurchAttributes]", replace.CommandText, StringComparison.Ordinal);
        Assert.Contains("[Source] IN (SELECT [value] FROM OPENJSON(@Sources))", replace.CommandText, StringComparison.Ordinal);
        Assert.Contains(nteeAttribute.Source, Assert.IsType<string>(replace.Parameters[ChurchSqlParameters.Sources].Value), StringComparison.Ordinal);
        Assert.Contains(nteeAttribute.Value, RowValues(replace));
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
        var (senders, sent) = FakeServiceBus.CreateSenders();
        var writer = new ChurchWriter(connection, senders);

        // Act
        await writer.UpsertAsync(NewFullRequest(), Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(sent);
    }

    [Fact]
    public async Task UpsertAsync_NewChurchWithWebsite_LooksTheUrlUpByItsHashThenRegistersCrawlSource()
    {
        // Arrange
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));
        var writer = NewWriter(connection);

        // Act
        await writer.UpsertAsync(NewFullRequest(), Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains(connection.ExecutedCommands, c =>
            c.CommandText.Contains("[UrlHash] = CAST(HASHBYTES('SHA2_256', @Url) AS BINARY (32))", StringComparison.Ordinal));
        Assert.Contains(connection.ExecutedCommands, c =>
            c.CommandText.Contains("INSERT INTO [dbo].[CrawlSources]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpsertAsync_NewChurchNoWebsite_DoesNotRegisterCrawlSource()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var req = NewFullRequest() with { Website = null };

        // Act
        await writer.UpsertAsync(req, Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        Assert.DoesNotContain(connection.ExecutedCommands, c =>
            c.CommandText.Contains("INSERT INTO [dbo].[CrawlSources]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpsertAsync_WithServiceSchedules_ReplacesThemAndInsertsOnlyTheParseableOnesInOneCommand()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var earlyWeekDay = Generated.NewEarlyWeekDayOfWeek();
        var lateWeekDay = Generated.NewLateWeekDayOfWeek();
        var invalidDayOfWeek = Generated.NewInvalidDayOfWeek();
        var unparseableServiceTime = Generated.NewNonNumericToken();
        var morningSchedule = new ServiceScheduleData(earlyWeekDay, Generated.NewServiceTime(), Generated.NewChurchServiceDescription());
        var eveningSchedule = new ServiceScheduleData(lateWeekDay, Generated.NewServiceTime(), Generated.NewChurchServiceDescription());
        var unparseableSchedule = new ServiceScheduleData(invalidDayOfWeek, unparseableServiceTime, Generated.NewChurchServiceDescription());
        var req = NewFullRequest() with
        {
            ServiceSchedules = [morningSchedule, eveningSchedule, unparseableSchedule],
        };

        // Act
        await writer.UpsertAsync(req, Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var replace = SingleReplacement(connection, "[dbo].[ServiceSchedules]");
        Assert.Contains("DELETE FROM [dbo].[ServiceSchedules]", replace.CommandText, StringComparison.Ordinal);
        Assert.Contains(morningSchedule.Description, RowValues(replace));
        Assert.Contains(eveningSchedule.Description, RowValues(replace));
        Assert.DoesNotContain(unparseableSchedule.Description, RowValues(replace));
    }

    [Fact]
    public async Task UpsertAsync_ServiceScheduleDescriptionOverLimit_TruncatesToColumnLength()
    {
        // Arrange
        var descriptionPadding = Generated.NewPaddingChar();
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var scheduledDay = Generated.NewDayOfWeek();
        var overlongSchedule = new ServiceScheduleData(
            scheduledDay,
            Generated.NewServiceTime(),
            new string(descriptionPadding, ChurchWriter.ServiceScheduleDescriptionMaxLength + Generated.NewOverflowMargin()));
        var req = NewFullRequest() with { ServiceSchedules = [overlongSchedule] };

        // Act
        await writer.UpsertAsync(req, Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var replace = SingleReplacement(connection, "[dbo].[ServiceSchedules]");
        Assert.Contains(
            new string(descriptionPadding, ChurchWriter.ServiceScheduleDescriptionMaxLength),
            RowValues(replace));
    }

    [Fact]
    public async Task UpsertAsync_WithMinistries_ReplacesThemAndInsertsOnlyTheNamedOnesInOneCommand()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var describedMinistry = new MinistryData(Generated.NewMinistryName(), Generated.NewMinistryDescription());
        var undescribedMinistry = new MinistryData(Generated.NewMinistryName(), null);
        var blankNameMinistry = new MinistryData(
            Generated.NewBlankRun(), Generated.NewMinistryDescription());
        var req = NewFullRequest() with
        {
            Ministries = [describedMinistry, undescribedMinistry, blankNameMinistry],
        };

        // Act
        await writer.UpsertAsync(req, Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var replace = SingleReplacement(connection, "[dbo].[Ministries]");
        Assert.Contains("DELETE FROM [dbo].[Ministries]", replace.CommandText, StringComparison.Ordinal);
        Assert.Contains(describedMinistry.Name, RowValues(replace));
        Assert.Contains(undescribedMinistry.Name, RowValues(replace));
        Assert.DoesNotContain(blankNameMinistry.Description, RowValues(replace));
    }

    [Fact]
    public async Task UpsertAsync_MinistryFieldsOverLimit_TruncateToColumnLength()
    {
        // Arrange
        var namePadding = Generated.NewPaddingChar();
        var descriptionPadding = Generated.NewPaddingChar();
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var overlongMinistry = new MinistryData(
            new string(namePadding, ChurchWriter.MinistryNameMaxLength + Generated.NewOverflowMargin()),
            new string(descriptionPadding, ChurchWriter.MinistryDescriptionMaxLength + Generated.NewOverflowMargin()));
        var req = NewFullRequest() with { Ministries = [overlongMinistry] };

        // Act
        await writer.UpsertAsync(req, Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var replace = SingleReplacement(connection, "[dbo].[Ministries]");
        Assert.Contains(new string(namePadding, ChurchWriter.MinistryNameMaxLength), RowValues(replace));
        Assert.Contains(new string(descriptionPadding, ChurchWriter.MinistryDescriptionMaxLength), RowValues(replace));
    }

    [Fact]
    public async Task UpsertAsync_WithCampuses_ReplacesThemAndInsertsOnlyTheCompleteOnesInOneCommand()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var completeCampus = new CampusData(
            Generated.NewCampusName(), Generated.NewStreet(), Generated.NewCity(), Generated.NewStateCodeText(), Generated.NewZip(), Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude());
        var blankCityCampus = new CampusData(
            Generated.NewCampusName(),
            null,
            string.Empty,
            Generated.NewStateCodeText(),
            Generated.NewZip(),
            Generated.NewGeocodedLatitude(),
            Generated.NewGeocodedLongitude());
        var req = NewFullRequest() with { Campuses = [completeCampus, blankCityCampus] };

        // Act
        await writer.UpsertAsync(req, Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var replace = SingleReplacement(connection, "[dbo].[Campuses]");
        Assert.Contains("DELETE FROM [dbo].[Campuses]", replace.CommandText, StringComparison.Ordinal);
        Assert.Contains(completeCampus.Name, RowValues(replace));
        Assert.DoesNotContain(blankCityCampus.Name, RowValues(replace));
    }

    [Fact]
    public async Task UpsertAsync_CampusFieldsOverLimit_TruncateToColumnLength()
    {
        // Arrange
        var namePadding = Generated.NewPaddingChar();
        var streetPadding = Generated.NewPaddingChar();
        var cityPadding = Generated.NewPaddingChar();
        var overlongZipDigits = Generated.NewOverlongZipDigits();
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var overlongCampus = new CampusData(
            new string(namePadding, ChurchWriter.CampusNameMaxLength + Generated.NewOverflowMargin()),
            new string(streetPadding, ChurchWriter.StreetMaxLength + Generated.NewOverflowMargin()),
            new string(cityPadding, ChurchWriter.CityMaxLength + Generated.NewOverflowMargin()),
            Generated.NewStateCodeText(),
            overlongZipDigits,
            Generated.NewGeocodedLatitude(),
            Generated.NewGeocodedLongitude());
        var req = NewFullRequest() with { Campuses = [overlongCampus] };

        // Act
        await writer.UpsertAsync(req, Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var replace = SingleReplacement(connection, "[dbo].[Campuses]");
        Assert.Contains(new string(namePadding, ChurchWriter.CampusNameMaxLength), RowValues(replace));
        Assert.Contains(new string(streetPadding, ChurchWriter.StreetMaxLength), RowValues(replace));
        Assert.Contains(new string(cityPadding, ChurchWriter.CityMaxLength), RowValues(replace));
        Assert.Contains(overlongZipDigits[..ChurchWriter.ZipMaxLength], RowValues(replace));
    }

    [Fact]
    public async Task UpdateCoordinatesAsync_RowAffected_UpdatesAndPublishesConfidence()
    {
        // Arrange
        var churchId = Guid.NewGuid();
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var (senders, sent) = FakeServiceBus.CreateSenders();
        var writer = new ChurchWriter(connection, senders);

        // Act
        var updated = await writer.UpdateCoordinatesAsync(
            churchId, Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

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
        var churchId = Guid.NewGuid();
        var connection = new FakeDbConnection();
        var (senders, sent) = FakeServiceBus.CreateSenders();
        var writer = new ChurchWriter(connection, senders);

        // Act
        var updated = await writer.UpdateCoordinatesAsync(
            churchId, Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        Assert.False(updated);
        Assert.Empty(sent);
    }

    [Fact]
    public async Task UpdateCampusCoordinatesAsync_RowAffected_StampsUpdatedAtAndPublishesNoConfidenceRequest()
    {
        // Arrange
        var campusId = Generated.NewCampusId();
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var (senders, sent) = FakeServiceBus.CreateSenders();
        var writer = new ChurchWriter(connection, senders);

        // Act
        var updated = await writer.UpdateCampusCoordinatesAsync(
            campusId, Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        Assert.True(updated);
        var campusUpdate = connection.ExecutedCommands.Single(command =>
            command.CommandText.Contains("UPDATE [dbo].[Campuses]", StringComparison.Ordinal));
        Assert.Contains("[UpdatedAt] = @Now", campusUpdate.CommandText, StringComparison.Ordinal);
        Assert.IsType<DateTimeOffset>(campusUpdate.Parameters[ChurchSqlParameters.Now].Value);
        Assert.Equal(campusId, campusUpdate.Parameters[ChurchSqlParameters.Id].Value);
        Assert.Empty(sent);
    }

    [Fact]
    public async Task UpdateCampusCoordinatesAsync_NoRow_ReturnsFalse()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);

        // Act
        var updated = await writer.UpdateCampusCoordinatesAsync(
            Generated.NewCampusId(),
            Generated.NewGeocodedLatitude(),
            Generated.NewGeocodedLongitude(),
            TestContext.Current.CancellationToken);

        // Assert
        Assert.False(updated);
    }

    private static FakeDbCommand SingleChurchInsert(FakeDbConnection connection) =>
        connection.ExecutedCommands.Single(command => command.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));

    private static FakeDbCommand SingleReplacement(FakeDbConnection connection, string childTable) =>
        connection.ExecutedCommands.Single(command => command.CommandText.Contains($"INSERT INTO {childTable}", StringComparison.Ordinal));

    private static IReadOnlyList<string?> RowValues(FakeDbCommand command)
    {
        using var rows = JsonDocument.Parse(Assert.IsType<string>(command.Parameters[ChurchSqlParameters.Rows].Value));
        return [.. rows.RootElement.EnumerateArray().SelectMany(row => row.EnumerateObject()).Select(column => column.Value.ToString())];
    }

    private static ChurchWriter NewWriter(FakeDbConnection connection) =>
        new(connection, FakeServiceBus.CreateSenders().Senders);

    private static GeocodingRequest NewFullRequest() =>
        NewFullRequest(Generated.NewChurchName(), Generated.NewCity(), Generated.NewStateCodeText());

    private static GeocodingRequest NewFullRequest(string canonicalName, string city, string state)
    {
        var crawlSourceId = Generated.NewCrawlSourceId();
        return new GeocodingRequest(
            CrawlSourceId: crawlSourceId,
            CanonicalName: canonicalName,
            Street: Generated.NewStreet(),
            City: city,
            State: state,
            Zip: Generated.NewZip(),
            PhoneNumber: Generated.NewPhoneNumber(),
            Website: Generated.NewWebsite(),
            EmailAddress: Generated.NewEmailAddress(),
            WorshipStyle: Generated.NewWorshipStyleCodeOtherThanUnknown(),
            PrimaryLanguage: Generated.NewLanguageName(),
            AcceptsLGBTQ: true,
            WheelchairAccessible: false,
            HasNursery: true,
            HasYouthProgram: false,
            Confidence: Generated.NewConfidence());
    }

    private static string ExpectedSlug(string canonicalName, string city, string state) =>
        $"{canonicalName}-{city}-{state.ToLowerInvariant()}";
}
