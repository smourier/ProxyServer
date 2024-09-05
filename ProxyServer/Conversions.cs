using System.Reflection;
using System.Text;

namespace ProxyServer;

public static class Conversions
{
    public static string GetAllMessagesWithDots(this Exception exception)
    {
        var s = GetAllMessages(exception, s =>
        {
            if (s == null)
                return null;

            if (s.EndsWith('.'))
                return " ";

            if (s.EndsWith(". "))
                return null;

            return ". ";
        });

        if (s != null && !s.EndsWith('.'))
            return s + ".";

        return string.Empty;
    }

    public static string? GetAllMessages(this Exception exception) => GetAllMessages(exception, s => Environment.NewLine);
    public static string? GetAllMessages(this Exception exception, Func<string?, string?>? getSeparatorFunc)
    {
        if (exception == null)
            return null;

        var sb = new StringBuilder();
        AppendMessages(sb, exception, getSeparatorFunc);
        var msg = sb.ToString().Replace("..", ".").Nullify();
        return msg;
    }

    private static void AppendMessages(StringBuilder sb, Exception? e, Func<string?, string?>? getSeparatorFunc)
    {
        if (e == null)
            return;

        if (e is AggregateException agg)
        {
            foreach (var ex in agg.InnerExceptions)
            {
                AppendMessages(sb, ex, getSeparatorFunc);
            }
            return;
        }

        if (e is not TargetInvocationException)
        {
            if (sb.Length > 0 && getSeparatorFunc != null)
            {
                var sep = getSeparatorFunc(sb.ToString());
                if (sep != null)
                {
                    sb.Append(sep);
                }
            }

            var typeName = GetExceptionTypeName(e);
            if (!string.IsNullOrWhiteSpace(typeName))
            {
                sb.Append(typeName);
                sb.Append(": ");
                sb.Append(e.Message);
            }
            else
            {
                sb.Append(e.Message);
            }
        }
        AppendMessages(sb, e.InnerException, getSeparatorFunc);
    }

    private static string? GetExceptionTypeName(Exception exception)
    {
        if (exception == null)
            return null;

        var type = exception.GetType();
        if (type == null || string.IsNullOrWhiteSpace(type.FullName))
            return null;

        if (type.FullName.StartsWith("System.") ||
            type.FullName.StartsWith("Microsoft."))
            return null;

        return type.FullName;
    }

    public static bool EqualsIgnoreCase(this string? thisString, string? text, bool trim = true)
    {
        if (trim)
        {
            thisString = thisString.Nullify();
            text = text.Nullify();
        }

        if (thisString == null)
            return text == null;

        if (text == null)
            return false;

        if (thisString.Length != text.Length)
            return false;

        return string.Compare(thisString, text, StringComparison.OrdinalIgnoreCase) == 0;
    }

    public static string? Nullify(this string? text)
    {
        if (text == null)
            return null;

        if (string.IsNullOrWhiteSpace(text))
            return null;

        var t = text.Trim();
        return t.Length == 0 ? null : t;
    }

    public static IList<string> SplitToNullifiedList(this string? text, char[] separators, int count = int.MaxValue, StringSplitOptions options = StringSplitOptions.None)
    {
        var list = new List<string>();
        if (!string.IsNullOrEmpty(text))
        {
            foreach (var str in text.Split(separators, count, options))
            {
                var s = str.Nullify();
                if (s != null)
                {
                    list.Add(s);
                }
            }
        }
        return list;
    }
}
