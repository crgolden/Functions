namespace Functions;

using System.Text;

internal static class SlugHelper
{
    internal static string ToSlug(string value)
    {
        var sb = new StringBuilder();
        var prevDash = false;
        foreach (var ch in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                prevDash = false;
            }
            else if (!prevDash && sb.Length > 0)
            {
                sb.Append('-');
                prevDash = true;
            }
        }

        return sb.ToString().TrimEnd('-');
    }
}
