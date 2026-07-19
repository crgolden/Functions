namespace Functions.Tests.Unit;

using System.Data;
using System.Net;
using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class BulkImportJobTests
{
    // --- ParseIrsCsv (pure, internal static) ---
    [Fact]
    public void ParseIrsCsv_SingleRow_MapsNameStreetCityStateZip()
    {
        // Arrange
        const string csv = """
            NAME,STREET,CITY,STATE,ZIP,NTEE_CD
            Grace Church,"123 Main St",Phoenix,AZ,85001,X20
            """;

        // Act
        var results = BulkImportJob.ParseIrsCsv(csv).ToList();

        // Assert
        Assert.Single(results);
        var r = results[0];
        Assert.Equal("Grace Church", r.CanonicalName);
        Assert.Equal("123 Main St", r.Street);
        Assert.Equal("Phoenix", r.City);
        Assert.Equal("AZ", r.State);
        Assert.Equal("85001", r.Zip);
        Assert.Equal(0, r.WorshipStyle);
        Assert.Equal(0.5m, r.Confidence);
    }

    [Fact]
    public void ParseIrsCsv_WithLatLonColumns_CarriesPreGeocodedCoordinates()
    {
        const string csv = """
            NAME,STREET,CITY,STATE,ZIP,NTEE_CD,Latitude,Longitude
            Grace Church,"123 Main St",Phoenix,AZ,85001,X20,33.4484,-112.0740
            """;

        var results = BulkImportJob.ParseIrsCsv(csv).ToList();

        Assert.Equal(33.4484m, results[0].Latitude);
        Assert.Equal(-112.0740m, results[0].Longitude);
    }

    [Fact]
    public void ParseIrsCsv_BlankLatLon_LeavesCoordinatesNull()
    {
        const string csv = """
            NAME,STREET,CITY,STATE,ZIP,NTEE_CD,Latitude,Longitude
            Grace Church,"123 Main St",Phoenix,AZ,85001,X20,,
            """;

        var results = BulkImportJob.ParseIrsCsv(csv).ToList();

        Assert.Null(results[0].Latitude);
        Assert.Null(results[0].Longitude);
    }

    [Theory]
    [InlineData("33.44", "-112.07", true)]
    [InlineData("0", "0", false)]
    [InlineData("", "", false)]
    [InlineData("abc", "-112.07", false)]
    public void ParseCoordinates_TruthTable(string lat, string lng, bool expectCoords)
    {
        var (latitude, longitude) = BulkImportJob.ParseCoordinates(lat, lng);
        Assert.Equal(expectCoords, latitude.HasValue && longitude.HasValue);
    }

    [Fact]
    public void ParseIrsCsv_NteeX21_MapsToLiturgical()
    {
        // Arrange
        const string csv = "NAME,STATE,NTEE_CD\nSt. Anthony's,AZ,X21";

        // Act
        var results = BulkImportJob.ParseIrsCsv(csv).ToList();

        // Assert
        Assert.Equal(5, results[0].WorshipStyle);
    }

    [Fact]
    public void ParseIrsCsv_NteeX22_MapsToLiturgical()
    {
        // Arrange
        const string csv = "NAME,STATE,NTEE_CD\nSt. George,AZ,X22";

        // Act
        var results = BulkImportJob.ParseIrsCsv(csv).ToList();

        // Assert
        Assert.Equal(5, results[0].WorshipStyle);
    }

    [Fact]
    public void ParseIrsCsv_MissingNameColumn_SkipsRow()
    {
        // Arrange — row has no name so it is skipped
        const string csv = "NAME,STATE\n,AZ";

        // Act
        var results = BulkImportJob.ParseIrsCsv(csv).ToList();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void ParseIrsCsv_MissingStateColumn_SkipsRow()
    {
        // Arrange
        const string csv = "NAME,STATE\nGrace Church,";

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
        // Act
        var results = BulkImportJob.ParseIrsCsv("NAME,STATE,NTEE_CD").ToList();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void ParseIrsCsv_MultipleRows_ParsesAll()
    {
        // Arrange
        const string csv = """
            NAME,STATE,NTEE_CD
            Grace Church,AZ,X20
            Trinity Church,CO,X21
            """;

        // Act
        var results = BulkImportJob.ParseIrsCsv(csv).ToList();

        // Assert
        Assert.Equal(2, results.Count);
        Assert.Equal("Grace Church", results[0].CanonicalName);
        Assert.Equal("Trinity Church", results[1].CanonicalName);
    }

    // --- ParseOsm (pure, internal static) ---
    [Fact]
    public void ParseOsm_SingleElement_MapsAllAddressFields()
    {
        // Arrange
        const string json = """
            {"elements":[{"tags":{"name":"St. Mark's","addr:street":"456 Oak Ave","addr:city":"Denver","addr:state":"CO","addr:postcode":"80201","phone":"303-555-1234","website":"https://stmarks.example","email":"info@stmarks.example"}}]}
            """;

        // Act
        var results = BulkImportJob.ParseOsm(json).ToList();

        // Assert
        Assert.Single(results);
        var r = results[0];
        Assert.Equal("St. Mark's", r.CanonicalName);
        Assert.Equal("456 Oak Ave", r.Street);
        Assert.Equal("Denver", r.City);
        Assert.Equal("CO", r.State);
        Assert.Equal("80201", r.Zip);
        Assert.Equal("303-555-1234", r.PhoneNumber);
        Assert.Equal("https://stmarks.example", r.Website);
        Assert.Equal("info@stmarks.example", r.EmailAddress);
        Assert.Equal(0.6m, r.Confidence);
    }

    [Fact]
    public void ParseOsm_ElementMissingName_SkipsRow()
    {
        // Arrange
        const string json = """{"elements":[{"tags":{"addr:state":"CO"}}]}""";

        // Act
        var results = BulkImportJob.ParseOsm(json).ToList();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void ParseOsm_ElementMissingState_SkipsRow()
    {
        // Arrange
        const string json = """{"elements":[{"tags":{"name":"Grace"}}]}""";

        // Act
        var results = BulkImportJob.ParseOsm(json).ToList();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void ParseOsm_ElementMissingTags_SkipsRow()
    {
        // Arrange
        const string json = """{"elements":[{"id":1}]}""";

        // Act
        var results = BulkImportJob.ParseOsm(json).ToList();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void ParseOsm_NoElementsKey_YieldsNothing()
    {
        // Act
        var results = BulkImportJob.ParseOsm("{}").ToList();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void ParseOsm_ElementMissingCity_SkipsRow()
    {
        // Arrange — City is NOT NULL in [dbo].[Churches], so a record without addr:city is skipped
        const string json = """{"elements":[{"tags":{"name":"Grace","addr:state":"CO","addr:postcode":"80201"}}]}""";

        // Act
        var results = BulkImportJob.ParseOsm(json).ToList();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void ParseOsm_ElementMissingPostcode_SkipsRow()
    {
        // Arrange — Zip is NOT NULL in [dbo].[Churches], so a record without addr:postcode is skipped
        const string json = """{"elements":[{"tags":{"name":"Grace","addr:city":"Denver","addr:state":"CO"}}]}""";

        // Act
        var results = BulkImportJob.ParseOsm(json).ToList();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void ParseOsm_NodeWithLatLon_PopulatesNativeCoordinates()
    {
        // Arrange — a node carries lat/lon directly; these must flow through instead of Census geocoding
        const string json = """
            {"elements":[{"type":"node","lat":39.7392,"lon":-104.9903,"tags":{"name":"St. Mark's","addr:city":"Denver","addr:state":"CO","addr:postcode":"80201"}}]}
            """;

        // Act
        var results = BulkImportJob.ParseOsm(json).ToList();

        // Assert
        var r = Assert.Single(results);
        Assert.Equal(39.7392m, r.Latitude);
        Assert.Equal(-104.9903m, r.Longitude);
    }

    [Fact]
    public void ParseOsm_WayWithCenter_PopulatesNativeCoordinates()
    {
        // Arrange — ways/relations expose coordinates via "center" (Overpass "out center")
        const string json = """
            {"elements":[{"type":"way","center":{"lat":39.5,"lon":-105.1},"tags":{"name":"Trinity","addr:city":"Lakewood","addr:state":"CO","addr:postcode":"80226"}}]}
            """;

        // Act
        var results = BulkImportJob.ParseOsm(json).ToList();

        // Assert
        var r = Assert.Single(results);
        Assert.Equal(39.5m, r.Latitude);
        Assert.Equal(-105.1m, r.Longitude);
    }

    [Fact]
    public void ParseOsm_NoCoordinates_LeavesCoordinatesNull()
    {
        // Arrange — without node lat/lon or center, coordinates stay null (GeocoderWorker falls back to Census)
        const string json = """{"elements":[{"tags":{"name":"Grace","addr:city":"Denver","addr:state":"CO","addr:postcode":"80201"}}]}""";

        // Act
        var r = Assert.Single(BulkImportJob.ParseOsm(json).ToList());

        // Assert
        Assert.Null(r.Latitude);
        Assert.Null(r.Longitude);
    }

    [Fact]
    public void ParseOsm_MultiValuePhone_KeepsOnlyFirstNumber()
    {
        // Arrange — OSM phone with several numbers exceeds NVARCHAR(20); only the first is kept
        const string json = """
            {"elements":[{"tags":{"name":"Grace","addr:city":"Denver","addr:state":"CO","addr:postcode":"80201","phone":"+1-707-758-2894;+1-415-555-1212"}}]}
            """;

        // Act
        var r = Assert.Single(BulkImportJob.ParseOsm(json).ToList());

        // Assert
        Assert.Equal("+1-707-758-2894", r.PhoneNumber);
    }

    [Fact]
    public void ParseOsm_OverlongSinglePhone_DropsPhone()
    {
        // Arrange — a single number longer than the 20-char column is dropped rather than truncated
        const string json = """
            {"elements":[{"tags":{"name":"Grace","addr:city":"Denver","addr:state":"CO","addr:postcode":"80201","phone":"+1-707-758-2894-extension-9999"}}]}
            """;

        // Act
        var r = Assert.Single(BulkImportJob.ParseOsm(json).ToList());

        // Assert
        Assert.Null(r.PhoneNumber);
    }

    [Fact]
    public void ParseOsm_MultiValueName_PrefersLatinSegment_TranslationFirst()
    {
        // Arrange — native-language name first, English translation second: prefer the English one
        const string json = """
            {"elements":[{"tags":{"name":"方舟浸信教會;Ark Baptist Church","addr:city":"Milpitas","addr:state":"CA","addr:postcode":"95035"}}]}
            """;

        // Act
        var r = Assert.Single(BulkImportJob.ParseOsm(json).ToList());

        // Assert
        Assert.Equal("Ark Baptist Church", r.CanonicalName);
    }

    [Fact]
    public void ParseOsm_MultiValueName_PrefersLatinSegment_TranslationSecond()
    {
        // Arrange — English translation first, native-language name second: still prefer English
        const string json = """
            {"elements":[{"tags":{"name":"Thánh Đường Các Thánh Tử Đạo Việt Nam;Christ the Incarnate Word Catholic Church","addr:city":"Houston","addr:state":"TX","addr:postcode":"77001"}}]}
            """;

        // Act
        var r = Assert.Single(BulkImportJob.ParseOsm(json).ToList());

        // Assert
        Assert.Equal("Christ the Incarnate Word Catholic Church", r.CanonicalName);
    }

    [Fact]
    public void ParseOsm_MultiValueName_BothAscii_KeepsFirstSegment()
    {
        // Arrange — no Latin-script signal to break the tie: keep the first segment, as with FirstPhone
        const string json = """
            {"elements":[{"tags":{"name":"West Side Church of Christ;Westside Church of Christ","addr:city":"Russellville","addr:state":"AR","addr:postcode":"72801"}}]}
            """;

        // Act
        var r = Assert.Single(BulkImportJob.ParseOsm(json).ToList());

        // Assert
        Assert.Equal("West Side Church of Christ", r.CanonicalName);
    }

    [Fact]
    public void ParseOsm_MultiValueName_TrailingEmptySegment_KeepsOnlyNonEmpty()
    {
        // Arrange — a trailing ';' with nothing after it must not be treated as a second candidate
        const string json = """
            {"elements":[{"tags":{"name":"Calvary Chapel;","addr:city":"Fredericksburg","addr:state":"VA","addr:postcode":"22401"}}]}
            """;

        // Act
        var r = Assert.Single(BulkImportJob.ParseOsm(json).ToList());

        // Assert
        Assert.Equal("Calvary Chapel", r.CanonicalName);
    }

    [Fact]
    public void ParseOsm_HouseNumberAndStreet_CombinesIntoStreet()
    {
        // Arrange — OSM stores the house number separately; it must be prepended so the street is complete
        const string json = """
            {"elements":[{"tags":{"name":"Grace","addr:housenumber":"123","addr:street":"Main St","addr:city":"Denver","addr:state":"CO","addr:postcode":"80201"}}]}
            """;

        // Act
        var r = Assert.Single(BulkImportJob.ParseOsm(json).ToList());

        // Assert
        Assert.Equal("123 Main St", r.Street);
    }

    [Fact]
    public void ParseOsm_DenominationTag_MapsToCanonicalName()
    {
        // Arrange — OSM denomination slug should resolve to a canonical seed name
        const string json = """
            {"elements":[{"tags":{"name":"Grace","denomination":"baptist","addr:city":"Denver","addr:state":"CO","addr:postcode":"80201"}}]}
            """;

        // Act
        var r = Assert.Single(BulkImportJob.ParseOsm(json).ToList());

        // Assert
        Assert.Equal("Baptist", r.DenominationName);
    }

    [Theory]
    [InlineData("CO", "CO")] // already a 2-letter code
    [InlineData("co", "CO")] // lowercased code → uppercased
    [InlineData("Ohio", "OH")] // full state name
    [InlineData("texas", "TX")] // full name, lowercased
    [InlineData("W. Va.", "WV")] // hand-typed abbreviation
    [InlineData("-IL", "IL")] // punctuation stripped to a 2-letter code
    public void ParseOsm_NormalizesState(string osmState, string expectedCode)
    {
        // Arrange — OSM addr:state is inconsistent; NCHAR(2) requires a 2-letter code or the insert truncates
        const string template = """{"elements":[{"tags":{"name":"Grace","addr:city":"Denver","addr:state":"__STATE__","addr:postcode":"80201"}}]}""";
        var json = template.Replace("__STATE__", osmState);

        // Act
        var r = Assert.Single(BulkImportJob.ParseOsm(json).ToList());

        // Assert
        Assert.Equal(expectedCode, r.State);
    }

    [Fact]
    public void ParseOsm_UnrecognizedState_SkipsRow()
    {
        // Arrange — an unmappable addr:state normalizes to null, so the required-State guard skips the row
        const string json = """{"elements":[{"tags":{"name":"Grace","addr:city":"Denver","addr:state":"Atlantis","addr:postcode":"80201"}}]}""";

        // Act
        var results = BulkImportJob.ParseOsm(json).ToList();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void ParseIrsCsv_NteeX22_SetsRomanCatholicDenomination()
    {
        // Arrange
        const string csv = "NAME,STATE,NTEE_CD\nSt. Mary,CO,X22";

        // Act
        var r = Assert.Single(BulkImportJob.ParseIrsCsv(csv).ToList());

        // Assert
        Assert.Equal("Roman Catholic", r.DenominationName);
    }

    // --- NteeToWorshipStyle ---
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

    // --- NteeToDenomination ---
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

    // --- OsmDenominationToName ---
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

    // --- Provenance attributes ---
    [Fact]
    public void ParseIrsCsv_WithNtee_EmitsNteeAttribute()
    {
        const string csv = "NAME,STATE,NTEE_CD\nGrace,CO,X20";

        var r = Assert.Single(BulkImportJob.ParseIrsCsv(csv).ToList());

        var attribute = Assert.Single(r.Attributes!);
        Assert.Equal("ntee_code", attribute.Key);
        Assert.Equal("X20", attribute.Value);
        Assert.Equal("irs", attribute.Source);
    }

    [Fact]
    public void ParseOsm_WithTags_EmitsSourceAttributes()
    {
        const string json = """
            {"elements":[{"tags":{"name":"Grace","denomination":"baptist","website":"https://g.example","addr:city":"Denver","addr:state":"CO","addr:postcode":"80201"}}]}
            """;

        var r = Assert.Single(BulkImportJob.ParseOsm(json).ToList());

        Assert.Contains(r.Attributes!, a => a.Key == "denomination" && a.Source == "osm");
        Assert.Contains(r.Attributes!, a => a.Key == "website" && a.Source == "osm");
    }

    // --- Run orchestration ---
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
        var req = BuildRequest([new("blobPath", "missing.csv")]);

        // Act
        var response = await worker.Run(req, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Run_NewIrsRecords_PublishesAndReturnsOk()
    {
        // Arrange — two records, DB returns no existing matches → both published
        const string csv = "NAME,STATE,NTEE_CD\nGrace Church,AZ,X20\nTrinity,CO,X20";
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(ExistingKeysTable())); // empty DB → nothing skipped

        var (worker, sender, _) = BuildWorker(connection, blobContent: csv);
        var req = BuildRequest([new("blobPath", "irs/test.csv"), new("source", "irs")]);

        // Act
        var response = await worker.Run(req, TestContext.Current.CancellationToken);

        // Assert — both records published in a single batch
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        sender.Verify(s => s.SendMessagesAsync(It.Is<IEnumerable<ServiceBusMessage>>(m => m.Count() == 2), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_DuplicateRecord_SkipsExistingInDb()
    {
        // Arrange — both keys already present in the DB set → both skipped
        const string csv = "NAME,STATE,NTEE_CD\nGrace Church,AZ,X20\nTrinity,CO,X20";
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(ExistingKeysTable(("Grace Church", "AZ"), ("Trinity", "CO"))));

        var (worker, sender, _) = BuildWorker(connection, blobContent: csv);
        var req = BuildRequest([new("blobPath", "irs/test.csv"), new("source", "irs")]);

        // Act
        var response = await worker.Run(req, TestContext.Current.CancellationToken);

        // Assert — no messages published; response still OK
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        sender.Verify(s => s.SendMessagesAsync(It.IsAny<IEnumerable<ServiceBusMessage>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Run_OsmSource_ParsesOsmAndPublishes()
    {
        // Arrange
        const string json = """
            {"elements":[{"tags":{"name":"St. Mark's","addr:city":"Denver","addr:state":"CO","addr:postcode":"80201"}}]}
            """;
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(ExistingKeysTable()));

        var (worker, sender, _) = BuildWorker(connection, blobContent: json);
        var req = BuildRequest([new("blobPath", "osm/co.json"), new("source", "osm")]);

        // Act
        var response = await worker.Run(req, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        sender.Verify(s => s.SendMessagesAsync(It.Is<IEnumerable<ServiceBusMessage>>(m => m.Count() == 1), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_DuplicateWithinSameFile_PublishesOnlyOnce()
    {
        // Arrange — empty DB, but the file lists the same name+state twice; the in-memory set must
        // collapse the second occurrence even though neither was in the DB to begin with.
        const string csv = "NAME,STATE,NTEE_CD\nGrace Church,CO,X20\nGrace Church,CO,X21";
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(ExistingKeysTable()));

        var (worker, sender, _) = BuildWorker(connection, blobContent: csv);
        var req = BuildRequest([new("blobPath", "irs/test.csv"), new("source", "irs")]);

        // Act
        var response = await worker.Run(req, TestContext.Current.CancellationToken);

        // Assert — only the first occurrence published
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        sender.Verify(s => s.SendMessagesAsync(It.Is<IEnumerable<ServiceBusMessage>>(m => m.Count() == 1), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static DataTable ExistingKeysTable(params (string Name, string State)[] rows)
    {
        var table = new DataTable();
        table.Columns.Add("CanonicalName", typeof(string));
        table.Columns.Add("State", typeof(string));
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
        blobServiceClient.Setup(s => s.GetBlobContainerClient("imports")).Returns(containerClient.Object);

        var blobFactory = new Mock<IAzureClientFactory<BlobServiceClient>>(MockBehavior.Strict);
        blobFactory.Setup(f => f.CreateClient("crgolden")).Returns(blobServiceClient.Object);

        var sender = new Mock<ServiceBusSender>(MockBehavior.Strict);
        sender.Setup(s => s.SendMessagesAsync(It.IsAny<IEnumerable<ServiceBusMessage>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        sender.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var serviceBusClient = new Mock<ServiceBusClient>(MockBehavior.Strict);
        serviceBusClient.Setup(c => c.CreateSender("geocoding-requests")).Returns(sender.Object);

        var busFactory = new Mock<IAzureClientFactory<ServiceBusClient>>(MockBehavior.Strict);
        busFactory.Setup(f => f.CreateClient("crgolden")).Returns(serviceBusClient.Object);

        var worker = new BulkImportJob(blobFactory.Object, busFactory.Object, connection, NullLogger<BulkImportJob>.Instance);
        return (worker, sender, connection);
    }

    private static FakeHttpRequestData BuildRequest(IEnumerable<KeyValuePair<string, string>> query)
    {
        var queryCollection = new System.Collections.Specialized.NameValueCollection();
        foreach (var (key, value) in query)
        {
            queryCollection[key] = value;
        }

        return new FakeHttpRequestData(queryCollection);
    }
}
