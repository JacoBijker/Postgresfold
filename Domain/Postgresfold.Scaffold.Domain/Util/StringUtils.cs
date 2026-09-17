using System.Text.RegularExpressions;

namespace Postgresfold.Scaffold.Domain.Util
{
    public static class StringUtils
    {
        public static string ToCamelCase(this string toConvert)
        {
            return char.ToLowerInvariant(toConvert[0]) + toConvert.Substring(1);
        }

        public static string ToPascalCase(this string toConvert)
        {
            return char.ToUpperInvariant(toConvert[0]) + toConvert.Substring(1);
        }

        public static string ToKebabCase(this string toConvert)
        {
            Regex wordBoundaries = new Regex(@"(?<=[a-z0-9])([A-Z])", RegexOptions.Compiled);
            if (string.IsNullOrWhiteSpace(toConvert))
                return string.Empty;

            string kebab = wordBoundaries.Replace(toConvert, "-$1");
            return kebab.ToLowerInvariant();
        }
    }
}
