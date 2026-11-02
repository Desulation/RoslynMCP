using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Roslyn.Mcp.Services
{
    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Injected DI service; tools call its members through the instance.")]
    public class SecurityValidator
    {
        private readonly HashSet<string> _allowedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".sln", ".slnx", ".csproj" };

        /// <summary>
        /// Accepts an absolute path to a .sln, .slnx or .csproj file, or to a directory
        /// that contains one (resolved later by CodeAnalysisService.ResolveSolutionPath).
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

        public string SanitizeSearchPattern(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                return "*";

            // Remove potentially dangerous characters
            return Regex.Replace(pattern, @"[^\w*?.]", "");
        }
    }
}
