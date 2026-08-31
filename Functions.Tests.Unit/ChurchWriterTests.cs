namespace Functions.Tests.Unit;

using System.Data;
using System.Globalization;
using Churches;
using TestSupport;

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
        await writer.UpsertAsync(NewFullRequest(), NewLatitude(), NewLongitude(), TestContext.Current.CancellationToken);

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
        await writer.UpsertAsync(NewFullRequest(), NewLatitude(), NewLongitude(), TestContext.Current.CancellationToken);

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
            writer.UpsertAsync(req, 0m, 0m, TestContext.Current.CancellationToken));

        // Assert
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
        var req = NewFullRequest() with
        {
            AcceptsLGBTQ = null,
            WheelchairAccessible = null,
            HasNursery = null,
            HasYouthProgram = null,
        };

        // Act
        await writer.UpsertAsync(req, 0m, 0m, TestContext.Current.CancellationToken);

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
        await writer.UpsertAsync(req, NewLatitude(), NewLongitude(), TestContext.Current.CancellationToken);

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
        await writer.UpsertAsync(req, NewLatitude(), NewLongitude(), TestContext.Current.CancellationToken);

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
        await writer.UpsertAsync(req, NewLatitude(), NewLongitude(), TestContext.Current.CancellationToken);

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
            Website = $"http://{websiteHost}/",
        };

        // Act
        await writer.UpsertAsync(req, NewLatitude(), NewLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        Assert.Equal($"+1{areaCode}{exchange}{lineNumber}", insert.Parameters["@Phone"].Value);
        Assert.Equal(zipFiveDigits, insert.Parameters["@Zip"].Value);
        Assert.Equal($"https://{websiteHost}", insert.Parameters["@Website"].Value);
    }

    [Fact]
    public async Task UpsertAsync_CanonicalNameOverLimit_TruncatesToColumnLength()
    {
        // Arrange
        var namePadding = NewPaddingChar();
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var req = NewFullRequest() with
        {
            CanonicalName = new string(namePadding, ChurchWriter.CanonicalNameMaxLength + NewOverflowMargin()),
        };

        // Act
        await writer.UpsertAsync(req, NewLatitude(), NewLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        Assert.Equal(new string(namePadding, ChurchWriter.CanonicalNameMaxLength), insert.Parameters["@Name"].Value);
    }

    [Fact]
    public async Task UpsertAsync_StreetEmailAndLanguageOverLimit_TruncateToColumnLength()
    {
        // Arrange
        var streetPadding = NewPaddingChar();
        var emailPadding = NewPaddingChar();
        var languagePadding = NewPaddingChar();
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var req = NewFullRequest() with
        {
            Street = new string(streetPadding, ChurchWriter.StreetMaxLength + NewOverflowMargin()),
            EmailAddress = new string(emailPadding, ChurchWriter.EmailMaxLength + NewOverflowMargin()),
            PrimaryLanguage = new string(languagePadding, ChurchWriter.PrimaryLanguageMaxLength + NewOverflowMargin()),
        };

        // Act
        await writer.UpsertAsync(req, NewLatitude(), NewLongitude(), TestContext.Current.CancellationToken);

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
            Website = new string(NewPaddingChar(), ChurchWriter.WebsiteMaxLength),
        };

        // Act
        await writer.UpsertAsync(req, NewLatitude(), NewLongitude(), TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        var website = Assert.IsType<string>(insert.Parameters["@Website"].Value);
        Assert.Equal(ChurchWriter.WebsiteMaxLength, website.Length);
        Assert.StartsWith("https://", website, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpsertAsync_LongNameAndCity_SlugTruncatedToColumnLength()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var req = NewFullRequest() with
        {
            CanonicalName = new string(NewPaddingChar(), ChurchWriter.CanonicalNameMaxLength + NewOverflowMargin()),
            City = new string(NewPaddingChar(), ChurchWriter.CityMaxLength + NewOverflowMargin()),
        };

        // Act
        await writer.UpsertAsync(req, NewLatitude(), NewLongitude(), TestContext.Current.CancellationToken);

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
        await writer.UpsertAsync(req, NewLatitude(), NewLongitude(), TestContext.Current.CancellationToken);

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
        await writer.UpsertAsync(req, NewLatitude(), NewLongitude(), TestContext.Current.CancellationToken);

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
        await writer.UpsertAsync(NewFullRequest(), NewLatitude(), NewLongitude(), TestContext.Current.CancellationToken);

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
            writer.UpsertAsync(req, NewLatitude(), NewLongitude(), TestContext.Current.CancellationToken));

        // Assert
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
        var req = NewFullRequest() with { State = NewFullStateName() };

        // Act
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            writer.UpsertAsync(req, NewLatitude(), NewLongitude(), TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal("state", ex.ParamName);
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
            NewLatitude(),
            NewLongitude(),
            TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        Assert.Equal($"{ExpectedSlug(canonicalName, city, state)}-2", insert.Parameters["@Slug"].Value);
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
        await writer.UpsertAsync(NewFullRequest(), NewLatitude(), NewLongitude(), TestContext.Current.CancellationToken);

        // Assert
        Assert.DoesNotContain(
            connection.ExecutedCommands,
            c => c.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpsertAsync_AttributeFieldsOverLimit_TruncateToColumnLength()
    {
        // Arrange
        var keyPadding = NewPaddingChar();
        var valuePadding = NewPaddingChar();
        var sourcePadding = NewPaddingChar();
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var overlongAttribute = new ChurchAttributeData(
            new string(keyPadding, ChurchWriter.AttributeKeyMaxLength + NewOverflowMargin()),
            new string(valuePadding, ChurchWriter.AttributeValueMaxLength + NewOverflowMargin()),
            new string(sourcePadding, ChurchWriter.AttributeSourceMaxLength + NewOverflowMargin()),
            NewConfidence());
        var req = NewFullRequest() with { Attributes = [overlongAttribute] };

        // Act
        await writer.UpsertAsync(req, NewLatitude(), NewLongitude(), TestContext.Current.CancellationToken);

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
            NewNteeCode(),
            ChurchImportSources.Irs,
            ChurchImportConfidence.Irs);
        var req = NewFullRequest() with { Attributes = [nteeAttribute] };

        // Act
        await writer.UpsertAsync(req, NewLatitude(), NewLongitude(), TestContext.Current.CancellationToken);

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
        await writer.UpsertAsync(NewFullRequest(), NewLatitude(), NewLongitude(), TestContext.Current.CancellationToken);

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
        await writer.UpsertAsync(NewFullRequest(), NewLatitude(), NewLongitude(), TestContext.Current.CancellationToken);

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
        var earlyWeekDay = NewDayOfWeek(0, 3);
        var lateWeekDay = NewDayOfWeek(3, 7);
        var invalidDayOfWeek = NewDayOfWeek(7, 256);
        var unparseableServiceTime = LowercaseToken(8);
        var morningSchedule = new ServiceScheduleData(earlyWeekDay, TestValues.NewServiceTime(), TestValues.NewServiceDescription());
        var eveningSchedule = new ServiceScheduleData(lateWeekDay, TestValues.NewServiceTime(), TestValues.NewServiceDescription());
        var unparseableSchedule = new ServiceScheduleData(invalidDayOfWeek, unparseableServiceTime, TestValues.NewServiceDescription());
        var req = NewFullRequest() with
        {
            ServiceSchedules = [morningSchedule, eveningSchedule, unparseableSchedule],
        };

        // Act
        await writer.UpsertAsync(req, NewLatitude(), NewLongitude(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains(connection.ExecutedCommands, c =>
            c.CommandText.Contains("DELETE FROM [dbo].[ServiceSchedules]", StringComparison.Ordinal));
        Assert.Equal(2, connection.ExecutedCommands.Count(c =>
            c.CommandText.Contains("INSERT INTO [dbo].[ServiceSchedules]", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task UpsertAsync_ServiceScheduleDescriptionOverLimit_TruncatesToColumnLength()
    {
        // Arrange
        var descriptionPadding = NewPaddingChar();
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var scheduledDay = NewDayOfWeek(0, 7);
        var overlongSchedule = new ServiceScheduleData(
            scheduledDay,
            TestValues.NewServiceTime(),
            new string(descriptionPadding, ChurchWriter.ServiceScheduleDescriptionMaxLength + NewOverflowMargin()));
        var req = NewFullRequest() with { ServiceSchedules = [overlongSchedule] };

        // Act
        await writer.UpsertAsync(req, NewLatitude(), NewLongitude(), TestContext.Current.CancellationToken);

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
        var blankNameMinistry = new MinistryData("  ", TestValues.NewMinistryDescription());
        var req = NewFullRequest() with
        {
            Ministries = [describedMinistry, undescribedMinistry, blankNameMinistry],
        };

        // Act
        await writer.UpsertAsync(req, NewLatitude(), NewLongitude(), TestContext.Current.CancellationToken);

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
        var namePadding = NewPaddingChar();
        var descriptionPadding = NewPaddingChar();
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var overlongMinistry = new MinistryData(
            new string(namePadding, ChurchWriter.MinistryNameMaxLength + NewOverflowMargin()),
            new string(descriptionPadding, ChurchWriter.MinistryDescriptionMaxLength + NewOverflowMargin()));
        var req = NewFullRequest() with { Ministries = [overlongMinistry] };

        // Act
        await writer.UpsertAsync(req, NewLatitude(), NewLongitude(), TestContext.Current.CancellationToken);

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
            TestValues.NewCampusName(), TestValues.NewStreet(), TestValues.NewCity(), TestValues.NewStateCode(), TestValues.NewZip(), NewLatitude(), NewLongitude());
        var blankCityCampus = new CampusData(
            TestValues.NewCampusName(), null, string.Empty, TestValues.NewStateCode(), TestValues.NewZip(), 0m, 0m);
        var req = NewFullRequest() with { Campuses = [completeCampus, blankCityCampus] };

        // Act
        await writer.UpsertAsync(req, NewLatitude(), NewLongitude(), TestContext.Current.CancellationToken);

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
        var namePadding = NewPaddingChar();
        var streetPadding = NewPaddingChar();
        var cityPadding = NewPaddingChar();
        var overlongZipDigits = Random.Shared.NextInt64(100000000000L, 1000000000000L).ToString(CultureInfo.InvariantCulture);
        var connection = new FakeDbConnection();
        var writer = NewWriter(connection);
        var overlongCampus = new CampusData(
            new string(namePadding, ChurchWriter.CampusNameMaxLength + NewOverflowMargin()),
            new string(streetPadding, ChurchWriter.StreetMaxLength + NewOverflowMargin()),
            new string(cityPadding, ChurchWriter.CityMaxLength + NewOverflowMargin()),
            TestValues.NewStateCode(),
            overlongZipDigits,
            NewLatitude(),
            NewLongitude());
        var req = NewFullRequest() with { Campuses = [overlongCampus] };

        // Act
        await writer.UpsertAsync(req, NewLatitude(), NewLongitude(), TestContext.Current.CancellationToken);

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
            churchId, NewLatitude(), NewLongitude(), TestContext.Current.CancellationToken);

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
            churchId, NewLatitude(), NewLongitude(), TestContext.Current.CancellationToken);

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
        var worshipStyle = Random.Shared.Next(1, 6);
        return new GeocodingRequest(
            CrawlSourceId: crawlSourceId,
            CanonicalName: canonicalName,
            Street: TestValues.NewStreet(),
            City: city,
            State: state,
            Zip: TestValues.NewZip(),
            PhoneNumber: TestValues.NewPhoneNumber(),
            Website: $"https://{TestValues.NewHost()}",
            EmailAddress: TestValues.NewEmailAddress(),
            WorshipStyle: worshipStyle,
            PrimaryLanguage: TestValues.NewLanguageName(),
            AcceptsLGBTQ: true,
            WheelchairAccessible: false,
            HasNursery: true,
            HasYouthProgram: false,
            Confidence: NewConfidence());
    }

    private static string ExpectedSlug(string canonicalName, string city, string state) =>
        $"{canonicalName}-{city}-{state.ToLowerInvariant()}";

    private static string LowercaseToken(int length) =>
        string.Concat(Enumerable.Range(0, length).Select(_ => (char)Random.Shared.Next('a', 'z' + 1)));

    private static string NewFullStateName() => $"state{LowercaseToken(10)}";

    private static string NewNteeCode() => $"X{Random.Shared.Next(10, 100).ToString(CultureInfo.InvariantCulture)}";

    private static byte NewDayOfWeek(int minInclusive, int maxExclusive) =>
        (byte)Random.Shared.Next(minInclusive, maxExclusive);

    private static char NewPaddingChar() => (char)Random.Shared.Next('a', 'z' + 1);

    private static int NewOverflowMargin() => Random.Shared.Next(1, 100);

    private static decimal NewConfidence() => Math.Round((decimal)Random.Shared.NextDouble(), 2);

    private static decimal NewLatitude() => Math.Round(((decimal)Random.Shared.NextDouble() * 40m) + 1m, 4);

    private static decimal NewLongitude() => -Math.Round(((decimal)Random.Shared.NextDouble() * 100m) + 1m, 4);
}
