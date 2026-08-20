namespace Functions.Tests.Unit;

using Curator.Enrichment;

[Trait("Category", "Unit")]
public sealed class PublisherTierClassifierTests
{
    [Fact]
    public void FingerprintPublisherTierRules_IsStableRegardlessOfInputOrder()
    {
        // Arrange
        var rulesA = new List<PublisherTierRule>
        {
            new(Guid.Parse("a4e72a6a-013a-ecbb-b420-cbd3130683fb"), "sony", PublisherTierRuleSet.AaaTier, PublisherTierRuleSet.SubstringMatchKind),
            new(Guid.Parse("b827608b-9c19-7e96-73d5-a5cdcadffed0"), "team17", PublisherTierRuleSet.AaTier, PublisherTierRuleSet.SubstringMatchKind),
        };
        var rulesB = new List<PublisherTierRule>
        {
            new(Guid.Parse("b827608b-9c19-7e96-73d5-a5cdcadffed0"), "team17", PublisherTierRuleSet.AaTier, PublisherTierRuleSet.SubstringMatchKind),
            new(Guid.Parse("a4e72a6a-013a-ecbb-b420-cbd3130683fb"), "sony", PublisherTierRuleSet.AaaTier, PublisherTierRuleSet.SubstringMatchKind),
        };

        // Act
        var fingerprintA = PublisherTierClassifier.FingerprintPublisherTierRules(rulesA);
        var fingerprintB = PublisherTierClassifier.FingerprintPublisherTierRules(rulesB);

        // Assert
        Assert.Equal(fingerprintA, fingerprintB);
    }

    [Fact]
    public void FingerprintPublisherTierRules_ChangesWhenARuleChanges()
    {
        // Arrange
        var before = new List<PublisherTierRule> { new(Guid.Parse("a4e72a6a-013a-ecbb-b420-cbd3130683fb"), "sony", PublisherTierRuleSet.AaaTier, PublisherTierRuleSet.SubstringMatchKind) };
        var after = new List<PublisherTierRule> { new(Guid.Parse("a4e72a6a-013a-ecbb-b420-cbd3130683fb"), "sony", PublisherTierRuleSet.AaTier, PublisherTierRuleSet.SubstringMatchKind) };

        // Act
        var fingerprintBefore = PublisherTierClassifier.FingerprintPublisherTierRules(before);
        var fingerprintAfter = PublisherTierClassifier.FingerprintPublisherTierRules(after);

        // Assert
        Assert.NotEqual(fingerprintBefore, fingerprintAfter);
    }
}
