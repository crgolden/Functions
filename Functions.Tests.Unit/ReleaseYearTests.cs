namespace Functions.Tests.Unit;

using Curator.Enrichment;

[Trait("Category", "Unit")]
public sealed class ReleaseYearTests
{
    [Fact]
    public void FromDate_ReturnsTheYear()
    {
        // Act
        var year = ReleaseYear.FromDate(new DateOnly(2018, 10, 5));

        // Assert
        Assert.Equal(2018, year);
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
        // Act
        var year = ReleaseYear.FromText("2018-10-05T04:00:00Z");

        // Assert
        Assert.Equal(2018, year);
    }

    [Fact]
    public void FromText_ReadsTheYearFromABareRawgDate()
    {
        // Act
        var year = ReleaseYear.FromText("2018-10-05");

        // Assert
        Assert.Equal(2018, year);
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
