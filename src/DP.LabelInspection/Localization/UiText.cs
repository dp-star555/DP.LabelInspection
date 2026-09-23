using System.Globalization;
using System.Resources;

namespace DP.LabelInspection;

internal static class UiText
{
    private static readonly ResourceManager Labels = new ResourceManager(
        "DP.LabelInspection.Resources.UiText",
        typeof(UiText).Assembly
    );
    private static readonly ResourceManager Errors = new ResourceManager(
        "DP.LabelInspection.Resources.Errors",
        typeof(UiText).Assembly
    );

    internal static string Get(string key)
    {
        return Labels.GetString(key, CultureInfo.CurrentUICulture) ?? key;
    }

    internal static string Format(string key, params object[] arguments)
    {
        return string.Format(CultureInfo.CurrentCulture, Get(key), arguments);
    }

    internal static string Error(string key)
    {
        return Errors.GetString(key, CultureInfo.CurrentUICulture) ?? key;
    }
}
