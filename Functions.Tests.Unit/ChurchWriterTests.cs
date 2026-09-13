namespace Functions.Tests.Unit;

using System.Data;
using System.Globalization;
using Churches;
using TestSupport;
using static ChurchWriterFixtureConstants;

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
        await writer.UpsertAsync(NewFullRequest(), TestValues.NewGeocodedLatitude(), TestValues.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(LookupThenInsertThenConfidence, connection.ExecutedCommands.Count);
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
        await writer.UpsertAsync(NewFullRequest(), TestValues.NewGeocodedLatitude(), TestValues.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

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
            writer.UpsertAsync(req, TestValues.NewGeocodedLatitude(), TestValues.NewGeocodedLongitude(), TestContext.Current.CancellationToken));

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
        await writer.UpsertAsync(req, TestValues.NewGeocodedLatitude(), TestValues.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        Assert.Equal(DBNull.Value, insert.Parameters["@Lgbtq"].Value);
        Assert.Equal(DBNull.Value, insert.Parameters["@Youth"].Value);
    }

    [Fact]
    public async Task UpsertAsync_NewChurch_BindsCreatedAtAsDateTimeOffset()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var req = NewFullRequest();

        // Act
        await writer.UpsertAsync(req, TestValues.NewGeocodedLatitude(), TestValues.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        Assert.IsType<DateTimeOffset>(insert.Parameters["@Now"].Value);
    }

    [Fact]
    public async Task UpsertAsync_NewChurch_BindsCreatedAtInUtc()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var req = NewFullRequest();

        // Act
        await writer.UpsertAsync(req, TestValues.NewGeocodedLatitude(), TestValues.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        Assert.Equal(TimeSpan.Zero, Assert.IsType<DateTimeOffset>(insert.Parameters["@Now"].Value).Offset);
    }

    [Fact]
    public async Task UpsertAsync_NewChurchPopulatedOptionals_BindsValues()
    {
        // Arrange
        var canonicalName = TestValues.NewChurchName();
        var city = TestValues.NewCity();
        var state = TestValues.NewStateCode();
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var req = NewFullRequest(canonicalName, city, state);

        // Act
        await writer.UpsertAsync(req, TestValues.NewGeocodedLatitude(), TestValues.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        Assert.Equal(canonicalName, insert.Parameters["@Name"].Value);
        Assert.Equal(ExpectedSlug(canonicalName, city, state), insert.Parameters["@Slug"].Value);
        Assert.True(insert.Parameters["@Lgbtq"].Value is true);
        Assert.True(insert.Parameters["@Youth"].Value is false);
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
        var websiteHost = TestValues.NewHost();
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var req = NewFullRequest() with
        {
            PhoneNumber = $"({areaCode}) {exchange}-{lineNumber}",
            Zip = $"{zipFiveDigits}-{zipPlusFour}",
            Website = $"{Uri.UriSchemeHttp}://{websiteHost}/",
        };

        // Act
        await writer.UpsertAsync(req, TestValues.NewGeocodedLatitude(), TestValues.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        Assert.Equal(
            $"{NorthAmericanCountryCode}{areaCode}{exchange}{lineNumber}",
            insert.Parameters["@Phone"].Value);
        Assert.Equal(zipFiveDigits, insert.Parameters["@Zip"].Value);
        Assert.Equal($"{Uri.UriSchemeHttps}://{websiteHost}", insert.Parameters["@Website"].Value);
    }

    [Fact]
    public async Task UpsertAsync_CanonicalNameOverLimit_TruncatesToColumnLength()
    {
        // Arrange
        var namePadding = TestValues.NewPaddingChar();
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var req = NewFullRequest() with
        {
            CanonicalName = new string(namePadding, ChurchWriter.CanonicalNameMaxLength + TestValues.NewOverflowMargin()),
        };

        // Act
        await writer.UpsertAsync(req, TestValues.NewGeocodedLatitude(), TestValues.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        Assert.Equal(new string(namePadding, ChurchWriter.CanonicalNameMaxLength), insert.Parameters["@Name"].Value);
    }

    [Fact]
    public async Task UpsertAsync_StreetEmailAndLanguageOverLimit_TruncateToColumnLength()
    {
        // Arrange
        var streetPadding = TestValues.NewPaddingChar();
        var emailPadding = TestValues.NewPaddingChar();
        var languagePadding = TestValues.NewPaddingChar();
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var req = NewFullRequest() with
        {
            Street = new string(streetPadding, ChurchWriter.StreetMaxLength + TestValues.NewOverflowMargin()),
            EmailAddress = new string(emailPadding, ChurchWriter.EmailMaxLength + TestValues.NewOverflowMargin()),
            PrimaryLanguage = new string(languagePadding, ChurchWriter.PrimaryLanguageMaxLength + TestValues.NewOverflowMargin()),
        };

        // Act
        await writer.UpsertAsync(req, TestValues.NewGeocodedLatitude(), TestValues.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        Assert.Equal(new string(streetPadding, ChurchWriter.StreetMaxLength), insert.Parameters["@Street"].Value);
        Assert.Equal(new string(emailPadding, ChurchWriter.EmailMaxLength), insert.Parameters["@Email"].Value);
        Assert.Equal(new string(languagePadding, ChurchWriter.PrimaryLanguageMaxLength), insert.Parameters["@Lang"].Value);
    }

    [Fact]
    public async Task UpsertAsync_NormalizedWebsiteOverLimit_TruncatesToColumnLength()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var req = NewFullRequest() with
        {
            Website = new string(TestValues.NewPaddingChar(), ChurchWriter.WebsiteMaxLength),
        };

        // Act
        await writer.UpsertAsync(req, TestValues.NewGeocodedLatitude(), TestValues.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        var website = Assert.IsType<string>(insert.Parameters["@Website"].Value);
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
            CanonicalName = new string(TestValues.NewPaddingChar(), ChurchWriter.CanonicalNameMaxLength + TestValues.NewOverflowMargin()),
            City = new string(TestValues.NewPaddingChar(), ChurchWriter.CityMaxLength + TestValues.NewOverflowMargin()),
        };

        // Act
        await writer.UpsertAsync(req, TestValues.NewGeocodedLatitude(), TestValues.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        var slugValue = Assert.IsType<string>(insert.Parameters["@Slug"].Value);
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
        var req = NewFullRequest() with { DenominationName = TestValues.NewDenominationName() };

        // Act
        await writer.UpsertAsync(req, TestValues.NewGeocodedLatitude(), TestValues.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
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
        var req = NewFullRequest() with { DenominationName = TestValues.NewDenominationName() };

        // Act
        await writer.UpsertAsync(req, TestValues.NewGeocodedLatitude(), TestValues.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        Assert.Equal(DBNull.Value, insert.Parameters["@Denom"].Value);
    }

    [Fact]
    public async Task UpsertAsync_NoDenominationName_DoesNotQueryDenominations()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);

        // Act
        await writer.UpsertAsync(NewFullRequest(), TestValues.NewGeocodedLatitude(), TestValues.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

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
            writer.UpsertAsync(req, TestValues.NewGeocodedLatitude(), TestValues.NewGeocodedLongitude(), TestContext.Current.CancellationToken));

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
        var req = NewFullRequest() with { State = TestValues.NewFullStateName() };

        // Act
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            writer.UpsertAsync(req, TestValues.NewGeocodedLatitude(), TestValues.NewGeocodedLongitude(), TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(SharedChurchBuilderStateParameter, ex.ParamName);
        Assert.DoesNotContain(connection.ExecutedCommands, c =>
            c.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpsertAsync_SlugCollision_AppendsSuffix()
    {
        // Arrange
        var canonicalName = TestValues.NewChurchName();
        var city = TestValues.NewCity();
        var state = TestValues.NewStateCode();
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));
        connection.Enqueue(FakeDbCommand.WithScalarResult(1));
        connection.Enqueue(FakeDbCommand.WithScalarResult(0));
        var writer = NewWriter(connection);

        // Act
        await writer.UpsertAsync(
            NewFullRequest(canonicalName, city, state),
            TestValues.NewGeocodedLatitude(),
            TestValues.NewGeocodedLongitude(),
            TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        Assert.Equal(
            $"{ExpectedSlug(canonicalName, city, state)}-{SecondSlugOrdinal}",
            insert.Parameters["@Slug"].Value);
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
        await writer.UpsertAsync(NewFullRequest(), TestValues.NewGeocodedLatitude(), TestValues.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        Assert.DoesNotContain(
            connection.ExecutedCommands,
            c => c.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpsertAsync_AttributeFieldsOverLimit_TruncateToColumnLength()
    {
        // Arrange
        var keyPadding = TestValues.NewPaddingChar();
        var valuePadding = TestValues.NewPaddingChar();
        var sourcePadding = TestValues.NewPaddingChar();
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var overlongAttribute = new ChurchAttributeData(
            new string(keyPadding, ChurchWriter.AttributeKeyMaxLength + TestValues.NewOverflowMargin()),
            new string(valuePadding, ChurchWriter.AttributeValueMaxLength + TestValues.NewOverflowMargin()),
            new string(sourcePadding, ChurchWriter.AttributeSourceMaxLength + TestValues.NewOverflowMargin()),
            TestValues.NewConfidence());
        var req = NewFullRequest() with { Attributes = [overlongAttribute] };

        // Act
        await writer.UpsertAsync(req, TestValues.NewGeocodedLatitude(), TestValues.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleInsert(connection, "INSERT INTO [dbo].[ChurchAttributes]");
        Assert.Equal(new string(keyPadding, ChurchWriter.AttributeKeyMaxLength), insert.Parameters["@Key"].Value);
        Assert.Equal(new string(valuePadding, ChurchWriter.AttributeValueMaxLength), insert.Parameters["@Value"].Value);
        Assert.Equal(new string(sourcePadding, ChurchWriter.AttributeSourceMaxLength), insert.Parameters["@Source"].Value);
    }

    [Fact]
    public async Task UpsertAsync_WithAttributes_RefreshesAttributesAndPublishesRecalc()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var (factory, sent) = FakeServiceBus.Create();
        var writer = new ChurchWriter(connection, factory);
        var nteeAttribute = new ChurchAttributeData(
            ChurchAttributeKeys.NteeCode,
            TestValues.NewNteeCode(),
            ChurchImportSources.Irs,
            ChurchImportConfidence.Irs);
        var req = NewFullRequest() with { Attributes = [nteeAttribute] };

        // Act
        await writer.UpsertAsync(req, TestValues.NewGeocodedLatitude(), TestValues.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

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
        await writer.UpsertAsync(NewFullRequest(), TestValues.NewGeocodedLatitude(), TestValues.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

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
        await writer.UpsertAsync(NewFullRequest(), TestValues.NewGeocodedLatitude(), TestValues.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

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
        var req = NewFullRequest() with { Website = null };

        // Act
        await writer.UpsertAsync(req, TestValues.NewGeocodedLatitude(), TestValues.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

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
        var earlyWeekDay = TestValues.NewEarlyWeekDayOfWeek();
        var lateWeekDay = TestValues.NewLateWeekDayOfWeek();
        var invalidDayOfWeek = TestValues.NewInvalidDayOfWeek();
        var unparseableServiceTime = TestValues.NewNonNumericToken();
        var morningSchedule = new ServiceScheduleData(earlyWeekDay, TestValues.NewServiceTime(), TestValues.NewServiceDescription());
        var eveningSchedule = new ServiceScheduleData(lateWeekDay, TestValues.NewServiceTime(), TestValues.NewServiceDescription());
        var unparseableSchedule = new ServiceScheduleData(invalidDayOfWeek, unparseableServiceTime, TestValues.NewServiceDescription());
        var req = NewFullRequest() with
        {
            ServiceSchedules = [morningSchedule, eveningSchedule, unparseableSchedule],
        };

        // Act
        await writer.UpsertAsync(req, TestValues.NewGeocodedLatitude(), TestValues.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains(connection.ExecutedCommands, c =>
            c.CommandText.Contains("DELETE FROM [dbo].[ServiceSchedules]", StringComparison.Ordinal));
        Assert.Equal(ValidRowsOfTheThreeSupplied, connection.ExecutedCommands.Count(c =>
            c.CommandText.Contains("INSERT INTO [dbo].[ServiceSchedules]", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task UpsertAsync_ServiceScheduleDescriptionOverLimit_TruncatesToColumnLength()
    {
        // Arrange
        var descriptionPadding = TestValues.NewPaddingChar();
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var scheduledDay = TestValues.NewDayOfWeek();
        var overlongSchedule = new ServiceScheduleData(
            scheduledDay,
            TestValues.NewServiceTime(),
            new string(descriptionPadding, ChurchWriter.ServiceScheduleDescriptionMaxLength + TestValues.NewOverflowMargin()));
        var req = NewFullRequest() with { ServiceSchedules = [overlongSchedule] };

        // Act
        await writer.UpsertAsync(req, TestValues.NewGeocodedLatitude(), TestValues.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleInsert(connection, "INSERT INTO [dbo].[ServiceSchedules]");
        Assert.Equal(
            new string(descriptionPadding, ChurchWriter.ServiceScheduleDescriptionMaxLength),
            insert.Parameters["@Desc"].Value);
    }

    [Fact]
    public async Task UpsertAsync_WithMinistries_ReplacesAndInsertsNamedOnes()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var describedMinistry = new MinistryData(TestValues.NewMinistryName(), TestValues.NewMinistryDescription());
        var undescribedMinistry = new MinistryData(TestValues.NewMinistryName(), null);
        var blankNameMinistry = new MinistryData(
            TestValues.NewBlankRun(), TestValues.NewMinistryDescription());
        var req = NewFullRequest() with
        {
            Ministries = [describedMinistry, undescribedMinistry, blankNameMinistry],
        };

        // Act
        await writer.UpsertAsync(req, TestValues.NewGeocodedLatitude(), TestValues.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains(connection.ExecutedCommands, c =>
            c.CommandText.Contains("DELETE FROM [dbo].[Ministries]", StringComparison.Ordinal));
        Assert.Equal(ValidRowsOfTheThreeSupplied, connection.ExecutedCommands.Count(c =>
            c.CommandText.Contains("INSERT INTO [dbo].[Ministries]", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task UpsertAsync_MinistryFieldsOverLimit_TruncateToColumnLength()
    {
        // Arrange
        var namePadding = TestValues.NewPaddingChar();
        var descriptionPadding = TestValues.NewPaddingChar();
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var overlongMinistry = new MinistryData(
            new string(namePadding, ChurchWriter.MinistryNameMaxLength + TestValues.NewOverflowMargin()),
            new string(descriptionPadding, ChurchWriter.MinistryDescriptionMaxLength + TestValues.NewOverflowMargin()));
        var req = NewFullRequest() with { Ministries = [overlongMinistry] };

        // Act
        await writer.UpsertAsync(req, TestValues.NewGeocodedLatitude(), TestValues.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleInsert(connection, "INSERT INTO [dbo].[Ministries]");
        Assert.Equal(new string(namePadding, ChurchWriter.MinistryNameMaxLength), insert.Parameters["@Name"].Value);
        Assert.Equal(new string(descriptionPadding, ChurchWriter.MinistryDescriptionMaxLength), insert.Parameters["@Desc"].Value);
    }

    [Fact]
    public async Task UpsertAsync_WithCampuses_ReplacesAndInsertsCompleteOnes()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var completeCampus = new CampusData(
            TestValues.NewCampusName(), TestValues.NewStreet(), TestValues.NewCity(), TestValues.NewStateCode(), TestValues.NewZip(), TestValues.NewGeocodedLatitude(), TestValues.NewGeocodedLongitude());
        var blankCityCampus = new CampusData(
            TestValues.NewCampusName(),
            null,
            string.Empty,
            TestValues.NewStateCode(),
            TestValues.NewZip(),
            TestValues.NewGeocodedLatitude(),
            TestValues.NewGeocodedLongitude());
        var req = NewFullRequest() with { Campuses = [completeCampus, blankCityCampus] };

        // Act
        await writer.UpsertAsync(req, TestValues.NewGeocodedLatitude(), TestValues.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

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
        var namePadding = TestValues.NewPaddingChar();
        var streetPadding = TestValues.NewPaddingChar();
        var cityPadding = TestValues.NewPaddingChar();
        var overlongZipDigits = TestValues.NewOverlongZipDigits();
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var overlongCampus = new CampusData(
            new string(namePadding, ChurchWriter.CampusNameMaxLength + TestValues.NewOverflowMargin()),
            new string(streetPadding, ChurchWriter.StreetMaxLength + TestValues.NewOverflowMargin()),
            new string(cityPadding, ChurchWriter.CityMaxLength + TestValues.NewOverflowMargin()),
            TestValues.NewStateCode(),
            overlongZipDigits,
            TestValues.NewGeocodedLatitude(),
            TestValues.NewGeocodedLongitude());
        var req = NewFullRequest() with { Campuses = [overlongCampus] };

        // Act
        await writer.UpsertAsync(req, TestValues.NewGeocodedLatitude(), TestValues.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleInsert(connection, "INSERT INTO [dbo].[Campuses]");
        Assert.Equal(new string(namePadding, ChurchWriter.CampusNameMaxLength), insert.Parameters["@Name"].Value);
        Assert.Equal(new string(streetPadding, ChurchWriter.StreetMaxLength), insert.Parameters["@Street"].Value);
        Assert.Equal(new string(cityPadding, ChurchWriter.CityMaxLength), insert.Parameters["@City"].Value);
        Assert.Equal(overlongZipDigits[..ChurchWriter.ZipMaxLength], insert.Parameters["@Zip"].Value);
    }

    [Fact]
    public async Task UpdateCoordinatesAsync_RowAffected_UpdatesAndPublishesConfidence()
    {
        // Arrange
        var churchId = Guid.NewGuid();
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var (factory, sent) = FakeServiceBus.Create();
        var writer = new ChurchWriter(connection, factory);

        // Act
        var updated = await writer.UpdateCoordinatesAsync(
            churchId, TestValues.NewGeocodedLatitude(), TestValues.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

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
        var (factory, sent) = FakeServiceBus.Create();
        var writer = new ChurchWriter(connection, factory);

        // Act
        var updated = await writer.UpdateCoordinatesAsync(
            churchId, TestValues.NewGeocodedLatitude(), TestValues.NewGeocodedLongitude(), TestContext.Current.CancellationToken);

        // Assert
        Assert.False(updated);
        Assert.Empty(sent);
    }

    private static FakeDbCommand SingleChurchInsert(FakeDbConnection connection) =>
        SingleInsert(connection, "INSERT INTO [dbo].[Churches]");

    private static FakeDbCommand SingleInsert(FakeDbConnection connection, string commandTextFragment) =>
        connection.ExecutedCommands.Single(c => c.CommandText.Contains(commandTextFragment, StringComparison.Ordinal));

    private static ChurchWriter NewWriter(FakeDbConnection connection) =>
        new(connection, FakeServiceBus.Create().Factory);

    private static GeocodingRequest NewFullRequest() =>
        NewFullRequest(TestValues.NewChurchName(), TestValues.NewCity(), TestValues.NewStateCode());

    private static GeocodingRequest NewFullRequest(string canonicalName, string city, string state)
    {
        var crawlSourceId = Guid.NewGuid();
        return new GeocodingRequest(
            CrawlSourceId: crawlSourceId,
            CanonicalName: canonicalName,
            Street: TestValues.NewStreet(),
            City: city,
            State: state,
            Zip: TestValues.NewZip(),
            PhoneNumber: TestValues.NewPhoneNumber(),
            Website: TestValues.NewWebsite(),
            EmailAddress: TestValues.NewEmailAddress(),
            WorshipStyle: TestValues.NewWorshipStyle(),
            PrimaryLanguage: TestValues.NewLanguageName(),
            AcceptsLGBTQ: true,
            WheelchairAccessible: false,
            HasNursery: true,
            HasYouthProgram: false,
            Confidence: TestValues.NewConfidence());
    }

    private static string ExpectedSlug(string canonicalName, string city, string state) =>
        $"{canonicalName}-{city}-{state.ToLowerInvariant()}";
}
