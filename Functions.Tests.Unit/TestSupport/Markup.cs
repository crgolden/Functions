namespace Functions.Tests.Unit.TestSupport;

using static MarkupSyntaxFixtureConstants;

internal static class Markup
{
    internal static string Element(string tag, string content) => $"<{tag}>{content}</{tag}>";

    internal static string ElementWithAttribute(string tag, string attribute, string attributeValue, string content) =>
        $"<{tag} {attribute}=\"{attributeValue}\">{content}</{tag}>";

    internal static string HtmlDocument(string bodyContent) => Element(HtmlRoot, Element(HtmlBody, bodyContent));
}
