namespace Roslyn.Mcp.Services
{
    public sealed class WorkspaceOptions
    {
        /// <summary>
        /// How often a cached workspace is compared against the file system.
        /// </summary>
        public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(2);
    }
}
