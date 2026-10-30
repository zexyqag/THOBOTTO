using System.Globalization;
using System.Reflection;

using NodaTime;

using THOBOTTO.Modules;

namespace THOBOTTO.Panel;

// Turns settings into form values and posted form values back into settings, checking each.
public static class SettingsForm
{
    private const long Megabyte = 1_048_576;

    public static Type Plain(PropertyInfo property) => Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

    public static bool IsList(PropertyInfo property) => property.PropertyType.IsGenericType && property.PropertyType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>);

    // The value as an input shows it.
    public static string Text(PropertyInfo property, SettingAttribute setting, object settings)
    {
        var value = property.GetValue(settings);
        return value switch
        {
            null => "",
            long bytes when setting.Kind == SettingKind.Megabytes => (bytes / Megabyte).ToString(CultureInfo.InvariantCulture),
            IReadOnlyList<string> texts => string.Join(", ", texts),
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
    }

    public static IReadOnlyList<ulong> Ids(PropertyInfo property, object settings) => property.GetValue(settings) switch
    {
        IReadOnlyList<ulong> ids => ids,
        ulong id => [id],
        _ => [],
    };

    // Reads every field from the form; a field with a problem is reported and left out. Switches: the choices of a
    // field of them.
    public static (Dictionary<PropertyInfo, object?> Values, Dictionary<string, string> Problems) Read(
        IEnumerable<(PropertyInfo Property, SettingAttribute Setting)> fields, Func<string, IReadOnlyList<string>> form, Func<PropertyInfo, IReadOnlyList<(string Value, string Label)>> switches)
    {
        var values = new Dictionary<PropertyInfo, object?>();
        var problems = new Dictionary<string, string>();
        foreach (var (property, setting) in fields)
        {
            var posted = form(property.Name);
            var text = posted.Count > 0 ? posted[0].Trim() : "";
            if (ReadOne(property, setting, text, posted, switches) is var (value, problem) && problem is not null)
                problems[property.Name] = problem;
            else
                values[property] = value;
        }
        return (values, problems);
    }

    private static (object? Value, string? Problem) ReadOne(PropertyInfo property, SettingAttribute setting, string text, IReadOnlyList<string> posted, Func<PropertyInfo, IReadOnlyList<(string Value, string Label)>> switches)
    {
        var type = Plain(property);
        var nullable = Nullable.GetUnderlyingType(property.PropertyType) is not null || !property.PropertyType.IsValueType && new NullabilityInfoContext().Create(property).WriteState == NullabilityState.Nullable;

        if (type == typeof(bool))
            return (posted.Contains("true"), null);

        if (property.PropertyType == typeof(IReadOnlyList<ulong>))
        {
            var ids = new List<ulong>();
            foreach (var item in posted.Where(p => p.Length > 0))
            {
                if (!ulong.TryParse(item, out var id))
                    return (null, "Pick from the list.");
                ids.Add(id);
            }
            return (ids, null);
        }
        // The switches posted are the ones on.
        if (setting.Kind == SettingKind.Switches)
        {
            var choices = switches(property);
            return posted.Any(p => p.Length > 0 && !choices.Any(c => c.Value == p)) ? (null, "Pick from the list.")
                : (choices.Where(c => !posted.Contains(c.Value)).Select(c => c.Value).ToList(), null);
        }
        if (property.PropertyType == typeof(IReadOnlyList<string>))
            return (text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList(), null);

        if (text.Length == 0)
            return nullable ? (null, null) : (null, type == typeof(string) ? "Can't be empty." : "Give a number.");

        if (type == typeof(ulong))
            return ulong.TryParse(text, out var id) ? (id, null) : (null, "Pick from the list.");

        if (type == typeof(string))
        {
            if (setting.Choices is { } choices && !choices.Any(c => c.Split('=')[0] == text))
                return (null, "Pick from the list.");
            if (setting.Kind == SettingKind.TimeZone)
                return DateTimeZoneProviders.Tzdb.GetZoneOrNull(text) is { } zone ? (zone.Id, null) : (null, "Not a time zone I know; pick one from the list.");
            return (text, null);
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || double.IsNaN(number) || double.IsInfinity(number))
            return (null, "Give a number.");
        if (number < setting.Min || number > setting.Max)
            return (null, $"Between {setting.Min.ToString(CultureInfo.InvariantCulture)} and {setting.Max.ToString(CultureInfo.InvariantCulture)}.");
        if (type == typeof(double))
            return (number, null);
        if (number != Math.Floor(number))
            return (null, "A whole number.");
        return type == typeof(int) ? ((int)number, null)
            : type == typeof(long) ? (setting.Kind == SettingKind.Megabytes ? (long)number * Megabyte : (long)number, null)
            : (null, "Unsupported setting.");
    }
}
