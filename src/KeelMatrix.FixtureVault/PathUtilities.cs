using System.Text;

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KeelMatrix.FixtureVault;

internal static class PathUtilities
{
    internal static string FindRepositoryRoot(string startingDirectory)
    {
        string current = Path.GetFullPath(startingDirectory);
        if (!Directory.Exists(current))
        {
            current = Path.GetDirectoryName(current) ?? Directory.GetCurrentDirectory();
        }

        var directory = new DirectoryInfo(current);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, FixtureVaultContract.PolicyFileName)))
            {
                return directory.FullName;
            }

            if (File.Exists(Path.Combine(directory.FullName, ".git")) ||
                Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return current;
    }

    internal static bool TryResolveRoot(
        string repositoryRoot,
        string configuredRoot,
        out string fullPath,
        out string relativePath,
        out string error)
    {
        fullPath = string.Empty;
        relativePath = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(configuredRoot) || configuredRoot.Contains('\0'))
        {
            error = "A configured fixture root is empty or invalid.";
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(Path.IsPathRooted(configuredRoot)
                ? configuredRoot
                : Path.Combine(repositoryRoot, configuredRoot));
            if (!IsWithin(repositoryRoot, fullPath))
            {
                error = "A configured fixture root must remain inside the repository root.";
                return false;
            }

            if (!TryValidateNoReparsePoints(repositoryRoot, fullPath, out error))
            {
                return false;
            }

            relativePath = NormalizeRelative(repositoryRoot, fullPath);
            if (relativePath == ".")
            {
                relativePath = string.Empty;
            }

            if (!Directory.Exists(fullPath))
            {
                error = "A configured fixture root does not exist or is not a directory.";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = "A configured fixture root could not be resolved safely.";
            return false;
        }
    }

    private static bool TryValidateNoReparsePoints(
        string repositoryRoot,
        string fullPath,
        out string error)
    {
        error = string.Empty;
        string relativePath = Path.GetRelativePath(repositoryRoot, fullPath);
        DirectoryInfo current = new(Path.GetFullPath(repositoryRoot));

        if (!TryReadDirectoryAttributes(current, out FileAttributes attributes, out error) ||
            IsLinked(attributes, current))
        {
            error = "A configured fixture root is beneath a linked or reparse path and cannot be scanned safely.";
            return false;
        }

        if (relativePath == ".")
        {
            return true;
        }

        foreach (string component in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = new DirectoryInfo(Path.Combine(current.FullName, component));
            if (!TryReadDirectoryAttributes(current, out attributes, out error))
            {
                return false;
            }

            if (IsLinked(attributes, current))
            {
                error = "A configured fixture root is beneath a linked or reparse path and cannot be scanned safely.";
                return false;
            }
        }

        return true;
    }

    private static bool TryReadDirectoryAttributes(
        DirectoryInfo directory,
        out FileAttributes attributes,
        out string error)
    {
        try
        {
            attributes = directory.Attributes;
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            attributes = default;
            error = "A configured fixture root could not be resolved safely.";
            return false;
        }
    }

    private static bool IsLinked(FileAttributes attributes, FileSystemInfo entry)
    {
        return (attributes & FileAttributes.ReparsePoint) != 0 || entry.LinkTarget is not null;
    }

    internal static bool TryIsLinkedOrReparseFile(string path, out bool isLinkedOrReparse)
    {
        isLinkedOrReparse = false;
        try
        {
            var file = new FileInfo(path);
            if (file.LinkTarget is not null)
            {
                isLinkedOrReparse = true;
                return true;
            }

            if (!file.Exists)
            {
                return true;
            }

            isLinkedOrReparse = (file.Attributes & FileAttributes.ReparsePoint) != 0;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    internal static bool IsWithin(string parent, string candidate)
    {
        string normalizedParent = EnsureTrailingSeparator(Path.GetFullPath(parent));
        string normalizedCandidate = Path.GetFullPath(candidate);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return normalizedCandidate.Equals(normalizedParent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), comparison)
            || normalizedCandidate.StartsWith(normalizedParent, comparison);
    }

    internal static string NormalizeRelative(string repositoryRoot, string fullPath)
    {
        string relative = Path.GetRelativePath(repositoryRoot, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

        if (relative == ".")
        {
            return relative;
        }

        while (relative.StartsWith("./", StringComparison.Ordinal))
        {
            relative = relative[2..];
        }

        return relative;
    }

    internal static string NormalizeComparisonPath(string relativePath)
    {
        string normalized = relativePath.Replace('\\', '/').Normalize(NormalizationForm.FormC);
        return normalized.ToUpperInvariant();
    }

    internal static string EscapeDiagnosticPath(string path)
    {
        var escaped = new StringBuilder(path.Length);
        foreach (char character in path)
        {
            switch (character)
            {
                case '\0':
                    escaped.Append("\\0");
                    break;
                case '\a':
                    escaped.Append("\\a");
                    break;
                case '\b':
                    escaped.Append("\\b");
                    break;
                case '\t':
                    escaped.Append("\\t");
                    break;
                case '\n':
                    escaped.Append("\\n");
                    break;
                case '\v':
                    escaped.Append("\\v");
                    break;
                case '\f':
                    escaped.Append("\\f");
                    break;
                case '\r':
                    escaped.Append("\\r");
                    break;
                case '\u001b':
                    escaped.Append("\\x1B");
                    break;
                case char control when char.IsControl(control) || control == '\u007f':
                    escaped.Append("\\u").Append(((int)character).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
                    break;
                default:
                    escaped.Append(character);
                    break;
            }
        }

        return escaped.ToString();
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }
}

internal enum SafeFileReadStatus
{
    Success,
    Missing,
    FileTooLarge,
    GrewBeyondLimit,
    TotalLimitExceeded,
    Changed,
    Unsafe,
    Failed
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "The opened stream is transferred to the caller on success and disposed on every failure path.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA2101", Justification = "Unix path arguments use the runtime's UTF-8 narrow-string ABI on Linux and macOS.")]
internal static class SafeFileReader
{
    private const int ReadBufferSize = 32 * 1024;
    private const int UnixReadOnly = 0;
    private const int LinuxNonBlocking = 0x800;
    private const int LinuxCloseOnExec = 0x80000;
    private const int LinuxGenericDirectory = 0x10000;
    private const int LinuxGenericNoFollow = 0x20000;
    private const int LinuxArmDirectory = 0x4000;
    private const int LinuxArmNoFollow = 0x8000;
    private const int MacNonBlocking = 0x4;
    private const int MacCloseOnExec = 0x01000000;
    private const int MacDirectory = 0x00100000;
    private const int MacNoFollow = 0x00000100;
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixRegularFile = 0x8000;
    private const int LinuxAtFileDescriptor = -100;
    private const int LinuxAtSymlinkNoFollow = 0x100;
    private const int LinuxAtEmptyPath = 0x1000;
    private const uint LinuxStatxType = 0x00000001;
    private const int LinuxStatxModeOffset = 0x1C;

    internal static SafeFileReadStatus TryReadBytes(
        string repositoryRoot,
        string path,
        long maximumBytes,
        long? remainingTotalBytes,
        out byte[] bytes,
        out long bytesRead,
        Action? afterInitialLengthRead = null)
    {
        bytes = [];
        bytesRead = 0;
        if (maximumBytes < 0 || remainingTotalBytes is < 0)
        {
            return SafeFileReadStatus.Failed;
        }

        if (!TryOpenRegularFile(repositoryRoot, path, out FileStream? stream, out SafeFileReadStatus openStatus))
        {
            return openStatus;
        }

        FileStream fileStream = stream!;
        using (fileStream)
        {
            long initialLength;
            try
            {
                initialLength = fileStream.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return SafeFileReadStatus.Failed;
            }

            afterInitialLengthRead?.Invoke();
            SafeFileReadStatus initialLimit = CheckLimits(initialLength, maximumBytes, remainingTotalBytes);
            if (initialLimit != SafeFileReadStatus.Success)
            {
                return initialLimit;
            }

            using var contents = new MemoryStream((int)initialLength);
            byte[] buffer = new byte[ReadBufferSize];
            long totalRead = 0;
            try
            {
                while (true)
                {
                    int read = fileStream.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                    {
                        break;
                    }

                    totalRead += read;
                    SafeFileReadStatus readLimit = CheckLimits(totalRead, maximumBytes, remainingTotalBytes);
                    bytesRead = totalRead;
                    if (readLimit == SafeFileReadStatus.TotalLimitExceeded)
                    {
                        return readLimit;
                    }

                    if (readLimit == SafeFileReadStatus.FileTooLarge)
                    {
                        // The initial length was within the per-file limit, so crossing it while
                        // reading proves that the file changed during inspection. Preserve the
                        // distinction from a file that was already oversized when it was opened.
                        return SafeFileReadStatus.GrewBeyondLimit;
                    }

                    contents.Write(buffer, 0, read);
                }

                long finalLength = fileStream.Length;
                if (initialLength != totalRead || finalLength != totalRead)
                {
                    bytesRead = totalRead;
                    return SafeFileReadStatus.Changed;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                bytesRead = totalRead;
                return SafeFileReadStatus.Changed;
            }

            bytes = contents.ToArray();
            bytesRead = totalRead;
            return SafeFileReadStatus.Success;
        }
    }

    private static SafeFileReadStatus CheckLimits(long length, long maximumBytes, long? remainingTotalBytes)
    {
        if (length > maximumBytes)
        {
            return remainingTotalBytes is not null && length > remainingTotalBytes.Value
                ? SafeFileReadStatus.TotalLimitExceeded
                : SafeFileReadStatus.FileTooLarge;
        }

        return remainingTotalBytes is not null && length > remainingTotalBytes.Value
            ? SafeFileReadStatus.TotalLimitExceeded
            : SafeFileReadStatus.Success;
    }

    private static bool TryOpenRegularFile(
        string repositoryRoot,
        string path,
        out FileStream? stream,
        out SafeFileReadStatus status)
    {
        stream = null;
        status = SafeFileReadStatus.Unsafe;
        bool success = false;
        string fullRoot;
        string fullPath;
        string relativePath;
        try
        {
            fullRoot = Path.GetFullPath(repositoryRoot);
            fullPath = Path.GetFullPath(path);
            if (!PathUtilities.IsWithin(fullRoot, fullPath) ||
                fullPath.Equals(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                return false;
            }

            relativePath = Path.GetRelativePath(fullRoot, fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            status = SafeFileReadStatus.Failed;
            return false;
        }

        if (!TryValidateRegularFilePath(fullRoot, fullPath, relativePath, out bool exists))
        {
            status = exists ? SafeFileReadStatus.Unsafe : SafeFileReadStatus.Missing;
            return false;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            success = TryOpenUnixRegularFile(fullRoot, relativePath, out stream, out status);
            return success;
        }

        SafeFileHandle? handle = null;
        try
        {
            handle = File.OpenHandle(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                FileOptions.SequentialScan);
            stream = new FileStream(handle!, FileAccess.Read, ReadBufferSize, isAsync: false);
            if (!TryValidateRegularFilePath(fullRoot, fullPath, relativePath, out exists) ||
                !exists ||
                !TryGetFinalWindowsPath(handle!, out string resolvedPath) ||
                !PathUtilities.IsWithin(fullRoot, resolvedPath))
            {
                stream.Dispose();
                stream = null;
                handle = null;
                status = SafeFileReadStatus.Unsafe;
                return false;
            }

            handle = null;
            status = SafeFileReadStatus.Success;
            success = true;
            return true;
        }
        catch (FileNotFoundException)
        {
            status = SafeFileReadStatus.Missing;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            status = SafeFileReadStatus.Unsafe;
            return false;
        }
        finally
        {
            handle?.Dispose();
            if (!success)
            {
                stream?.Dispose();
                stream = null;
            }
        }
    }

    private static bool TryValidateRegularFilePath(
        string fullRoot,
        string fullPath,
        string relativePath,
        out bool exists)
    {
        exists = false;
        var root = new DirectoryInfo(fullRoot);
        if (!TryReadFileSystemAttributes(root, out FileAttributes rootAttributes, out bool rootExists) ||
            !rootExists ||
            IsReparse(rootAttributes, root))
        {
            return false;
        }

        string[] components = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        string currentPath = fullRoot;
        for (int index = 0; index < components.Length; index++)
        {
            currentPath = Path.Combine(currentPath, components[index]);
            FileSystemInfo entry = index == components.Length - 1
                ? new FileInfo(currentPath)
                : new DirectoryInfo(currentPath);
            if (!TryReadFileSystemAttributes(entry, out FileAttributes attributes, out bool entryExists))
            {
                return false;
            }

            if (!entryExists)
            {
                return false;
            }

            exists = true;
            if (IsReparse(attributes, entry) ||
                index < components.Length - 1 && (entry is not DirectoryInfo || (attributes & FileAttributes.Directory) == 0) ||
                index == components.Length - 1 && (entry is DirectoryInfo || (attributes & FileAttributes.Directory) != 0))
            {
                return false;
            }
        }

        return components.Length > 0;
    }

    private static bool TryReadFileSystemAttributes(FileSystemInfo entry, out FileAttributes attributes, out bool exists)
    {
        try
        {
            attributes = entry.Attributes;
            exists = entry.Exists;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            attributes = default;
            exists = false;
            return false;
        }
    }

    private static bool IsReparse(FileAttributes attributes, FileSystemInfo entry) =>
        (attributes & FileAttributes.ReparsePoint) != 0 || entry.LinkTarget is not null;

    [DllImport("libc", EntryPoint = "open", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int UnixOpen([MarshalAs(UnmanagedType.LPStr)] string path, int flags);

    [DllImport("libc", EntryPoint = "openat", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int UnixOpenAt(int directoryFileDescriptor, [MarshalAs(UnmanagedType.LPStr)] string path, int flags);

    [DllImport("libc", EntryPoint = "lstat", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int UnixLStat([MarshalAs(UnmanagedType.LPStr)] string path, IntPtr buffer);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int UnixFStat(int fileDescriptor, IntPtr buffer);

    [DllImport("libc", EntryPoint = "statx", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int UnixStatX(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPStr)] string path,
        int flags,
        uint mask,
        IntPtr buffer);

    private static bool TryOpenUnixRegularFile(
        string fullRoot,
        string relativePath,
        out FileStream? stream,
        out SafeFileReadStatus status)
    {
        stream = null;
        status = SafeFileReadStatus.Unsafe;
        int closeOnExec;
        int directory;
        int noFollow;
        int nonBlocking;
        if (OperatingSystem.IsMacOS())
        {
            closeOnExec = MacCloseOnExec;
            directory = MacDirectory;
            noFollow = MacNoFollow;
            nonBlocking = MacNonBlocking;
        }
        else if (OperatingSystem.IsLinux())
        {
            closeOnExec = LinuxCloseOnExec;
            nonBlocking = LinuxNonBlocking;
            // Linux ARM and ARM64 override O_DIRECTORY/O_NOFOLLOW in their exported
            // ABI headers; the other supported Linux architectures use the generic values.
            (directory, noFollow) = GetLinuxOpenBoundaryFlags();
        }
        else
        {
            return false;
        }
        int directoryFlags = closeOnExec | directory | noFollow;
        int rootDescriptor = UnixOpen(fullRoot, UnixReadOnly | directoryFlags);
        if (rootDescriptor < 0)
        {
            return false;
        }

        var directoryHandles = new List<SafeFileHandle>
        {
            new SafeFileHandle((IntPtr)rootDescriptor, ownsHandle: true)
        };
        try
        {
            string[] components = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            SafeFileHandle current = directoryHandles[0];
            for (int index = 0; index < components.Length - 1; index++)
            {
                int descriptor = UnixOpenAt(current.DangerousGetHandle().ToInt32(), components[index], UnixReadOnly | directoryFlags);
                if (descriptor < 0)
                {
                    return false;
                }

                var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
                directoryHandles.Add(handle);
                current = handle;
            }

            if (!IsUnixRegularPath(Path.Combine(fullRoot, relativePath)))
            {
                return false;
            }

            int fileFlags = UnixReadOnly | closeOnExec | noFollow | nonBlocking;
            int fileDescriptor = UnixOpenAt(current.DangerousGetHandle().ToInt32(), components[^1], fileFlags);
            if (fileDescriptor < 0)
            {
                return false;
            }

            SafeFileHandle? fileHandle = new SafeFileHandle((IntPtr)fileDescriptor, ownsHandle: true);
            try
            {
                if (!IsUnixRegularFile(fileDescriptor))
                {
                    return false;
                }

                stream = new FileStream(fileHandle, FileAccess.Read, ReadBufferSize, isAsync: false);
                fileHandle = null;
                status = SafeFileReadStatus.Success;
                return true;
            }
            finally
            {
                fileHandle?.Dispose();
            }
        }
        finally
        {
            foreach (SafeFileHandle handle in directoryHandles)
            {
                handle.Dispose();
            }
        }
    }

    private static bool IsUnixRegularPath(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            return IsLinuxRegularPath(path);
        }

        IntPtr statBuffer = Marshal.AllocHGlobal(256);
        try
        {
            return UnixLStat(path, statBuffer) == 0 && IsRegularMode(ReadUnixMode(statBuffer));
        }
        finally
        {
            Marshal.FreeHGlobal(statBuffer);
        }
    }

    private static bool IsUnixRegularFile(int fileDescriptor)
    {
        if (OperatingSystem.IsLinux())
        {
            return IsLinuxRegularFile(fileDescriptor);
        }

        IntPtr statBuffer = Marshal.AllocHGlobal(256);
        try
        {
            return UnixFStat(fileDescriptor, statBuffer) == 0 && IsRegularMode(ReadUnixMode(statBuffer));
        }
        finally
        {
            Marshal.FreeHGlobal(statBuffer);
        }
    }

    private static bool IsLinuxRegularPath(string path)
    {
        IntPtr statBuffer = Marshal.AllocHGlobal(256);
        try
        {
            return UnixStatX(
                       LinuxAtFileDescriptor,
                       path,
                       LinuxAtSymlinkNoFollow,
                       LinuxStatxType,
                       statBuffer) == 0 &&
                   IsRegularMode(ReadLinuxStatxMode(statBuffer));
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(statBuffer);
        }
    }

    private static bool IsLinuxRegularFile(int fileDescriptor)
    {
        IntPtr statBuffer = Marshal.AllocHGlobal(256);
        try
        {
            return UnixStatX(
                       fileDescriptor,
                       string.Empty,
                       LinuxAtEmptyPath,
                       LinuxStatxType,
                       statBuffer) == 0 &&
                   IsRegularMode(ReadLinuxStatxMode(statBuffer));
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(statBuffer);
        }
    }

    private static int ReadUnixMode(IntPtr statBuffer) => OperatingSystem.IsMacOS()
        ? Marshal.ReadInt16(statBuffer, 4)
        : ReadLinuxStatxMode(statBuffer);

    // Linux statx is a fixed UAPI layout, unlike the libc struct stat layout.
    private static int ReadLinuxStatxMode(IntPtr statBuffer) => Marshal.ReadInt16(statBuffer, LinuxStatxModeOffset);

    private static (int Directory, int NoFollow) GetLinuxOpenBoundaryFlags() =>
        RuntimeInformation.OSArchitecture is Architecture.Arm or Architecture.Arm64
            ? (LinuxArmDirectory, LinuxArmNoFollow)
            : (LinuxGenericDirectory, LinuxGenericNoFollow);

    private static bool IsRegularMode(int mode) => (mode & UnixFileTypeMask) == UnixRegularFile;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle fileHandle,
        [Out] char[] path,
        uint pathLength,
        uint flags);

    private static bool TryGetFinalWindowsPath(SafeFileHandle handle, out string path)
    {
        path = string.Empty;
        char[] buffer = new char[512];
        uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length)
        {
            return false;
        }

        path = new string(buffer, 0, (int)length);
        if (path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
        {
            path = "\\\\" + path[8..];
        }
        else if (path.StartsWith("\\\\?\\", StringComparison.Ordinal))
        {
            path = path[4..];
        }

        return true;
    }
}

internal sealed record SafeFileEntry(string FullPath, string RelativePath);

internal sealed record WalkResult(
    IReadOnlyList<SafeFileEntry> Files,
    IReadOnlyList<string> ReparsePaths,
    ScanError? Error);

internal delegate WalkResult FixtureFileWalk(
    string repositoryRoot,
    string root,
    bool failOnAccessErrors,
    Func<string, GlobMatchStatus>? shouldPruneDirectory);

internal enum GlobMatchStatus
{
    NoMatch,
    Match,
    Failure
}

internal readonly record struct GlobMatchResult(
    GlobMatchStatus Status,
    long Steps,
    long WorkBound);

internal sealed class GlobMatchBudget
{
    // This budget is shared by every ignored-path comparison in one scan, including
    // active-root traversal, directory pruning, and repository path-policy discovery.
    internal const long MaximumAggregateSteps = 50_000_000;

    internal GlobMatchBudget(long maximumSteps = MaximumAggregateSteps)
    {
        MaximumSteps = maximumSteps > 0
            ? maximumSteps
            : throw new ArgumentOutOfRangeException(nameof(maximumSteps));
    }

    internal long MaximumSteps { get; }

    internal long Steps { get; private set; }

    internal bool TryConsume()
    {
        if (Steps >= MaximumSteps)
        {
            return false;
        }

        Steps++;
        return true;
    }
}

internal static class SafeFileWalker
{
    private const int MaximumEntries = 100_000;

    internal static WalkResult Walk(
        string repositoryRoot,
        string root,
        bool failOnAccessErrors,
        Func<string, GlobMatchStatus>? shouldPruneDirectory = null)
    {
        var files = new List<SafeFileEntry>();
        var reparsePaths = new List<string>();
        var pending = new Stack<DirectoryInfo>();
        DirectoryInfo startingDirectory = new(root);
        GlobMatchStatus startingDirectoryStatus = shouldPruneDirectory?.Invoke(
            PathUtilities.NormalizeRelative(repositoryRoot, startingDirectory.FullName)) ?? GlobMatchStatus.NoMatch;
        if (startingDirectoryStatus == GlobMatchStatus.Failure)
        {
            return new WalkResult(
                files,
                reparsePaths,
                new ScanError(FixtureVaultContract.IgnoredPathMatchingErrorCode, "Ignored path matching could not be completed safely."));
        }

        if (startingDirectoryStatus == GlobMatchStatus.Match)
        {
            return new WalkResult(files, reparsePaths, null);
        }

        pending.Push(startingDirectory);
        int entriesSeen = 0;

        while (pending.Count > 0)
        {
            DirectoryInfo directory = pending.Pop();
            if (!IsSafeDirectoryForEnumeration(repositoryRoot, directory))
            {
                return new WalkResult(files, reparsePaths, new ScanError(
                    "FV-E002",
                    "A configured fixture root could not be inspected completely."));
            }

            try
            {
                foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
                {
                    // The directory and every current ancestor were validated immediately before
                    // enumeration started, and again before each yielded entry. This prevents a
                    // queued directory whose parent was replaced with a link from contributing
                    // entries to the report.
                    if (!IsSafeDirectoryForEnumeration(repositoryRoot, directory))
                    {
                        return new WalkResult(files, reparsePaths, new ScanError(
                            "FV-E002",
                            "A configured fixture root could not be inspected completely."));
                    }

                    if (!IsWithinEntryLimit(ref entriesSeen))
                    {
                        return new WalkResult(files, reparsePaths, new ScanError(
                            "FV-E003",
                            "The scan exceeded its filesystem entry safety limit."));
                    }

                    FileAttributes attributes;
                    try
                    {
                        attributes = entry.Attributes;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
                    {
                        if (!failOnAccessErrors)
                        {
                            continue;
                        }

                        return new WalkResult(files, reparsePaths, new ScanError(
                            "FV-E002",
                            "A configured fixture root could not be inspected completely."));
                    }

                    string relativePath = PathUtilities.NormalizeRelative(repositoryRoot, entry.FullName);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        reparsePaths.Add(relativePath);
                        continue;
                    }

                    if (entry is DirectoryInfo childDirectory)
                    {
                        GlobMatchStatus directoryStatus = shouldPruneDirectory?.Invoke(relativePath) ?? GlobMatchStatus.NoMatch;
                        if (directoryStatus == GlobMatchStatus.Failure)
                        {
                            return new WalkResult(
                                files,
                                reparsePaths,
                                new ScanError(FixtureVaultContract.IgnoredPathMatchingErrorCode, "Ignored path matching could not be completed safely."));
                        }

                        if (directoryStatus != GlobMatchStatus.Match)
                        {
                            pending.Push(childDirectory);
                        }
                    }
                    else
                    {
                        if (!IsSafeDirectoryForEnumeration(repositoryRoot, directory))
                        {
                            return new WalkResult(files, reparsePaths, new ScanError(
                                "FV-E002",
                                "A configured fixture root could not be inspected completely."));
                        }

                        files.Add(new SafeFileEntry(entry.FullName, relativePath));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                if (!failOnAccessErrors)
                {
                    continue;
                }

                return new WalkResult(files, reparsePaths, new ScanError(
                    "FV-E002",
                    "A configured fixture root could not be inspected completely."));
            }
        }

        return new WalkResult(files, reparsePaths, null);
    }

    private static bool IsSafeDirectoryForEnumeration(string repositoryRoot, DirectoryInfo directory)
    {
        try
        {
            string fullRepositoryRoot = Path.GetFullPath(repositoryRoot);
            string fullDirectoryPath = Path.GetFullPath(directory.FullName);
            if (!PathUtilities.IsWithin(fullRepositoryRoot, fullDirectoryPath))
            {
                return false;
            }

            DirectoryInfo current = new(fullRepositoryRoot);
            if (!IsSafeDirectory(current))
            {
                return false;
            }

            string relativePath = Path.GetRelativePath(fullRepositoryRoot, fullDirectoryPath);
            if (relativePath == ".")
            {
                return true;
            }

            foreach (string component in relativePath.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = new DirectoryInfo(Path.Combine(current.FullName, component));
                if (!IsSafeDirectory(current))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsSafeDirectory(DirectoryInfo directory)
    {
        FileAttributes attributes = directory.Attributes;
        return directory.Exists &&
               (attributes & FileAttributes.Directory) != 0 &&
               (attributes & FileAttributes.ReparsePoint) == 0 &&
               directory.LinkTarget is null;
    }

    private static bool IsWithinEntryLimit(ref int entriesSeen) => ++entriesSeen <= MaximumEntries;
}

internal sealed class GlobMatcher
{
    private readonly GlobToken[] tokens;
    private readonly int[] globStarSlashOrdinals;
    private readonly int[] globStarSlashNextStates;
    private readonly int stateCount;

    private GlobMatcher(GlobToken[] tokens, int[] globStarSlashOrdinals)
    {
        this.tokens = tokens;
        this.globStarSlashOrdinals = globStarSlashOrdinals;
        globStarSlashNextStates = new int[globStarSlashOrdinals.Count(ordinal => ordinal >= 0)];
        for (int tokenIndex = 0; tokenIndex < globStarSlashOrdinals.Length; tokenIndex++)
        {
            int ordinal = globStarSlashOrdinals[tokenIndex];
            if (ordinal >= 0)
            {
                globStarSlashNextStates[ordinal] = tokenIndex + 1;
            }
        }

        stateCount = tokens.Length + 1 + globStarSlashNextStates.Length;
    }

    internal static bool TryCreate(string pattern, out GlobMatcher? matcher)
    {
        matcher = null;
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > 256 || pattern.Contains('\0'))
        {
            return false;
        }

        if (IsRootedOrDriveQualified(pattern))
        {
            return false;
        }

        string normalized = PathUtilities.NormalizeComparisonPath(pattern.Replace('\\', '/'));
        if (normalized.Split('/').Any(segment => segment == ".."))
        {
            return false;
        }

        var tokens = new List<GlobToken>(normalized.Length);
        var globStarSlashOrdinals = new List<int>(normalized.Length);
        int globStarSlashCount = 0;
        for (int i = 0; i < normalized.Length; i++)
        {
            char character = normalized[i];
            if (character == '*' && i + 2 < normalized.Length && normalized[i + 1] == '*' && normalized[i + 2] == '/')
            {
                tokens.Add(new GlobToken(GlobTokenKind.GlobStarSlash, '\0'));
                globStarSlashOrdinals.Add(globStarSlashCount++);
                i += 2;
            }
            else if (character == '*' && i + 1 < normalized.Length && normalized[i + 1] == '*')
            {
                tokens.Add(new GlobToken(GlobTokenKind.GlobStar, '\0'));
                globStarSlashOrdinals.Add(-1);
                i++;
            }
            else if (character == '*')
            {
                tokens.Add(new GlobToken(GlobTokenKind.SegmentStar, '\0'));
                globStarSlashOrdinals.Add(-1);
            }
            else if (character == '?')
            {
                tokens.Add(new GlobToken(GlobTokenKind.SingleCharacter, '\0'));
                globStarSlashOrdinals.Add(-1);
            }
            else
            {
                tokens.Add(new GlobToken(GlobTokenKind.Literal, character));
                globStarSlashOrdinals.Add(-1);
            }
        }

        matcher = new GlobMatcher(tokens.ToArray(), globStarSlashOrdinals.ToArray());
        return true;
    }

    private static bool IsRootedOrDriveQualified(string pattern)
    {
        if (pattern[0] is '/' or '\\')
        {
            return true;
        }

        return pattern.Length >= 2 &&
               pattern[1] == ':' &&
               ((pattern[0] >= 'A' && pattern[0] <= 'Z') || (pattern[0] >= 'a' && pattern[0] <= 'z'));
    }

    internal int StateCount => stateCount;

    internal int TokenCount => tokens.Length;

    internal GlobMatchResult Match(string relativePath, GlobMatchBudget? budget = null)
    {
        string normalizedPath = PathUtilities.NormalizeComparisonPath(relativePath);
        // Each path character performs one clear pass, one state pass, and one epsilon pass.
        // Therefore workBound = (2 * stateCount + tokenCount) * (pathLength + 1), with no backtracking.
        long workBound = checked((2L * stateCount + tokens.Length) * ((long)normalizedPath.Length + 1));
        long steps = 0;
        bool[] activeStates = new bool[stateCount];
        bool[] nextStates = new bool[stateCount];
        activeStates[0] = true;

        if (!TryCloseEpsilon(activeStates, ref steps, workBound, budget))
        {
            return new GlobMatchResult(GlobMatchStatus.Failure, steps, workBound);
        }

        foreach (char pathCharacter in normalizedPath)
        {
            for (int state = 0; state < nextStates.Length; state++)
            {
                if (!TryConsume(ref steps, workBound, budget))
                {
                    return new GlobMatchResult(GlobMatchStatus.Failure, steps, workBound);
                }

                nextStates[state] = false;
            }

            for (int state = 0; state < activeStates.Length; state++)
            {
                if (!TryConsume(ref steps, workBound, budget))
                {
                    return new GlobMatchResult(GlobMatchStatus.Failure, steps, workBound);
                }

                if (!activeStates[state])
                {
                    continue;
                }

                if (state == tokens.Length)
                {
                    continue;
                }

                if (state < tokens.Length)
                {
                    GlobToken token = tokens[state];
                    switch (token.Kind)
                    {
                        case GlobTokenKind.Literal when token.Value == pathCharacter:
                        case GlobTokenKind.SingleCharacter when pathCharacter != '/':
                            nextStates[state + 1] = true;
                            break;
                        case GlobTokenKind.SegmentStar when pathCharacter != '/':
                            nextStates[state] = true;
                            break;
                        case GlobTokenKind.GlobStar:
                            nextStates[state] = true;
                            break;
                        case GlobTokenKind.GlobStarSlash:
                            nextStates[GetInsideState(state)] = true;
                            if (pathCharacter == '/')
                            {
                                nextStates[state + 1] = true;
                            }

                            break;
                    }

                    continue;
                }

                int ordinal = state - tokens.Length - 1;
                nextStates[state] = true;
                if (pathCharacter == '/')
                {
                    nextStates[globStarSlashNextStates[ordinal]] = true;
                }
            }

            if (!TryCloseEpsilon(nextStates, ref steps, workBound, budget))
            {
                return new GlobMatchResult(GlobMatchStatus.Failure, steps, workBound);
            }

            (activeStates, nextStates) = (nextStates, activeStates);
        }

        GlobMatchStatus status = activeStates[tokens.Length]
            ? GlobMatchStatus.Match
            : GlobMatchStatus.NoMatch;
        return new GlobMatchResult(status, steps, workBound);
    }

    private int GetInsideState(int tokenIndex)
    {
        int ordinal = globStarSlashOrdinals[tokenIndex];
        return tokens.Length + 1 + ordinal;
    }

    private bool TryCloseEpsilon(
        bool[] states,
        ref long steps,
        long workBound,
        GlobMatchBudget? budget)
    {
        for (int state = 0; state < tokens.Length; state++)
        {
            if (!TryConsume(ref steps, workBound, budget))
            {
                return false;
            }

            if (states[state] && tokens[state].Kind is GlobTokenKind.SegmentStar or GlobTokenKind.GlobStar or GlobTokenKind.GlobStarSlash)
            {
                states[state + 1] = true;
            }
        }

        return true;
    }

    private static bool TryConsume(ref long steps, long workBound, GlobMatchBudget? budget)
    {
        if (steps >= workBound || budget is not null && !budget.TryConsume())
        {
            return false;
        }

        steps++;
        return true;
    }

    private readonly record struct GlobToken(GlobTokenKind Kind, char Value);

    private enum GlobTokenKind
    {
        Literal,
        SingleCharacter,
        SegmentStar,
        GlobStar,
        GlobStarSlash
    }
}
