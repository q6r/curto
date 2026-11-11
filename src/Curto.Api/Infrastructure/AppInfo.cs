using System.Reflection;

namespace Curto.Api.Infrastructure;

/// <summary>Build metadata.</summary>
internal static class AppInfo
{
    /// <summary>Product name used in documents and logs.</summary>
    public const string Name = "curto";

    /// <summary>Semantic version of this build, without the source-link commit suffix.</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var informational = typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrEmpty(informational))
        {
            return "0.0.0";
        }

        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }
}
