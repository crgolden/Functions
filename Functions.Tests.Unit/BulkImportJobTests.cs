namespace Functions.Tests.Unit;

using System.Collections.Specialized;
using System.Data;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Churches;
using Churches.Import;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class BulkImportJobTests
{
    private const string NonLiturgicalNteeCode = "X20";

    private const string BaptistDenominationSlug = "baptist";

    [Fact]
    public void ParseIrsCsv_SingleRow_MapsNameStreetCityStateZip()
    {
        // Arrange
        var importedName = NewChurchName();
        var importedStreet = NewStreet();
        var importedCity = NewCity();
        var importedState = NewStateCode();
        var importedZip = NewZip();
        var csv = IrsCsv(
            [IrsCsvColumns.Name, IrsCsvColumns.Street, IrsCsvColumns.City, IrsCsvColumns.State, IrsCsvColumns.Zip, IrsCsvColumns.NteeCode],
            [[importedName, importedStreet, importedCity, importedState, importedZip, NonLiturgicalNteeCode]]);

        // Act
        var results = BulkImportJob.ParseIrsCsv(csv).ToList();

        // Assert
        var record = Assert.Single(results);
        Assert.Equal(importedName, record.CanonicalName);
        Assert.Equal(importedStreet, record.Street);
        Assert.Equal(importedCity, record.City);
        Assert.Equal(importedState, record.State);
        Assert.Equal(importedZip, record.Zip);
        Assert.Equal(ChurchWorshipStyles.Unknown, record.WorshipStyle);
        Assert.Equal(ChurchImportConfidence.Irs, record.Confidence);
    }

    [Fact]
    public void ParseIrsCsv_WithLatLonColumns_CarriesPreGeocodedCoordinates()
    {
        // Arrange
        var preGeocodedLatitude = NewLatitude();
        var preGeocodedLongitude = NewLongitude();
        var csv = IrsCsv(
            [IrsCsvColumns.Name, IrsCsvColumns.State, IrsCsvColumns.NteeCode, IrsCsvColumns.Latitude, IrsCsvColumns.Longitude],
            [[NewChurchName(), NewStateCode(), NonLiturgicalNteeCode, Decimal(preGeocodedLatitude), Decimal(preGeocodedLongitude)]]);

        // Act
        var results = BulkImportJob.ParseIrsCsv(csv).ToList();

        // Assert
        Assert.Equal(preGeocodedLatitude, results[0].Latitude);
        Assert.Equal(preGeocodedLongitude, results[0].Longitude);
    }

    [Fact]
    public void ParseIrsCsv_BlankLatLon_LeavesCoordinatesNull()
    {
        // Arrange
        var csv = IrsCsv(
            [IrsCsvColumns.Name, IrsCsvColumns.State, IrsCsvColumns.Latitude, IrsCsvColumns.Longitude],
            [[NewChurchName(), NewStateCode(), string.Empty, string.Empty]]);

        // Act
        var results = BulkImportJob.ParseIrsCsv(csv).ToList();

        // Assert
        Assert.Null(results[0].Latitude);
        Assert.Null(results[0].Longitude);
    }

    [Theory]
    [InlineData("33.44", "-112.07", true)]
    [InlineData("0", "0", false)]
    [InlineData("", "", false)]
    [InlineData("abc", "-112.07", false)]
    public void ParseCoordinates_TruthTable(string latitude, string longitude, bool expectCoordinates)
    {
        var (parsedLatitude, parsedLongitude) = BulkImportJob.ParseCoordinates(latitude, longitude);
        Assert.Equal(expectCoordinates, parsedLatitude.HasValue && parsedLongitude.HasValue);
    }

    [Theory]
    [InlineData(NteeCodes.Protestant)]
    [InlineData(NteeCodes.RomanCatholic)]
    public void ParseIrsCsv_LiturgicalNteeCode_MapsToLiturgicalWorshipStyle(string nteeCode)
    {
        // Arrange
        var csv = IrsCsv(
            [IrsCsvColumns.Name, IrsCsvColumns.State, IrsCsvColumns.NteeCode],
            [[NewChurchName(), NewStateCode(), nteeCode]]);

        // Act
        var results = BulkImportJob.ParseIrsCsv(csv).ToList();

        // Assert
        Assert.Equal(ChurchWorshipStyles.Liturgical, results[0].WorshipStyle);
    }

    [Fact]
    public void ParseIrsCsv_MissingNameColumn_SkipsRow()
    {
        // Arrange
        var csv = IrsCsv([IrsCsvColumns.Name, IrsCsvColumns.State], [[string.Empty, NewStateCode()]]);

        // Act
        var results = BulkImportJob.ParseIrsCsv(csv).ToList();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void ParseIrsCsv_MissingStateColumn_SkipsRow()
    {
        // Arrange
        var csv = IrsCsv([IrsCsvColumns.Name, IrsCsvColumns.State], [[NewChurchName(), string.Empty]]);

        // Act
        var results = BulkImportJob.ParseIrsCsv(csv).ToList();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void ParseIrsCsv_EmptyCsv_YieldsNothing()
    {
        // Act
        var results = BulkImportJob.ParseIrsCsv(string.Empty).ToList();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void ParseIrsCsv_HeaderOnly_YieldsNothing()
    {
        // Arrange
        var headerOnlyCsv = IrsCsv([IrsCsvColumns.Name, IrsCsvColumns.State, IrsCsvColumns.NteeCode], []);

        // Act
        var results = BulkImportJob.ParseIrsCsv(headerOnlyCsv).ToList();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void ParseIrsCsv_MultipleRows_ParsesAll()
    {
        // Arrange
        var firstImportedName = NewChurchName();
        var secondImportedName = NewChurchName();
        IReadOnlyList<IReadOnlyList<string>> importedRows =
        [
            [firstImportedName, NewStateCode(), NonLiturgicalNteeCode],
            [secondImportedName, NewStateCode(), NonLiturgicalNteeCode],
        ];
        var csv = IrsCsv([IrsCsvColumns.Name, IrsCsvColumns.State, IrsCsvColumns.NteeCode], importedRows);

        // Act
        var results = BulkImportJob.ParseIrsCsv(csv).ToList();

        // Assert
        Assert.Equal(2, results.Count);
        Assert.Equal(firstImportedName, results[0].CanonicalName);
        Assert.Equal(secondImportedName, results[1].CanonicalName);
    }

    [Fact]
    public void ParseOsm_SingleElement_MapsAllAddressFields()
    {
        // Arrange
        var importedName = NewChurchName();
        var importedStreet = NewStreetName();
        var importedCity = NewCity();
        var importedState = NewStateCode();
        var importedZip = NewZip();
        var importedPhone = NewPhoneNumber();
        var importedWebsite = NewWebsite();
        var importedEmail = NewEmailAddress();
        var tags = AddressTags(importedName, importedCity, importedState, importedZip);
        tags[OsmTags.Street] = importedStreet;
        tags[OsmTags.Phone] = importedPhone;
        tags[OsmTags.Website] = importedWebsite;
        tags[OsmTags.Email] = importedEmail;

        // Act
        var results = BulkImportJob.ParseOsm(OsmDocument(OsmElement(tags))).ToList();

        // Assert
        var record = Assert.Single(results);
        Assert.Equal(importedName, record.CanonicalName);
        Assert.Equal(importedStreet, record.Street);
        Assert.Equal(importedCity, record.City);
        Assert.Equal(importedState, record.State);
        Assert.Equal(importedZip, record.Zip);
        Assert.Equal(importedPhone, record.PhoneNumber);
        Assert.Equal(importedWebsite, record.Website);
        Assert.Equal(importedEmail, record.EmailAddress);
        Assert.Equal(ChurchImportConfidence.Osm, record.Confidence);
    }

    [Fact]
    public void ParseOsm_BlankEmailTag_NormalizesToNull()
    {
        // Arrange
        var tags = AddressTags(NewChurchName(), NewCity(), NewStateCode(), NewZip());
        tags[OsmTags.Email] = string.Empty;

        // Act
        var results = BulkImportJob.ParseOsm(OsmDocument(OsmElement(tags))).ToList();

        // Assert
        Assert.Null(results[0].EmailAddress);
    }

    [Fact]
    public void ParseOsm_ElementMissingName_SkipsRow()
    {
        // Arrange
        var tags = AddressTags(NewChurchName(), NewCity(), NewStateCode(), NewZip());
        tags.Remove(OsmTags.Name);

        // Act
        var results = BulkImportJob.ParseOsm(OsmDocument(OsmElement(tags))).ToList();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void ParseOsm_ElementMissingState_SkipsRow()
    {
        // Arrange
        var tags = AddressTags(NewChurchName(), NewCity(), NewStateCode(), NewZip());
        tags.Remove(OsmTags.State);

        // Act
        var results = BulkImportJob.ParseOsm(OsmDocument(OsmElement(tags))).ToList();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void ParseOsm_ElementMissingCity_SkipsRow()
    {
        // Arrange
        var tags = AddressTags(NewChurchName(), NewCity(), NewStateCode(), NewZip());
        tags.Remove(OsmTags.City);

        // Act
        var results = BulkImportJob.ParseOsm(OsmDocument(OsmElement(tags))).ToList();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void ParseOsm_ElementMissingPostcode_SkipsRow()
    {
        // Arrange
        var tags = AddressTags(NewChurchName(), NewCity(), NewStateCode(), NewZip());
        tags.Remove(OsmTags.Postcode);

        // Act
        var results = BulkImportJob.ParseOsm(OsmDocument(OsmElement(tags))).ToList();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void ParseOsm_ElementMissingTags_SkipsRow()
    {
        // Arrange
        var elementWithoutTags = new Dictionary<string, object>();

        // Act
        var results = BulkImportJob.ParseOsm(OsmDocument(elementWithoutTags)).ToList();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void ParseOsm_NoElementsKey_YieldsNothing()
    {
        // Arrange
        var documentWithoutElements = JsonSerializer.Serialize(new Dictionary<string, object>());

        // Act
        var results = BulkImportJob.ParseOsm(documentWithoutElements).ToList();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void ParseOsm_NodeWithLatLon_PopulatesNativeCoordinates()
    {
        // Arrange
        var nodeLatitude = NewLatitude();
        var nodeLongitude = NewLongitude();
        var tags = AddressTags(NewChurchName(), NewCity(), NewStateCode(), NewZip());
        var node = OsmElement(tags);
        node[OsmTags.Latitude] = nodeLatitude;
        node[OsmTags.Longitude] = nodeLongitude;

        // Act
        var record = Assert.Single(BulkImportJob.ParseOsm(OsmDocument(node)).ToList());

        // Assert
        Assert.Equal(nodeLatitude, record.Latitude);
        Assert.Equal(nodeLongitude, record.Longitude);
    }

    [Fact]
    public void ParseOsm_WayWithCenter_PopulatesNativeCoordinates()
    {
        // Arrange
        var centerLatitude = NewLatitude();
        var centerLongitude = NewLongitude();
        var tags = AddressTags(NewChurchName(), NewCity(), NewStateCode(), NewZip());
        var way = OsmElement(tags);
        way[OsmTags.Center] = new Dictionary<string, decimal>
        {
            [OsmTags.Latitude] = centerLatitude,
            [OsmTags.Longitude] = centerLongitude,
        };

        // Act
        var record = Assert.Single(BulkImportJob.ParseOsm(OsmDocument(way)).ToList());

        // Assert
        Assert.Equal(centerLatitude, record.Latitude);
        Assert.Equal(centerLongitude, record.Longitude);
    }

    [Fact]
    public void ParseOsm_NoCoordinates_LeavesCoordinatesNull()
    {
        // Arrange
        var tags = AddressTags(NewChurchName(), NewCity(), NewStateCode(), NewZip());

        // Act
        var record = Assert.Single(BulkImportJob.ParseOsm(OsmDocument(OsmElement(tags))).ToList());

        // Assert
        Assert.Null(record.Latitude);
        Assert.Null(record.Longitude);
    }

    [Fact]
    public void ParseOsm_MultiValuePhone_KeepsOnlyFirstNumber()
    {
        // Arrange
        var preferredPhone = NewPhoneNumber();
        var secondaryPhone = NewPhoneNumber();
        var tags = AddressTags(NewChurchName(), NewCity(), NewStateCode(), NewZip());
        tags[OsmTags.Phone] = string.Join(BulkImportJob.MultiValueSeparator, preferredPhone, secondaryPhone);

        // Act
        var record = Assert.Single(BulkImportJob.ParseOsm(OsmDocument(OsmElement(tags))).ToList());

        // Assert
        Assert.Equal(preferredPhone, record.PhoneNumber);
    }

    [Fact]
    public void ParseOsm_OverlongSinglePhone_DropsPhone()
    {
        // Arrange
        var overlongPhone = new string('9', BulkImportJob.MaxPhoneLength + 1);
        var tags = AddressTags(NewChurchName(), NewCity(), NewStateCode(), NewZip());
        tags[OsmTags.Phone] = overlongPhone;

        // Act
        var record = Assert.Single(BulkImportJob.ParseOsm(OsmDocument(OsmElement(tags))).ToList());

        // Assert
        Assert.Null(record.PhoneNumber);
    }

    [Fact]
    public void ParseOsm_MultiValueName_PrefersLatinSegment_TranslationFirst()
    {
        // Arrange
        var latinName = NewChurchName();
        var nonLatinName = NewNonLatinChurchName();
        var tags = AddressTags(string.Join(BulkImportJob.MultiValueSeparator, nonLatinName, latinName), NewCity(), NewStateCode(), NewZip());

        // Act
        var record = Assert.Single(BulkImportJob.ParseOsm(OsmDocument(OsmElement(tags))).ToList());

        // Assert
        Assert.Equal(latinName, record.CanonicalName);
    }

    [Fact]
    public void ParseOsm_MultiValueName_PrefersLatinSegment_TranslationSecond()
    {
        // Arrange
        var latinName = NewChurchName();
        var nonLatinName = NewNonLatinChurchName();
        var tags = AddressTags(string.Join(BulkImportJob.MultiValueSeparator, latinName, nonLatinName), NewCity(), NewStateCode(), NewZip());

        // Act
        var record = Assert.Single(BulkImportJob.ParseOsm(OsmDocument(OsmElement(tags))).ToList());

        // Assert
        Assert.Equal(latinName, record.CanonicalName);
    }

    [Fact]
    public void ParseOsm_MultiValueName_BothAscii_KeepsFirstSegment()
    {
        // Arrange
        var firstAsciiName = NewChurchName();
        var secondAsciiName = NewChurchName();
        var tags = AddressTags(string.Join(BulkImportJob.MultiValueSeparator, firstAsciiName, secondAsciiName), NewCity(), NewStateCode(), NewZip());

        // Act
        var record = Assert.Single(BulkImportJob.ParseOsm(OsmDocument(OsmElement(tags))).ToList());

        // Assert
        Assert.Equal(firstAsciiName, record.CanonicalName);
    }

    [Fact]
    public void ParseOsm_MultiValueName_TrailingEmptySegment_KeepsOnlyNonEmpty()
    {
        // Arrange
        var onlyPopulatedName = NewChurchName();
        var tags = AddressTags(onlyPopulatedName + BulkImportJob.MultiValueSeparator, NewCity(), NewStateCode(), NewZip());

        // Act
        var record = Assert.Single(BulkImportJob.ParseOsm(OsmDocument(OsmElement(tags))).ToList());

        // Assert
        Assert.Equal(onlyPopulatedName, record.CanonicalName);
    }

    [Fact]
    public void ParseOsm_HouseNumberAndStreet_CombinesIntoStreet()
    {
        // Arrange
        var houseNumber = NewHouseNumber();
        var streetName = NewStreetName();
        var tags = AddressTags(NewChurchName(), NewCity(), NewStateCode(), NewZip());
        tags[OsmTags.HouseNumber] = houseNumber;
        tags[OsmTags.Street] = streetName;

        // Act
        var record = Assert.Single(BulkImportJob.ParseOsm(OsmDocument(OsmElement(tags))).ToList());

        // Assert
        Assert.Equal($"{houseNumber} {streetName}", record.Street);
    }

    [Fact]
    public void ParseOsm_DenominationTag_MapsToCanonicalName()
    {
        // Arrange
        var tags = AddressTags(NewChurchName(), NewCity(), NewStateCode(), NewZip());
        tags[OsmTags.Denomination] = BaptistDenominationSlug;

        // Act
        var record = Assert.Single(BulkImportJob.ParseOsm(OsmDocument(OsmElement(tags))).ToList());

        // Assert
        Assert.Equal(ChurchDenominations.Baptist, record.DenominationName);
    }

    [Theory]
    [InlineData("CO", "CO")]
    [InlineData("co", "CO")]
    [InlineData("Ohio", "OH")]
    [InlineData("texas", "TX")]
    [InlineData("W. Va.", "WV")]
    [InlineData("-IL", "IL")]
    public void ParseOsm_NormalizesState(string osmState, string expectedCode)
    {
        // Arrange
        var tags = AddressTags(NewChurchName(), NewCity(), osmState, NewZip());

        // Act
        var record = Assert.Single(BulkImportJob.ParseOsm(OsmDocument(OsmElement(tags))).ToList());

        // Assert
        Assert.Equal(expectedCode, record.State);
    }

    [Fact]
    public void ParseOsm_UnrecognizedState_SkipsRow()
    {
        // Arrange
        var unrecognizedState = $"State{Guid.NewGuid():N}";
        var tags = AddressTags(NewChurchName(), NewCity(), unrecognizedState, NewZip());

        // Act
        var results = BulkImportJob.ParseOsm(OsmDocument(OsmElement(tags))).ToList();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void ParseIrsCsv_RomanCatholicNteeCode_SetsRomanCatholicDenomination()
    {
        // Arrange
        var csv = IrsCsv(
            [IrsCsvColumns.Name, IrsCsvColumns.State, IrsCsvColumns.NteeCode],
            [[NewChurchName(), NewStateCode(), NteeCodes.RomanCatholic]]);

        // Act
        var record = Assert.Single(BulkImportJob.ParseIrsCsv(csv).ToList());

        // Assert
        Assert.Equal(ChurchDenominations.RomanCatholic, record.DenominationName);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("X20", 0)]
    [InlineData("X21", 5)]
    [InlineData("X22", 5)]
    [InlineData("X50", 0)]
    public void NteeToWorshipStyle_VariousCodes_ReturnsExpected(string? ntee, int expected)
    {
        Assert.Equal(expected, BulkImportJob.NteeToWorshipStyle(ntee));
    }

    [Theory]
    [InlineData("X22", "Roman Catholic")]
    [InlineData("x22", "Roman Catholic")]
    [InlineData("X21", null)]
    [InlineData("X20", null)]
    [InlineData(null, null)]
    public void NteeToDenomination_VariousCodes_ReturnsExpected(string? ntee, string? expected)
    {
        Assert.Equal(expected, BulkImportJob.NteeToDenomination(ntee));
    }

    [Theory]
    [InlineData("roman_catholic", "Roman Catholic")]
    [InlineData("catholic", "Roman Catholic")]
    [InlineData("baptist", "Baptist")]
    [InlineData("LUTHERAN", "Lutheran")]
    [InlineData("nonexistent_sect", null)]
    [InlineData(null, null)]
    public void OsmDenominationToName_VariousSlugs_ReturnsExpected(string? slug, string? expected)
    {
        Assert.Equal(expected, BulkImportJob.OsmDenominationToName(slug));
    }

    [Fact]
    public void ParseIrsCsv_WithNtee_EmitsNteeAttribute()
    {
        // Arrange
        var csv = IrsCsv(
            [IrsCsvColumns.Name, IrsCsvColumns.State, IrsCsvColumns.NteeCode],
            [[NewChurchName(), NewStateCode(), NonLiturgicalNteeCode]]);

        // Act
        var record = Assert.Single(BulkImportJob.ParseIrsCsv(csv).ToList());

        // Assert
        var attribute = Assert.Single(record.Attributes);
        Assert.Equal(ChurchAttributeKeys.NteeCode, attribute.Key);
        Assert.Equal(NonLiturgicalNteeCode, attribute.Value);
        Assert.Equal(ChurchImportSources.Irs, attribute.Source);
    }

    [Fact]
    public void ParseOsm_WithTags_EmitsSourceAttributes()
    {
        // Arrange
        var tags = AddressTags(NewChurchName(), NewCity(), NewStateCode(), NewZip());
        tags[OsmTags.Denomination] = BaptistDenominationSlug;
        tags[OsmTags.Website] = NewWebsite();

        // Act
        var record = Assert.Single(BulkImportJob.ParseOsm(OsmDocument(OsmElement(tags))).ToList());

        // Assert
        Assert.Contains(record.Attributes, a =>
            string.Equals(a.Key, ChurchAttributeKeys.Denomination, StringComparison.Ordinal)
            && string.Equals(a.Source, ChurchImportSources.Osm, StringComparison.Ordinal));
        Assert.Contains(record.Attributes, a =>
            string.Equals(a.Key, ChurchAttributeKeys.Website, StringComparison.Ordinal)
            && string.Equals(a.Source, ChurchImportSources.Osm, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_MissingBlobPath_ReturnsBadRequest()
    {
        // Arrange
        var (worker, _, _) = BuildWorker(new FakeDbConnection(), blobContent: null);
        var req = BuildRequest([]);

        // Act
        var response = await worker.Run(req, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Run_BlobNotFound_ReturnsNotFound()
    {
        // Arrange
        var (worker, _, _) = BuildWorker(new FakeDbConnection(), blobContent: null);
        var req = BuildRequest([new(BulkImportJob.BlobPathQueryParameter, NewImportBlobPath())]);

        // Act
        var response = await worker.Run(req, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Run_NewIrsRecords_PublishesAndReturnsOk()
    {
        // Arrange
        IReadOnlyList<IReadOnlyList<string>> newRows =
        [
            [NewChurchName(), NewStateCode(), NonLiturgicalNteeCode],
            [NewChurchName(), NewStateCode(), NonLiturgicalNteeCode],
        ];
        var csv = IrsCsv([IrsCsvColumns.Name, IrsCsvColumns.State, IrsCsvColumns.NteeCode], newRows);
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(ExistingKeysTable()));

        var (worker, sender, _) = BuildWorker(connection, blobContent: csv);
        var req = BuildRequest([new(BulkImportJob.BlobPathQueryParameter, NewImportBlobPath()), new(BulkImportJob.SourceQueryParameter, ChurchImportSources.Irs)]);

        // Act
        var response = await worker.Run(req, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        sender.Verify(
            s => s.SendMessagesAsync(It.Is<IEnumerable<ServiceBusMessage>>(m => m.Count() == 2), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Run_DuplicateRecord_SkipsExistingInDb()
    {
        // Arrange
        var firstExistingName = NewChurchName();
        var firstExistingState = NewStateCode();
        var secondExistingName = NewChurchName();
        var secondExistingState = NewStateCode();
        IReadOnlyList<IReadOnlyList<string>> existingRows =
        [
            [firstExistingName, firstExistingState, NonLiturgicalNteeCode],
            [secondExistingName, secondExistingState, NonLiturgicalNteeCode],
        ];
        var csv = IrsCsv([IrsCsvColumns.Name, IrsCsvColumns.State, IrsCsvColumns.NteeCode], existingRows);
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(ExistingKeysTable(
            (firstExistingName, firstExistingState),
            (secondExistingName, secondExistingState))));

        var (worker, sender, _) = BuildWorker(connection, blobContent: csv);
        var req = BuildRequest([new(BulkImportJob.BlobPathQueryParameter, NewImportBlobPath()), new(BulkImportJob.SourceQueryParameter, ChurchImportSources.Irs)]);

        // Act
        var response = await worker.Run(req, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        sender.Verify(
            s => s.SendMessagesAsync(It.IsAny<IEnumerable<ServiceBusMessage>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Run_OsmSource_ParsesOsmAndPublishes()
    {
        // Arrange
        var tags = AddressTags(NewChurchName(), NewCity(), NewStateCode(), NewZip());
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(ExistingKeysTable()));

        var (worker, sender, _) = BuildWorker(connection, blobContent: OsmDocument(OsmElement(tags)));
        var req = BuildRequest([new(BulkImportJob.BlobPathQueryParameter, NewImportBlobPath()), new(BulkImportJob.SourceQueryParameter, ChurchImportSources.Osm)]);

        // Act
        var response = await worker.Run(req, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        sender.Verify(
            s => s.SendMessagesAsync(It.Is<IEnumerable<ServiceBusMessage>>(m => m.Count() == 1), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Run_DuplicateWithinSameFile_PublishesOnlyOnce()
    {
        // Arrange
        var repeatedName = NewChurchName();
        var repeatedState = NewStateCode();
        IReadOnlyList<IReadOnlyList<string>> duplicatedRows =
        [
            [repeatedName, repeatedState, NonLiturgicalNteeCode],
            [repeatedName, repeatedState, NteeCodes.Protestant],
        ];
        var csv = IrsCsv([IrsCsvColumns.Name, IrsCsvColumns.State, IrsCsvColumns.NteeCode], duplicatedRows);
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(ExistingKeysTable()));

        var (worker, sender, _) = BuildWorker(connection, blobContent: csv);
        var req = BuildRequest([new(BulkImportJob.BlobPathQueryParameter, NewImportBlobPath()), new(BulkImportJob.SourceQueryParameter, ChurchImportSources.Irs)]);

        // Act
        var response = await worker.Run(req, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        sender.Verify(
            s => s.SendMessagesAsync(It.Is<IEnumerable<ServiceBusMessage>>(m => m.Count() == 1), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static string NewChurchName() => $"Church{Guid.NewGuid():N}";

    private static string NewNonLatinChurchName() =>
        string.Concat(Enumerable.Range(0, 8).Select(_ => (char)Random.Shared.Next(0x4E00, 0x9FFF)));

    private static string NewCity() => $"City{Guid.NewGuid():N}";

    private static string NewStreetName() => $"{Guid.NewGuid():N} Street";

    private static string NewHouseNumber() => Random.Shared.Next(100, 9999).ToString(CultureInfo.InvariantCulture);

    private static string NewStreet() => $"{NewHouseNumber()} {NewStreetName()}";

    private static string NewZip() => Random.Shared.Next(10000, 99999).ToString(CultureInfo.InvariantCulture);

    private static string NewStateCode() =>
        $"{(char)Random.Shared.Next('A', 'Z' + 1)}{(char)Random.Shared.Next('A', 'Z' + 1)}";

    private static string NewPhoneNumber() =>
        $"{Random.Shared.Next(200, 999)}-{Random.Shared.Next(200, 999)}-{Random.Shared.Next(1000, 9999)}";

    private static string NewWebsite() => $"https://{Guid.NewGuid():N}.example";

    private static string NewEmailAddress() => $"{Guid.NewGuid():N}@{Guid.NewGuid():N}.example";

    private static string NewImportBlobPath() => $"{Guid.NewGuid():N}/{Guid.NewGuid():N}";

    private static decimal NewLatitude() => Math.Round(((decimal)Random.Shared.NextDouble() * 40m) + 1m, 4);

    private static decimal NewLongitude() => -Math.Round(((decimal)Random.Shared.NextDouble() * 100m) + 1m, 4);

    private static string Decimal(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static string IrsCsv(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var lines = new List<string> { string.Join(',', columns) };
        foreach (var row in rows)
        {
            lines.Add(string.Join(',', row));
        }

        return string.Join('\n', lines);
    }

    private static Dictionary<string, string> AddressTags(string name, string city, string state, string zip) =>
        new(StringComparer.Ordinal)
        {
            [OsmTags.Name] = name,
            [OsmTags.City] = city,
            [OsmTags.State] = state,
            [OsmTags.Postcode] = zip,
        };

    private static Dictionary<string, object> OsmElement(Dictionary<string, string> tags) =>
        new(StringComparer.Ordinal) { [OsmTags.Tags] = tags };

    private static string OsmDocument(params Dictionary<string, object>[] elements) =>
        JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [OsmTags.Elements] = elements,
        });

    private static DataTable ExistingKeysTable(params (string Name, string State)[] rows)
    {
        var table = new DataTable();
        table.Columns.Add(nameof(GeocodingRequest.CanonicalName), typeof(string));
        table.Columns.Add(nameof(GeocodingRequest.State), typeof(string));
        foreach (var (name, state) in rows)
        {
            table.Rows.Add(name, state);
        }

        return table;
    }

    private static (BulkImportJob Worker, Mock<ServiceBusSender> Sender, FakeDbConnection Connection) BuildWorker(
        FakeDbConnection connection,
        string? blobContent)
    {
        var azureResponse = Mock.Of<Response>();

        var blobClient = new Mock<BlobClient>(MockBehavior.Strict);
        blobClient
            .Setup(b => b.ExistsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(blobContent is not null, azureResponse));
        if (blobContent is not null)
        {
            blobClient
                .Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Response.FromValue(
                    BlobsModelFactory.BlobDownloadResult(content: BinaryData.FromString(blobContent)),
                    azureResponse));
        }

        var containerClient = new Mock<BlobContainerClient>(MockBehavior.Strict);
        containerClient.Setup(c => c.GetBlobClient(It.IsAny<string>())).Returns(blobClient.Object);

        var blobServiceClient = new Mock<BlobServiceClient>(MockBehavior.Strict);
        blobServiceClient.Setup(s => s.GetBlobContainerClient(BlobContainerNames.Imports)).Returns(containerClient.Object);

        var blobFactory = new Mock<IAzureClientFactory<BlobServiceClient>>(MockBehavior.Strict);
        blobFactory.Setup(f => f.CreateClient(AzureClientNames.Crgolden)).Returns(blobServiceClient.Object);

        var sender = new Mock<ServiceBusSender>(MockBehavior.Strict);
        sender.Setup(s => s.SendMessagesAsync(It.IsAny<IEnumerable<ServiceBusMessage>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        sender.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var serviceBusClient = new Mock<ServiceBusClient>(MockBehavior.Strict);
        serviceBusClient.Setup(c => c.CreateSender(ChurchQueueNames.GeocodingRequests)).Returns(sender.Object);

        var busFactory = new Mock<IAzureClientFactory<ServiceBusClient>>(MockBehavior.Strict);
        busFactory.Setup(f => f.CreateClient(AzureClientNames.Crgolden)).Returns(serviceBusClient.Object);

        var worker = new BulkImportJob(blobFactory.Object, busFactory.Object, connection, NullLogger<BulkImportJob>.Instance);
        return (worker, sender, connection);
    }

    private static FakeHttpRequestData BuildRequest(IEnumerable<KeyValuePair<string, string>> query)
    {
        var queryCollection = new NameValueCollection();
        foreach (var (key, value) in query)
        {
            queryCollection[key] = value;
        }

        return new FakeHttpRequestData(queryCollection);
    }
}
