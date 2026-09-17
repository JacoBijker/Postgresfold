using System.Text.RegularExpressions;

namespace Postgresfold.Scaffold.Domain.Util
{
    public static class StringUtils
    {
        public static string ToCamelCase(this string toConvert)
        {
            var pascal = toConvert.ToPascalCase();
            if (string.IsNullOrEmpty(pascal))
                return pascal;

            return char.ToLowerInvariant(pascal[0]) + pascal.Substring(1);
        }

        /// <summary>
        /// Converts snake_case (Postgres' idiomatic identifier style) to PascalCase, e.g.
        /// "user_devices" -> "UserDevices". Already-PascalCase input (no underscores) is
        /// left as-is apart from ensuring the first letter is uppercase.
        /// </summary>
        public static string ToPascalCase(this string toConvert)
        {
            if (string.IsNullOrEmpty(toConvert))
                return toConvert;

            var parts = toConvert.Split('_', StringSplitOptions.RemoveEmptyEntries);
            return string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p.Substring(1)));
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
