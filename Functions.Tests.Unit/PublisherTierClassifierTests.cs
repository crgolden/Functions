namespace Functions.Tests.Unit;

using Curator.Enrichment;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class PublisherTierClassifierTests
{
    [Fact]
    public void FingerprintPublisherTierRules_IsStableRegardlessOfInputOrder()
    {
        // Arrange
        var aaaRule = new PublisherTierRule(
            TestValues.NewPublisherTierRuleId(),
            TestValues.NewPublisherPattern(),
            PublisherTierRuleSet.AaaTier,
            PublisherTierRuleSet.SubstringMatchKind);
        var aaRule = new PublisherTierRule(
            TestValues.NewPublisherTierRuleId(),
            TestValues.NewPublisherPattern(),
            PublisherTierRuleSet.AaTier,
            PublisherTierRuleSet.SubstringMatchKind);
        var rulesA = new List<PublisherTierRule> { aaaRule, aaRule };
        var rulesB = new List<PublisherTierRule> { aaRule, aaaRule };

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
        var ruleId = TestValues.NewPublisherTierRuleId();
        var pattern = TestValues.NewPublisherPattern();
        var before = new List<PublisherTierRule>
        {
            new(ruleId, pattern, PublisherTierRuleSet.AaaTier, PublisherTierRuleSet.SubstringMatchKind),
        };
        var after = new List<PublisherTierRule>
        {
            new(ruleId, pattern, PublisherTierRuleSet.AaTier, PublisherTierRuleSet.SubstringMatchKind),
        };

        // Act
        var fingerprintBefore = PublisherTierClassifier.FingerprintPublisherTierRules(before);
        var fingerprintAfter = PublisherTierClassifier.FingerprintPublisherTierRules(after);

        // Assert
        Assert.NotEqual(fingerprintBefore, fingerprintAfter);
    }
}
