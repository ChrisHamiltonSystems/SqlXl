using System.Globalization;
using System.Text.RegularExpressions;

namespace SqlXl.Core.SchemaInference;

internal static class TypeEvaluators
{
    private static readonly Regex FixedPointPattern = new(@"^-?\d+(\.\d+)?$", RegexOptions.Compiled);

    private static readonly string[] IsoAndLongFormats = new[]
    {
        "yyyy-MM-dd",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss.f",
        "yyyy-MM-ddTHH:mm:ss.ff",
        "yyyy-MM-ddTHH:mm:ss.fff",
        "yyyy-MM-ddTHH:mm:ss.ffff",
        "yyyy-MM-ddTHH:mm:ss.fffff",
        "yyyy-MM-ddTHH:mm:ss.ffffff",
        "yyyy-MM-ddTHH:mm:ss.fffffff",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.fff",
        "yyyy/MM/dd",
        "yyyy/MM/dd HH:mm:ss",
        "MMMM d, yyyy",
        "MMM d, yyyy",
        "d MMMM yyyy",
        "d MMM yyyy"
    };

    private static readonly string[] UsFormats = new[]
    {
        "M/d/yyyy",
        "MM/dd/yyyy",
        "M/d/yyyy h:mm tt",
        "M/d/yyyy hh:mm tt",
        "MM/dd/yyyy h:mm tt",
        "MM/dd/yyyy hh:mm tt",
        "M/d/yyyy HH:mm:ss",
        "MM/dd/yyyy HH:mm:ss"
    };

    private static readonly string[] IsoUsAndLongFormats =
        IsoAndLongFormats.Concat(UsFormats).ToArray();

    public static bool TryParseBit(object value)
    {
        switch (value)
        {
            case bool _: return true;
            case double d: return d == 0.0 || d == 1.0;
            case int i: return i == 0 || i == 1;
            case long l: return l == 0L || l == 1L;
            case string s:
                var t = s.Trim();
                return t == "0" || t == "1"
                    || t.Equals("true",  StringComparison.OrdinalIgnoreCase)
                    || t.Equals("false", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    public static bool TryParseInt(object value)
    {
        switch (value)
        {
            case int _: return true;
            case long l: return l >= int.MinValue && l <= int.MaxValue;
            case double d:
                return !double.IsNaN(d) && !double.IsInfinity(d)
                    && d == Math.Truncate(d)
                    && d >= int.MinValue && d <= int.MaxValue;
            case string s:
                return int.TryParse(s.Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out _);
        }
        return false;
    }

    public static bool TryParseBigInt(object value)
    {
        switch (value)
        {
            case int _:
            case long _: return true;
            case double d:
                return !double.IsNaN(d) && !double.IsInfinity(d)
                    && d == Math.Truncate(d)
                    && d >= long.MinValue && d <= long.MaxValue;
            case string s:
                return long.TryParse(s.Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out _);
        }
        return false;
    }

    public static bool TryParseDecimal(object value, out int integerDigits, out int fractionDigits)
    {
        integerDigits = 0;
        fractionDigits = 0;

        string text;
        switch (value)
        {
            case string s: text = s.Trim(); break;
            case double d when !double.IsNaN(d) && !double.IsInfinity(d):
                text = d.ToString("R", CultureInfo.InvariantCulture);
                break;
            case decimal dec: text = dec.ToString(CultureInfo.InvariantCulture); break;
            case int i: text = i.ToString(CultureInfo.InvariantCulture); break;
            case long l: text = l.ToString(CultureInfo.InvariantCulture); break;
            default: return false;
        }

        if (!FixedPointPattern.IsMatch(text)) return false;

        var clean = text.StartsWith("-") ? text.Substring(1) : text;
        int dotIdx = clean.IndexOf('.');
        if (dotIdx < 0)
        {
            integerDigits = clean.Length;
            fractionDigits = 0;
        }
        else
        {
            integerDigits = dotIdx;
            fractionDigits = clean.Length - dotIdx - 1;
        }
        return true;
    }

    public static bool TryParseFloat(object value)
    {
        switch (value)
        {
            case double d: return !double.IsNaN(d) && !double.IsInfinity(d);
            case float f:  return !float.IsNaN(f) && !float.IsInfinity(f);
            case decimal _:
            case int _:
            case long _: return true;
            case string s:
                return double.TryParse(s.Trim(),
                    NumberStyles.Float | NumberStyles.AllowThousands,
                    CultureInfo.InvariantCulture, out var v)
                    && !double.IsNaN(v) && !double.IsInfinity(v);
        }
        return false;
    }

    public static bool TryParseDateTime2(object value, DateFormatStyle style)
    {
        if (value is DateTime) return true;
        if (value is not string s) return false;

        var formats = style == DateFormatStyle.Iso
            ? IsoAndLongFormats
            : IsoUsAndLongFormats;

        return DateTime.TryParseExact(
            s.Trim(), formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces, out _);
    }
}
