namespace Roslyn.Mcp.Services
{
    public class SecurityValidator
    {
        private const int MaxSearchPatternLength = 256;

        private readonly HashSet<string> _allowedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".sln", ".slnx", ".csproj" };

        /// <summary>
        /// Accepts an absolute path to a .sln, .slnx or .csproj file, or to a directory
        /// that contains one (resolved later by WorkspaceManager.ResolveSolutionPath).
        /// </summary>
        public bool ValidateSolutionPath(string path)
        {
            if (!IsSafeAbsolutePath(path))
                return false;

            try
            {
                if (Directory.Exists(path))
                    return true;

                return _allowedExtensions.Contains(Path.GetExtension(path)) && File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Accepts an absolute path to an existing C# source file.
        /// </summary>
        public bool ValidateSourceFilePath(string path)
        {
            if (!IsSafeAbsolutePath(path))
                return false;

            try
            {
                return Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase) && File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsSafeAbsolutePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            // Check for path traversal attempts
            if (path.Split('/', '\\').Contains(".."))
                return false;

            if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                return false;

            return Path.IsPathFullyQualified(path);
        }

        /// <summary>
        /// Returns an error message when the search pattern is unusable, otherwise null.
        /// The pattern itself is not altered: the search only ever Regex.Escapes it.
        /// </summary>
        public string? ValidateSearchPattern(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                return "Search pattern must not be empty.";

            if (pattern.Length > MaxSearchPatternLength)
                return $"Search pattern must be {MaxSearchPatternLength} characters or fewer.";

            return null;
        }
    }
}
