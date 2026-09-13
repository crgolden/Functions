namespace Functions.Tests.Unit;

using System.Globalization;
using Curator.Enrichment;
using TestSupport;
using static ReleaseYearFixtureConstants;

[Trait("Category", "Unit")]
public sealed class ReleaseYearTests
{
    [Fact]
    public void FromDate_ReturnsTheYear()
    {
        // Arrange
        var releaseDate = TestValues.NewReleaseDate();

        // Act
        var year = ReleaseYear.FromDate(releaseDate);

        // Assert
        Assert.Equal(releaseDate.Year, year);
    }

    [Fact]
    public void FromDate_WithNull_ReturnsNull()
    {
        // Act
        var year = ReleaseYear.FromDate(null);

        // Assert
        Assert.Null(year);
    }

    [Fact]
    public void FromText_ReadsTheYearFromAPsnFullTimestamp()
    {
        // Arrange
        var releaseTimestamp = TestValues.NewUtcTimestamp();

        // Act
        var year = ReleaseYear.FromText(
            releaseTimestamp.ToString(PsnFullTimestampFormat, CultureInfo.InvariantCulture));

        // Assert
        Assert.Equal(releaseTimestamp.Year, year);
    }

    [Fact]
    public void FromText_ReadsTheYearFromABareRawgDate()
    {
        // Arrange
        var releaseDate = TestValues.NewReleaseDate();

        // Act
        var year = ReleaseYear.FromText(releaseDate.ToString(RawgBareDateFormat, CultureInfo.InvariantCulture));

        // Assert
        Assert.Equal(releaseDate.Year, year);
    }

    [Fact]
    public void FromText_WithAnEmptyOrUnparseableValue_ReturnsNull()
    {
        // Act
        var year = ReleaseYear.FromText(string.Empty);

        // Assert
        Assert.Null(year);
    }

    [Fact]
    public void FromText_WithNull_ReturnsNull()
    {
        // Act
        var year = ReleaseYear.FromText(null);

        // Assert
        Assert.Null(year);
    }
}
