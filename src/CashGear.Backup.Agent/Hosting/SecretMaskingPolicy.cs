using System.Reflection;
using Serilog.Core;
using Serilog.Events;

namespace CashGear.Backup.Agent.Hosting;

/// <summary>
/// Serilog destructuring guard (plan, "Security"): when an object is logged with {@Object}, any property whose
/// name contains Password, Secret or Key is written as "***". The code never logs such objects; this catches
/// the day someone does.
/// </summary>
internal sealed class SecretMaskingPolicy : IDestructuringPolicy
{
    private static readonly string[] s_sensitive = ["password", "secret", "key"];

    public bool TryDestructure(object value, ILogEventPropertyValueFactory propertyValueFactory, out LogEventPropertyValue result)
    {
        var properties = value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0).ToList();
        if (!properties.Any(IsSensitive))
        {
            result = null!;
            return false;
        }

        result = new StructureValue(
            properties.Select(p => new LogEventProperty(p.Name, IsSensitive(p) ? new ScalarValue("***") : propertyValueFactory.CreatePropertyValue(SafeGet(p, value), destructureObjects: true))),
            value.GetType().Name);
        return true;
    }

    private static bool IsSensitive(PropertyInfo property) =>
        s_sensitive.Any(s => property.Name.Contains(s, StringComparison.OrdinalIgnoreCase));

    private static object? SafeGet(PropertyInfo property, object value)
    {
        try
        {
            return property.GetValue(value);
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }
}
