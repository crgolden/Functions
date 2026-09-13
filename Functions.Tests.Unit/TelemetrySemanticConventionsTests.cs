namespace Functions.Tests.Unit;

using TestSupport;

[Trait("Category", "Unit")]
public sealed class TelemetrySemanticConventionsTests
{
    [Fact]
    public void OptInToStableDatabaseConventions_WhenNothingHasChosen_SelectsTheStableDatabaseAttributes()
    {
        // Arrange
        var operatorValueToRestore =
            Environment.GetEnvironmentVariable(Telemetry.SemanticConventions.StabilityOptInVariable);
        Environment.SetEnvironmentVariable(Telemetry.SemanticConventions.StabilityOptInVariable, null);

        try
        {
            // Act
            Telemetry.SemanticConventions.OptInToStableDatabaseConventionsUnlessAlreadyChosen();

            // Assert
            Assert.Equal(
                Telemetry.SemanticConventions.StableDatabaseConventions,
                Environment.GetEnvironmentVariable(Telemetry.SemanticConventions.StabilityOptInVariable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                Telemetry.SemanticConventions.StabilityOptInVariable, operatorValueToRestore);
        }
    }

    [Fact]
    public void OptInToStableDatabaseConventions_WhenAnOperatorAlreadyChose_LeavesTheirValueAlone()
    {
        // Arrange
        var operatorValueToRestore =
            Environment.GetEnvironmentVariable(Telemetry.SemanticConventions.StabilityOptInVariable);
        var operatorChosenOptIn = TestValues.LowercaseToken(8);
        Environment.SetEnvironmentVariable(
            Telemetry.SemanticConventions.StabilityOptInVariable, operatorChosenOptIn);

        try
        {
            // Act
            Telemetry.SemanticConventions.OptInToStableDatabaseConventionsUnlessAlreadyChosen();

            // Assert
            Assert.Equal(
                operatorChosenOptIn,
                Environment.GetEnvironmentVariable(Telemetry.SemanticConventions.StabilityOptInVariable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                Telemetry.SemanticConventions.StabilityOptInVariable, operatorValueToRestore);
        }
    }
}
