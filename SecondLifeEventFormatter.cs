using System.Collections;
using System.Reflection;
using OpenMetaverse;

namespace Munibot;

public static class SecondLifeEventFormatter
{
    private static readonly string[] PreferredMembers =
    [
        "Message",
        "FromName",
        "FromAgentName",
        "FromAgentID",
        "SourceID",
        "OwnerID",
        "ObjectID",
        "ObjectName",
        "IM",
        "Dialog",
        "SessionID",
        "IMSessionID",
        "GroupID",
        "RegionID",
        "Simulator",
        "Status",
        "Reason",
        "Balance",
        "Amount",
        "TransactionID",
        "Success",
        "Description",
        "Item",
        "Offer"
    ];

    public static IReadOnlyDictionary<string, string> Format(object eventArgs, int maxValueLength = 384)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddObject(result, eventArgs, prefix: null, depth: 0, maxValueLength);
        return result;
    }

    private static void AddObject(
        IDictionary<string, string> result,
        object? value,
        string? prefix,
        int depth,
        int maxValueLength)
    {
        if (value is null || depth > 2)
        {
            return;
        }

        var type = value.GetType();
        if (IsSimple(type))
        {
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                result[prefix] = Redaction.RedactText(Convert.ToString(value), maxValueLength);
            }
            return;
        }

        var members = type
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.GetIndexParameters().Length == 0)
            .Select(p => new MemberAccessor(p.Name, () => p.GetValue(value)))
            .Concat(type
                .GetFields(BindingFlags.Instance | BindingFlags.Public)
                .Select(f => new MemberAccessor(f.Name, () => f.GetValue(value))))
            .OrderBy(m =>
            {
                var index = Array.FindIndex(PreferredMembers, preferred =>
                    string.Equals(preferred, m.Name, StringComparison.OrdinalIgnoreCase));
                return index < 0 ? int.MaxValue : index;
            })
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Take(24);

        foreach (var member in members)
        {
            object? memberValue;
            try
            {
                memberValue = member.GetValue();
            }
            catch
            {
                continue;
            }

            if (memberValue is null)
            {
                continue;
            }

            var name = string.IsNullOrWhiteSpace(prefix) ? member.Name : $"{prefix}.{member.Name}";
            var memberType = memberValue.GetType();
            if (IsSimple(memberType) || memberValue is UUID or Vector3 or Simulator)
            {
                result[name] = Redaction.RedactText(Convert.ToString(memberValue), maxValueLength);
                continue;
            }

            if (memberValue is IEnumerable and not string)
            {
                result[name] = "[collection]";
                continue;
            }

            AddObject(result, memberValue, name, depth + 1, maxValueLength);
        }
    }

    private static bool IsSimple(Type type)
        => type.IsPrimitive ||
           type.IsEnum ||
           type == typeof(string) ||
           type == typeof(decimal) ||
           type == typeof(DateTime) ||
           type == typeof(DateTimeOffset) ||
           type == typeof(Guid);

    private sealed record MemberAccessor(string Name, Func<object?> GetValue);
}
