namespace Functions.Curator;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

public static class CurationRuleFingerprint
{
    private const char FirstLiteralCharacter = ' ';
    private const char FirstEscapedHighCharacter = '\u007f';

    public static string PythonJsonNumber(int value) => value.ToString(CultureInfo.InvariantCulture);

    public static string PythonJsonString(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (character >= FirstLiteralCharacter && character < FirstEscapedHighCharacter)
                    {
                        builder.Append(character);
                    }
                    else
                    {
                        builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)character:x4}");
                    }

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    public static string Compute(IReadOnlyList<IReadOnlyList<string>> canonicalRows)
    {
        var builder = new StringBuilder("[");
        for (var row = 0; row < canonicalRows.Count; row++)
        {
            if (row > 0)
            {
                builder.Append(", ");
            }

            builder.Append('[').Append(string.Join(", ", canonicalRows[row])).Append(']');
        }

        builder.Append(']');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
