using System.Reflection;

namespace Roslyn.Mcp;

/// <summary>The server's version, as reported by <c>--version</c> and in the MCP server info.</summary>
internal static class ServerVersion
{
    /// <summary>
    /// The assembly's informational version (the <c>Version</c> MSBuild property) without any
    /// <c>+suffix</c>, so SourceLink's commit hash never appears in the reported version.
    /// </summary>
    public static string Value { get; } = Read();

    private static string Read()
    {
        var informational = typeof(ServerVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrEmpty(informational))
        {
            return typeof(ServerVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }
}
