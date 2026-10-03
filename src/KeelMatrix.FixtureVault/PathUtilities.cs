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
        string? policyWithoutRepositoryMarker = null;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, ".git")) ||
                Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            if (policyWithoutRepositoryMarker is null &&
                File.Exists(Path.Combine(directory.FullName, FixtureVaultContract.PolicyFileName)))
            {
                policyWithoutRepositoryMarker = directory.FullName;
            }

            directory = directory.Parent;
        }

        return policyWithoutRepositoryMarker ?? current;
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
            PathContainmentResult containment = IsWithin(repositoryRoot, fullPath);
            if (containment != PathContainmentResult.Within)
            {
                error = containment == PathContainmentResult.BudgetExhausted
                    ? FixtureVaultContract.FilesystemTraversalErrorMessage
                    : "A configured fixture root must remain inside the repository root.";
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

    internal static PathContainmentResult IsWithin(string parent, string candidate)
    {
        return SafePathBoundary.TryIsWithin(parent, candidate);
    }

    internal static bool IsWithinUncharged(string parent, string candidate)
    {
        return SafePathBoundary.TryIsWithinUncharged(parent, candidate);
    }

    internal static string ToWindowsHandlePath(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return path;
        }

        string fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith("\\\\?\\", StringComparison.Ordinal))
        {
            return fullPath;
        }

        return fullPath.StartsWith("\\\\", StringComparison.Ordinal)
            ? "\\\\?\\UNC\\" + fullPath[2..]
            : "\\\\?\\" + fullPath;
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
        // This is only the portability-collision key for FV003. It must never be
        // used to authorize an exact manifest relationship.
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
                case '\\':
                    escaped.Append("\\\\");
                    break;
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
                case char presentationControl when
                    char.GetUnicodeCategory(presentationControl) == System.Globalization.UnicodeCategory.Format ||
                    char.GetUnicodeCategory(presentationControl) is System.Globalization.UnicodeCategory.LineSeparator or
                        System.Globalization.UnicodeCategory.ParagraphSeparator:
                    escaped.Append("\\u").Append(((int)character).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
                    break;
                default:
                    escaped.Append(character);
                    break;
            }
        }

        return escaped.ToString();
    }

}

internal readonly record struct UnixFileIdentity(long Device, long Inode);

internal enum PathContainmentResult
{
    Outside,
    Within,
    BudgetExhausted
}

// File identity is intentionally separate from NormalizeComparisonPath. The latter is
// a portability-collision key; this identity is obtained from the filesystem and is
// used for trusted containment and traversal deduplication. Classification remains
// keyed by the exact repository-relative path so aliases cannot be dropped.
internal readonly record struct FileSystemIdentity(ulong VolumeOrDevice, ulong FileOrInode);

internal readonly record struct DirectoryEntryReadResult(IntPtr Entry, int ErrorNumber);

internal static class WindowsPathResolver
{
    private const int InitialBufferLength = 512;
    private const int MaximumBufferLength = 32 * 1024;

    internal static bool TryGetFinalPath(SafeFileHandle handle, out string path)
    {
        path = string.Empty;
        char[] buffer = new char[InitialBufferLength];
        for (int attempt = 0; attempt < 8; attempt++)
        {
            uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0)
            {
                return false;
            }

            if (length < buffer.Length)
            {
                path = Normalize(new string(buffer, 0, (int)length));
                return true;
            }

            long requiredLength = (long)length + 1;
            if (requiredLength > MaximumBufferLength)
            {
                return false;
            }

            buffer = new char[(int)requiredLength];
        }

        return false;
    }

    private static string Normalize(string path)
    {
        if (path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
        {
            return "\\\\" + path[8..];
        }

        return path.StartsWith("\\\\?\\", StringComparison.Ordinal)
            ? path[4..]
            : path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle fileHandle,
        [Out] char[] path,
        uint pathLength,
        uint flags);
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA2101", Justification = "Unix path arguments use the runtime's UTF-8 narrow-string ABI on Linux and macOS.")]
internal sealed class SafePathBoundary : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
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
    private const int UnixDirectory = 0x4000;
    private const int UnixRegularFile = 0x8000;
    private const byte UnixDirectoryEntryUnknown = 0;
    private const byte UnixDirectoryEntryDirectory = 4;
    private const byte UnixDirectoryEntrySymbolicLink = 10;
    private const int UnixDirectoryEntryTypeOffset = 18;
    // Darwin's 64-bit dirent includes a 16-bit d_namlen before d_type.
    private const int MacDirectoryEntryTypeOffset = 20;
    private const int LinuxAtFileDescriptor = -100;
    private const int LinuxAtSymlinkNoFollow = 0x100;
    private const int LinuxAtEmptyPath = 0x1000;
    private const int MacAtSymlinkNoFollow = 0x20;
    private const uint LinuxStatxBasicStats = 0x000007ff;
    private const int LinuxStatxModeOffset = 0x1C;
    private const int LinuxStatxInodeOffset = 0x20;
    private const int LinuxStatxDeviceMajorOffset = 0x88;
    private const int LinuxStatxDeviceMinorOffset = 0x8C;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    private readonly SafeFileHandle? unixRootHandle;
    private readonly SafeFileHandle? windowsRootHandle;
    private readonly FileSystemIdentity rootIdentity;
    private readonly Dictionary<string, FileSystemIdentity> authorizedDirectoryIdentities = new(StringComparer.Ordinal);
    private static readonly AsyncLocal<Func<string, int, DirectoryEntryReadResult, DirectoryEntryReadResult?>?> DirectoryEntryReadHook = new();
    internal static Func<string, bool>? BoundaryMatchOverrideForTesting { get; set; }

    private SafePathBoundary(
        string rootPath,
        SafeFileHandle? unixRootHandle,
        SafeFileHandle? windowsRootHandle,
        FileSystemIdentity rootIdentity)
    {
        RootPath = rootPath;
        this.unixRootHandle = unixRootHandle;
        this.windowsRootHandle = windowsRootHandle;
        this.rootIdentity = rootIdentity;
        authorizedDirectoryIdentities[string.Empty] = rootIdentity;
    }

    internal string RootPath { get; }

    internal static bool TryCreate(string repositoryRoot, out SafePathBoundary? boundary)
    {
        boundary = null;
        string fullRoot;
        try
        {
            fullRoot = Path.GetFullPath(repositoryRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            int descriptor = UnixOpen(fullRoot, GetDirectoryFlags());
            if (descriptor < 0 || !TryGetUnixIdentity(descriptor, out UnixFileIdentity unixIdentity) ||
                !IsUnixDirectory(unixIdentity, descriptor))
            {
                if (descriptor >= 0)
                {
                    _ = UnixClose(descriptor);
                }

                return false;
            }

            boundary = new SafePathBoundary(
                fullRoot,
                new SafeFileHandle((IntPtr)descriptor, ownsHandle: true),
                null,
                ToFileSystemIdentity(unixIdentity));
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        SafeFileHandle rootHandle = CreateWindowsHandle(fullRoot, FileFlagBackupSemantics | FileFlagOpenReparsePoint);
        if (rootHandle.IsInvalid || !TryGetWindowsIdentity(rootHandle, out FileSystemIdentity identity))
        {
            rootHandle.Dispose();
            return false;
        }

        boundary = new SafePathBoundary(fullRoot, null, rootHandle, identity);
        return true;
    }

    internal bool Matches(string repositoryRoot) =>
        (BoundaryMatchOverrideForTesting?.Invoke(repositoryRoot) ?? true) &&
        TryGetPathIdentity(repositoryRoot, out FileSystemIdentity identity) && identity == rootIdentity;

    internal bool TryGetCanonicalPath(string path, out string canonicalPath)
    {
        canonicalPath = string.Empty;
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            const int maximumNativePathBytes = 32 * 1024;
            IntPtr resolvedPath = Marshal.AllocHGlobal(maximumNativePathBytes);
            try
            {
                IntPtr nativePath = UnixRealPath(fullPath, resolvedPath);
                if (nativePath == IntPtr.Zero || !TryReadStrictUtf8(nativePath, out canonicalPath))
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
            {
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(resolvedPath);
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            using SafeFileHandle handle = CreateWindowsHandle(
                fullPath,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint);
            if (handle.IsInvalid || !WindowsPathResolver.TryGetFinalPath(handle, out canonicalPath))
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        string containmentRoot = RootPath;
        if ((OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) &&
            !TryGetUnixCanonicalPath(RootPath, out containmentRoot))
        {
            canonicalPath = string.Empty;
            return false;
        }

        if (!TryGetPathIdentityCore(fullPath, out FileSystemIdentity requestedIdentity) ||
            !TryGetPathIdentityCore(canonicalPath, out FileSystemIdentity canonicalIdentity) ||
            requestedIdentity != canonicalIdentity ||
            !TryIsWithinUncharged(containmentRoot, canonicalPath))
        {
            canonicalPath = string.Empty;
            return false;
        }

        return true;
    }

    internal bool TryAuthorizeDirectory(string relativePath, FileSystemIdentity identity)
    {
        string key = NormalizeRelativeDirectoryKey(relativePath);
        if (authorizedDirectoryIdentities.TryGetValue(key, out FileSystemIdentity existing) && existing != identity)
        {
            return false;
        }

        authorizedDirectoryIdentities[key] = identity;
        return true;
    }

    internal bool TryOpenDirectory(string relativePath, out SafeFileHandle? handle) =>
        TryOpenDirectory(relativePath, expectedIdentity: null, out handle, out _);

    internal bool TryOpenDirectory(
        string relativePath,
        FileSystemIdentity? expectedIdentity,
        out SafeFileHandle? handle,
        out FileSystemIdentity actualIdentity)
    {
        handle = null;
        actualIdentity = default;
        if (!FilesystemTraversalBudgetContext.TryConsumePathWork(relativePath))
        {
            return false;
        }

        if (expectedIdentity is null &&
            authorizedDirectoryIdentities.TryGetValue(NormalizeRelativeDirectoryKey(relativePath), out FileSystemIdentity authorizedIdentity))
        {
            expectedIdentity = authorizedIdentity;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            if (!TryOpenUnixRelative(relativePath, GetDirectoryFlags(), out int descriptor) ||
                !TryGetUnixIdentity(descriptor, out UnixFileIdentity unixIdentity) ||
                !IsUnixDirectory(unixIdentity, descriptor))
            {
                if (descriptor >= 0)
                {
                    _ = UnixClose(descriptor);
                }

                return false;
            }

            actualIdentity = ToFileSystemIdentity(unixIdentity);
            if (expectedIdentity is not null && expectedIdentity.Value != actualIdentity)
            {
                _ = UnixClose(descriptor);
                return false;
            }

            handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        string path = CombineRelative(relativePath);
        if (!TryIsWithinUncharged(RootPath, path))
        {
            return false;
        }

        SafeFileHandle candidate = CreateWindowsHandle(path, FileFlagBackupSemantics | FileFlagOpenReparsePoint);
        if (candidate.IsInvalid ||
            !WindowsPathResolver.TryGetFinalPath(candidate, out string resolvedPath) ||
            !TryIsWithinUncharged(RootPath, resolvedPath) ||
            !TryGetWindowsFileAttributes(candidate, out FileAttributes attributes) ||
            !TryGetWindowsIdentity(candidate, out actualIdentity) ||
            (attributes & FileAttributes.ReparsePoint) != 0 ||
            (attributes & FileAttributes.Directory) == 0 ||
            expectedIdentity is not null && expectedIdentity.Value != actualIdentity)
        {
            candidate.Dispose();
            return false;
        }

        handle = candidate;
        return true;
    }

    internal bool TryOpenRegularFile(
        string relativePath,
        FileSystemIdentity? expectedIdentity,
        out FileStream? stream,
        out SafeFileReadStatus status)
    {
        stream = null;
        status = SafeFileReadStatus.Unsafe;
        if (!FilesystemTraversalBudgetContext.TryConsumePathWork(relativePath))
        {
            return false;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            string[] components = SplitRelativePath(relativePath);
            if (components.Length == 0 || !TryOpenUnixParent(components, out SafeFileHandle[] parents))
            {
                return false;
            }

            try
            {
                SafeFileHandle parent = parents[^1];
                int descriptor = UnixOpenAt(parent.DangerousGetHandle().ToInt32(), components[^1], GetFileFlags());
                if (descriptor < 0)
                {
                    status = SafeFileReadStatus.Missing;
                    return false;
                }

                SafeFileHandle fileHandle = new((IntPtr)descriptor, ownsHandle: true);
                try
                {
                    if (!TryGetUnixIdentity(descriptor, out UnixFileIdentity unixIdentity) ||
                        !IsUnixRegular(unixIdentity, descriptor) ||
                        expectedIdentity is not null && expectedIdentity.Value != ToFileSystemIdentity(unixIdentity))
                    {
                        status = SafeFileReadStatus.Unsafe;
                        return false;
                    }

                    stream = new FileStream(fileHandle, FileAccess.Read, 32 * 1024, isAsync: false);
                    fileHandle = null!;
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
                foreach (SafeFileHandle parentHandle in parents)
                {
                    parentHandle.Dispose();
                }
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        string path = CombineRelative(relativePath);
        if (!TryIsWithinUncharged(RootPath, path))
        {
            return false;
        }

        SafeFileHandle candidate = CreateWindowsHandle(path, FileFlagOpenReparsePoint);

        if (candidate.IsInvalid ||
            !WindowsPathResolver.TryGetFinalPath(candidate, out string resolvedPath) ||
            !TryIsWithinUncharged(RootPath, resolvedPath) ||
            !TryGetWindowsFileAttributes(candidate, out FileAttributes attributes) ||
            !TryGetWindowsIdentity(candidate, out FileSystemIdentity identity) ||
            (expectedIdentity is not null && expectedIdentity.Value != identity) ||
            (attributes & FileAttributes.ReparsePoint) != 0 ||
            (attributes & FileAttributes.Directory) != 0)
        {
            candidate.Dispose();
            status = SafeFileReadStatus.Unsafe;
            return false;
        }

        try
        {
            stream = new FileStream(candidate, FileAccess.Read, 32 * 1024, isAsync: false);
            candidate = null!;
            status = SafeFileReadStatus.Success;
            return true;
        }
        finally
        {
            candidate?.Dispose();
        }
    }

    internal bool TryPathExists(string relativePath)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            string[] components = SplitRelativePath(relativePath);
            if (components.Length == 0 || !TryOpenUnixParent(components, out SafeFileHandle[] parents))
            {
                return false;
            }

            try
            {
                int descriptor = UnixOpenAt(parents[^1].DangerousGetHandle().ToInt32(), components[^1], GetFileFlags());
                if (descriptor >= 0)
                {
                    _ = UnixClose(descriptor);
                    return true;
                }

                return UnixReadLinkAt(parents[^1].DangerousGetHandle().ToInt32(), components[^1]) >= 0;
            }
            finally
            {
                foreach (SafeFileHandle parentHandle in parents)
                {
                    parentHandle.Dispose();
                }
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        string path = CombineRelative(relativePath);
        if (!TryIsWithinUncharged(RootPath, path))
        {
            return false;
        }

        SafeFileHandle candidate = CreateWindowsHandle(path, FileFlagOpenReparsePoint | FileFlagBackupSemantics);
        if (candidate.IsInvalid)
        {
            candidate.Dispose();
            return false;
        }

        bool trusted =
            WindowsPathResolver.TryGetFinalPath(candidate, out string resolvedPath) &&
            TryIsWithinUncharged(RootPath, resolvedPath) &&
            TryGetWindowsIdentity(candidate, out _);
        candidate.Dispose();
        return trusted;
    }

    internal bool TryDuplicateDirectory(
        string relativePath,
        FileSystemIdentity? expectedIdentity,
        out int descriptor,
        out FileSystemIdentity actualIdentity)
    {
        descriptor = -1;
        actualIdentity = default;
        SafeFileHandle? handle = null;
        try
        {
            if (!TryOpenDirectory(relativePath, expectedIdentity, out handle, out actualIdentity) || handle is null)
            {
                return false;
            }

            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            {
                return false;
            }

            descriptor = UnixDup(handle.DangerousGetHandle().ToInt32());
            return descriptor >= 0;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    internal static bool TryOpenEntryAt(
        int parentDescriptor,
        string name,
        out int descriptor,
        out FileSystemIdentity identity,
        out bool isDirectory)
    {
        identity = default;
        isDirectory = false;
        descriptor = -1;
        descriptor = UnixOpenAt(parentDescriptor, name, GetFileFlags());
        if (descriptor < 0 || !TryGetUnixIdentity(descriptor, out UnixFileIdentity unixIdentity))
        {
            if (descriptor >= 0)
            {
                _ = UnixClose(descriptor);
            }

            return false;
        }

        identity = ToFileSystemIdentity(unixIdentity);
        isDirectory = IsUnixDirectory(unixIdentity, descriptor);
        return true;
    }

    internal static bool TryGetEntryMetadataAt(
        int parentDescriptor,
        string name,
        out FileSystemIdentity identity,
        out bool isDirectory,
        out bool isSymbolicLink)
    {
        identity = default;
        isDirectory = false;
        isSymbolicLink = false;
        IntPtr statBuffer = Marshal.AllocHGlobal(256);
        try
        {
            int mode;
            UnixFileIdentity unixIdentity;
            if (OperatingSystem.IsLinux())
            {
                try
                {
                    if (UnixStatX(
                            parentDescriptor,
                            name,
                            LinuxAtSymlinkNoFollow,
                            LinuxStatxBasicStats,
                            statBuffer) != 0)
                    {
                        return false;
                    }
                }
                catch (EntryPointNotFoundException)
                {
                    return false;
                }

                mode = Marshal.ReadInt16(statBuffer, LinuxStatxModeOffset);
                unixIdentity = new UnixFileIdentity(
                    ((long)(uint)Marshal.ReadInt32(statBuffer, LinuxStatxDeviceMajorOffset) << 32) |
                    (uint)Marshal.ReadInt32(statBuffer, LinuxStatxDeviceMinorOffset),
                    Marshal.ReadInt64(statBuffer, LinuxStatxInodeOffset));
            }
            else if (OperatingSystem.IsMacOS())
            {
                if (UnixFStatAt(parentDescriptor, name, statBuffer, MacAtSymlinkNoFollow) != 0)
                {
                    return false;
                }

                mode = Marshal.ReadInt16(statBuffer, 4);
                unixIdentity = new UnixFileIdentity(
                    Marshal.ReadInt32(statBuffer, 0),
                    Marshal.ReadInt64(statBuffer, 8));
            }
            else
            {
                return false;
            }

            identity = ToFileSystemIdentity(unixIdentity);
            isDirectory = (mode & UnixFileTypeMask) == UnixDirectory;
            isSymbolicLink = (mode & UnixFileTypeMask) == 0xA000;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(statBuffer);
        }
    }

    internal static bool IsSymbolicLinkAt(int parentDescriptor, string name) =>
        UnixReadLinkAt(parentDescriptor, name) >= 0;

    internal static void CloseDescriptor(int descriptor)
    {
        if (descriptor >= 0)
        {
            _ = UnixClose(descriptor);
        }
    }

    internal static IntPtr OpenDirectoryStream(int descriptor) => UnixFdOpenDirectory(descriptor);

    internal static DirectoryEntryReadResult ReadDirectoryEntry(
        string relativeDirectory,
        IntPtr directoryStream,
        int readIndex)
    {
        Marshal.SetLastPInvokeError(0);
        IntPtr entry = UnixReadDirectory(directoryStream);
        var nativeResult = new DirectoryEntryReadResult(
            entry,
            entry == IntPtr.Zero ? Marshal.GetLastPInvokeError() : 0);
        return DirectoryEntryReadHook.Value?.Invoke(relativeDirectory, readIndex, nativeResult) ?? nativeResult;
    }

    internal static IDisposable PushDirectoryEntryReadHook(
        Func<string, int, DirectoryEntryReadResult, DirectoryEntryReadResult?> hook)
    {
        Func<string, int, DirectoryEntryReadResult, DirectoryEntryReadResult?>? previous = DirectoryEntryReadHook.Value;
        DirectoryEntryReadHook.Value = hook;
        return new DirectoryEntryReadHookScope(previous);
    }

    internal static string? ReadDirectoryEntryName(IntPtr entry) =>
        ReadDirectoryEntryName(entry, out _);

    internal static string? ReadDirectoryEntryName(IntPtr entry, out bool unrepresentable)
    {
        unrepresentable = false;
        if (entry == IntPtr.Zero)
        {
            return null;
        }

        int nameOffset = OperatingSystem.IsMacOS() ? 21 : 19;
        var bytes = new List<byte>(256);
        for (int offset = 0; offset < 4_096; offset++)
        {
            byte value = Marshal.ReadByte(entry, nameOffset + offset);
            if (value == 0)
            {
                try
                {
                    return StrictUtf8.GetString(bytes.ToArray());
                }
                catch (DecoderFallbackException)
                {
                    // A native filename that cannot be represented as a .NET string
                    // is an incomplete scan. Returning null makes the walker fail
                    // closed before the decoded value can authorize another entry.
                    unrepresentable = true;
                    return null;
                }
            }

            bytes.Add(value);
        }

        return null;
    }

    internal static bool TryGetDirectoryEntryType(
        IntPtr entry,
        out bool isDirectory,
        out bool isSymbolicLink)
    {
        isDirectory = false;
        isSymbolicLink = false;
        if (entry == IntPtr.Zero)
        {
            return false;
        }

        int typeOffset = OperatingSystem.IsMacOS()
            ? MacDirectoryEntryTypeOffset
            : UnixDirectoryEntryTypeOffset;
        byte type = Marshal.ReadByte(entry, typeOffset);
        if (type == UnixDirectoryEntryUnknown)
        {
            return false;
        }

        isDirectory = type == UnixDirectoryEntryDirectory;
        isSymbolicLink = type == UnixDirectoryEntrySymbolicLink;
        return true;
    }

    internal static void CloseDirectoryStream(IntPtr directoryStream)
    {
        _ = UnixCloseDirectory(directoryStream);
    }

    private sealed class DirectoryEntryReadHookScope(
        Func<string, int, DirectoryEntryReadResult, DirectoryEntryReadResult?>? previous) : IDisposable
    {
        public void Dispose() => DirectoryEntryReadHook.Value = previous;
    }

    public void Dispose()
    {
        unixRootHandle?.Dispose();
        windowsRootHandle?.Dispose();
    }

    internal static PathContainmentResult TryIsWithin(string parent, string candidate)
        => TryIsWithinCore(parent, candidate, chargePathWork: true);

    internal static bool TryIsWithinUncharged(string parent, string candidate)
        => TryIsWithinCore(parent, candidate, chargePathWork: false) == PathContainmentResult.Within;

    private static PathContainmentResult TryIsWithinCore(string parent, string candidate, bool chargePathWork)
    {
        string fullParent;
        string current;
        try
        {
            fullParent = Path.GetFullPath(parent);
            current = Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return PathContainmentResult.Outside;
        }

        string logicalPath;
        try
        {
            logicalPath = Path.GetRelativePath(fullParent, current);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return PathContainmentResult.Outside;
        }

        if (chargePathWork && !FilesystemTraversalBudgetContext.TryConsumePathWork(logicalPath))
        {
            return PathContainmentResult.BudgetExhausted;
        }

        if (!TryGetPathIdentityCore(fullParent, out FileSystemIdentity parentIdentity))
        {
            return PathContainmentResult.Outside;
        }

        while (true)
        {
            if (chargePathWork && !FilesystemTraversalBudgetContext.TryConsumePathValidation())
            {
                return PathContainmentResult.BudgetExhausted;
            }

            if (TryGetPathIdentityCore(current, out FileSystemIdentity currentIdentity) &&
                currentIdentity == parentIdentity)
            {
                return PathContainmentResult.Within;
            }

            string? parentDirectory;
            try
            {
                parentDirectory = Directory.GetParent(current)?.FullName;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                return PathContainmentResult.Outside;
            }

            if (string.IsNullOrEmpty(parentDirectory) ||
                string.Equals(parentDirectory, current, StringComparison.Ordinal))
            {
                return PathContainmentResult.Outside;
            }

            current = parentDirectory;
        }
    }

    internal static bool TryGetPathIdentity(string path, out FileSystemIdentity identity)
    {
        identity = default;
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }

        return FilesystemTraversalBudgetContext.TryConsumePathWork(".") &&
            TryGetPathIdentityCore(fullPath, out identity);
    }

    internal static bool TryGetPathIdentityWithoutBudget(string path, out FileSystemIdentity identity)
    {
        identity = default;
        try
        {
            return TryGetPathIdentityCore(Path.GetFullPath(path), out identity);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryGetPathIdentityCore(string fullPath, out FileSystemIdentity identity)
    {
        identity = default;

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            int descriptor = UnixOpen(fullPath, GetFileFlags());
            if (descriptor < 0)
            {
                return false;
            }

            try
            {
                if (!TryGetUnixIdentity(descriptor, out UnixFileIdentity unixIdentity))
                {
                    return false;
                }

                identity = ToFileSystemIdentity(unixIdentity);
                return true;
            }
            finally
            {
                _ = UnixClose(descriptor);
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        SafeFileHandle handle = CreateWindowsHandle(fullPath, FileFlagBackupSemantics | FileFlagOpenReparsePoint);
        try
        {
            return !handle.IsInvalid && TryGetWindowsIdentity(handle, out identity);
        }
        finally
        {
            handle.Dispose();
        }
    }

    private static FileSystemIdentity ToFileSystemIdentity(UnixFileIdentity identity) =>
        new(unchecked((ulong)identity.Device), unchecked((ulong)identity.Inode));

    private string CombineRelative(string relativePath) =>
        string.IsNullOrEmpty(relativePath) ? RootPath : Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string NormalizeRelativeDirectoryKey(string relativePath) =>
        relativePath.Replace('\\', '/').Trim('/');

    private static string[] SplitRelativePath(string relativePath) =>
        relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

    private static bool TryReadStrictUtf8(IntPtr nativeString, out string value)
    {
        var bytes = new List<byte>(256);
        for (int offset = 0; offset < 32 * 1024; offset++)
        {
            byte current = Marshal.ReadByte(nativeString, offset);
            if (current == 0)
            {
                try
                {
                    value = StrictUtf8.GetString(bytes.ToArray());
                    return true;
                }
                catch (DecoderFallbackException)
                {
                    value = string.Empty;
                    return false;
                }
            }

            bytes.Add(current);
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetUnixCanonicalPath(string path, out string canonicalPath)
    {
        canonicalPath = string.Empty;
        const int maximumNativePathBytes = 32 * 1024;
        IntPtr resolvedPath = Marshal.AllocHGlobal(maximumNativePathBytes);
        try
        {
            IntPtr nativePath = UnixRealPath(path, resolvedPath);
            return nativePath != IntPtr.Zero && TryReadStrictUtf8(nativePath, out canonicalPath);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(resolvedPath);
        }
    }

    private bool TryOpenUnixRelative(string relativePath, int flags, out int descriptor)
    {
        descriptor = -1;
        if (unixRootHandle is null)
        {
            return false;
        }

        var opened = new List<SafeFileHandle>();
        try
        {
            string[] components = SplitRelativePath(relativePath);
            SafeFileHandle current = new SafeFileHandle((IntPtr)UnixOpenAt(unixRootHandle.DangerousGetHandle().ToInt32(), ".", GetDirectoryFlags()), ownsHandle: true);
            if (current.IsInvalid)
            {
                current.Dispose();
                return false;
            }

            opened.Add(current);
            for (int index = 0; index < components.Length; index++)
            {
                int next = UnixOpenAt(current.DangerousGetHandle().ToInt32(), components[index], flags);
                if (next < 0)
                {
                    return false;
                }

                current = new SafeFileHandle((IntPtr)next, ownsHandle: true);
                opened.Add(current);
            }

            descriptor = current.DangerousGetHandle().ToInt32();
            current.SetHandleAsInvalid();
            return true;
        }
        finally
        {
            foreach (SafeFileHandle handle in opened)
            {
                handle.Dispose();
            }
        }
    }

    private bool TryOpenUnixParent(string[] components, out SafeFileHandle[] handles)
    {
        handles = [];
        if (unixRootHandle is null || components.Length == 0)
        {
            return false;
        }

        var opened = new List<SafeFileHandle>();
        try
        {
            int rootDescriptor = UnixOpenAt(unixRootHandle.DangerousGetHandle().ToInt32(), ".", GetDirectoryFlags());
            if (rootDescriptor < 0)
            {
                return false;
            }

            opened.Add(new SafeFileHandle((IntPtr)rootDescriptor, ownsHandle: true));
            for (int index = 0; index < components.Length - 1; index++)
            {
                int descriptor = UnixOpenAt(opened[^1].DangerousGetHandle().ToInt32(), components[index], GetDirectoryFlags());
                if (descriptor < 0)
                {
                    return false;
                }

                opened.Add(new SafeFileHandle((IntPtr)descriptor, ownsHandle: true));
            }

            handles = [.. opened];
            opened = [];
            return true;
        }
        finally
        {
            foreach (SafeFileHandle handle in opened)
            {
                handle.Dispose();
            }
        }
    }

    private static bool IsUnixDirectory(UnixFileIdentity _, int descriptor) =>
        TryGetUnixMode(descriptor, out int mode) && (mode & UnixFileTypeMask) == UnixDirectory;

    private static bool IsUnixRegular(UnixFileIdentity _, int descriptor) =>
        TryGetUnixMode(descriptor, out int mode) && (mode & UnixFileTypeMask) == UnixRegularFile;

    private static bool TryGetUnixMode(int descriptor, out int mode)
    {
        mode = 0;
        if (OperatingSystem.IsLinux())
        {
            IntPtr buffer = Marshal.AllocHGlobal(256);
            try
            {
                if (UnixStatX(descriptor, string.Empty, LinuxAtEmptyPath, LinuxStatxBasicStats, buffer) != 0)
                {
                    return false;
                }

                mode = Marshal.ReadInt16(buffer, LinuxStatxModeOffset);
                return true;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        IntPtr statBuffer = Marshal.AllocHGlobal(256);
        try
        {
            if (UnixFStat(descriptor, statBuffer) != 0)
            {
                return false;
            }

            mode = Marshal.ReadInt16(statBuffer, 4);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(statBuffer);
        }
    }

    internal static bool TryGetUnixIdentity(int descriptor, out UnixFileIdentity identity)
    {
        identity = default;
        if (OperatingSystem.IsLinux())
        {
            IntPtr buffer = Marshal.AllocHGlobal(256);
            try
            {
                if (UnixStatX(descriptor, string.Empty, LinuxAtEmptyPath, LinuxStatxBasicStats, buffer) != 0)
                {
                    return false;
                }

                identity = new UnixFileIdentity(
                    ((long)(uint)Marshal.ReadInt32(buffer, LinuxStatxDeviceMajorOffset) << 32) |
                    (uint)Marshal.ReadInt32(buffer, LinuxStatxDeviceMinorOffset),
                    Marshal.ReadInt64(buffer, LinuxStatxInodeOffset));
                return true;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        IntPtr statBuffer = Marshal.AllocHGlobal(256);
        try
        {
            if (UnixFStat(descriptor, statBuffer) != 0)
            {
                return false;
            }

            identity = new UnixFileIdentity(Marshal.ReadInt32(statBuffer, 0), Marshal.ReadInt64(statBuffer, 8));
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(statBuffer);
        }
    }

    internal static bool TryGetDescriptorIdentity(int descriptor, out FileSystemIdentity identity)
    {
        identity = default;
        if (!TryGetUnixIdentity(descriptor, out UnixFileIdentity unixIdentity))
        {
            return false;
        }

        identity = ToFileSystemIdentity(unixIdentity);
        return true;
    }

    private static int GetDirectoryFlags()
    {
        if (OperatingSystem.IsMacOS())
        {
            return MacCloseOnExec | MacDirectory | MacNoFollow | UnixReadOnly;
        }

        return LinuxCloseOnExec | GetLinuxDirectoryFlags() | UnixReadOnly;
    }

    private static int GetFileFlags() =>
        OperatingSystem.IsMacOS()
            ? MacCloseOnExec | MacNoFollow | MacNonBlocking | UnixReadOnly
            : LinuxCloseOnExec | GetLinuxNoFollowFlag() | LinuxNonBlocking | UnixReadOnly;

    private static int GetLinuxDirectoryFlags() =>
        RuntimeInformation.OSArchitecture is Architecture.Arm or Architecture.Arm64
            ? LinuxArmDirectory | LinuxArmNoFollow
            : LinuxGenericDirectory | LinuxGenericNoFollow;

    private static int GetLinuxNoFollowFlag() =>
        RuntimeInformation.OSArchitecture is Architecture.Arm or Architecture.Arm64
            ? LinuxArmNoFollow
            : LinuxGenericNoFollow;

    private static SafeFileHandle CreateWindowsHandle(string path, uint flags) =>
        CreateFile(
            PathUtilities.ToWindowsHandlePath(path),
            GenericRead,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            flags,
            IntPtr.Zero);

    private static bool TryGetWindowsFileAttributes(SafeFileHandle handle, out FileAttributes attributes)
    {
        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
        {
            attributes = default;
            return false;
        }

        attributes = (FileAttributes)information.FileAttributes;
        return true;
    }

    private static bool TryGetWindowsIdentity(SafeFileHandle handle, out FileSystemIdentity identity)
    {
        identity = default;
        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information) ||
            (information.FileIndexHigh == 0 && information.FileIndexLow == 0))
        {
            return false;
        }

        identity = new FileSystemIdentity(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal FileTime CreationTime;
        internal FileTime LastAccessTime;
        internal FileTime LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        internal uint LowDateTime;
        internal uint HighDateTime;
    }

    [DllImport("libc", EntryPoint = "open", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int UnixOpen([MarshalAs(UnmanagedType.LPStr)] string path, int flags);

    [DllImport("libc", EntryPoint = "realpath", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr UnixRealPath(
        [MarshalAs(UnmanagedType.LPStr)] string path,
        IntPtr resolvedPath);

    [DllImport("libc", EntryPoint = "openat", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int UnixOpenAt(int directoryFileDescriptor, [MarshalAs(UnmanagedType.LPStr)] string path, int flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int UnixClose(int fileDescriptor);

    [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static extern int UnixDup(int fileDescriptor);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int UnixFStat(int fileDescriptor, IntPtr buffer);

    [DllImport("libc", EntryPoint = "statx", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int UnixStatX(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPStr)] string path,
        int flags,
        uint mask,
        IntPtr buffer);

    [DllImport("libc", EntryPoint = "fstatat", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int UnixFStatAt(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPStr)] string path,
        IntPtr buffer,
        int flags);

    [DllImport("libc", EntryPoint = "readlinkat", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern nint UnixReadLinkAtNative(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPStr)] string path,
        byte[] buffer,
        nint bufferSize);

    private static int UnixReadLinkAt(int directoryFileDescriptor, string path)
    {
        byte[] buffer = new byte[4_096];
        return UnixReadLinkAtNative(directoryFileDescriptor, path, buffer, buffer.Length) >= 0 ? 0 : -1;
    }

    [DllImport("libc", EntryPoint = "fdopendir", SetLastError = true)]
    private static extern IntPtr UnixFdOpenDirectory(int fileDescriptor);

    [DllImport("libc", EntryPoint = "readdir", SetLastError = true)]
    private static extern IntPtr UnixReadDirectory(IntPtr directoryStream);

    [DllImport("libc", EntryPoint = "closedir", SetLastError = true)]
    private static extern int UnixCloseDirectory(IntPtr directoryStream);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        [MarshalAs(UnmanagedType.LPWStr)] string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle fileHandle,
        out ByHandleFileInformation information);
}

internal static class SafePathBoundaryContext
{
    private static readonly AsyncLocal<SafePathBoundary?> CurrentBoundary = new();

    internal static SafePathBoundary? Current => CurrentBoundary.Value;

    internal static IDisposable Push(SafePathBoundary boundary)
    {
        SafePathBoundary? previous = CurrentBoundary.Value;
        CurrentBoundary.Value = boundary;
        return new BoundaryScope(previous);
    }

    private sealed class BoundaryScope(SafePathBoundary? previous) : IDisposable
    {
        public void Dispose() => CurrentBoundary.Value = previous;
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
        return TryReadBytesCore(
            repositoryRoot,
            path,
            maximumBytes,
            remainingTotalBytes,
            expectedIdentity: null,
            out bytes,
            out bytesRead,
            afterInitialLengthRead);
    }

    internal static SafeFileReadStatus TryReadBytes(
        string repositoryRoot,
        SafeFileEntry file,
        long maximumBytes,
        long? remainingTotalBytes,
        out byte[] bytes,
        out long bytesRead,
        Action? afterInitialLengthRead = null)
    {
        return TryReadBytesCore(
            repositoryRoot,
            file.FullPath,
            maximumBytes,
            remainingTotalBytes,
            file.ExpectedIdentity,
            out bytes,
            out bytesRead,
            afterInitialLengthRead);
    }

    private static SafeFileReadStatus TryReadBytesCore(
        string repositoryRoot,
        string path,
        long maximumBytes,
        long? remainingTotalBytes,
        FileSystemIdentity? expectedIdentity,
        out byte[] bytes,
        out long bytesRead,
        Action? afterInitialLengthRead)
    {
        bytes = [];
        bytesRead = 0;
        if (maximumBytes < 0 || remainingTotalBytes is < 0)
        {
            return SafeFileReadStatus.Failed;
        }

        if (!TryOpenRegularFile(
                repositoryRoot,
                path,
                expectedIdentity,
                out FileStream? stream,
                out SafeFileReadStatus openStatus))
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
        FileSystemIdentity? expectedIdentity,
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
            if (PathUtilities.IsWithin(fullRoot, fullPath) != PathContainmentResult.Within)
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

        SafePathBoundary? boundary = SafePathBoundaryContext.Current;
        if (boundary is not null)
        {
            if (!boundary.Matches(fullRoot))
            {
                status = SafeFileReadStatus.Unsafe;
                return false;
            }

            return boundary.TryOpenRegularFile(relativePath, expectedIdentity, out stream, out status);
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
                PathUtilities.ToWindowsHandlePath(fullPath),
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                FileOptions.SequentialScan);
            stream = new FileStream(handle!, FileAccess.Read, ReadBufferSize, isAsync: false);
            if (!TryValidateRegularFilePath(fullRoot, fullPath, relativePath, out exists) ||
                !exists ||
                !WindowsPathResolver.TryGetFinalPath(handle!, out string resolvedPath) ||
                PathUtilities.IsWithin(fullRoot, resolvedPath) != PathContainmentResult.Within)
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

}

internal sealed record SafeFileEntry(
    string FullPath,
    string RelativePath,
    FileSystemIdentity? ExpectedIdentity = null);

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

[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "Directory handles are closed on every success and failure path; descriptor ownership is transferred to the traversal stack where required.")]
internal static class SafeFileWalker
{
    internal static Action<string>? BeforeEnumerationForTesting { get; set; }
    internal static Action<string>? AfterEnumerationOpenedForTesting { get; set; }

    private sealed record UnixPendingDirectory(
        int Descriptor,
        string RelativePath,
        FileSystemIdentity ExpectedIdentity);

    private sealed record WindowsPendingDirectory(
        DirectoryInfo Directory,
        FileSystemIdentity ExpectedIdentity);

    internal static WalkResult Walk(
        string repositoryRoot,
        string root,
        bool failOnAccessErrors,
        Func<string, GlobMatchStatus>? shouldPruneDirectory = null)
    {
        SafePathBoundary? ownedBoundary = null;
        IDisposable? boundaryScope = null;
        SafePathBoundary? boundary = SafePathBoundaryContext.Current;
        FilesystemTraversalBudget budget =
            FilesystemTraversalBudgetContext.Current ?? new FilesystemTraversalBudget();
        if (boundary is not null && !boundary.Matches(repositoryRoot))
        {
            // A scan owns one trusted boundary for its entire lifetime. A changed
            // repository path is a substitution, not permission to rebind trust to
            // whatever now occupies that pathname.
            return new WalkResult([], [], new ScanError(
                budget.IsExhausted
                    ? FixtureVaultContract.FilesystemTraversalErrorCode
                    : "FV-E002",
                budget.IsExhausted
                    ? FixtureVaultContract.FilesystemTraversalErrorMessage
                    : "The trusted repository boundary changed during the scan."));
        }

        if (boundary is null)
        {
            if (budget.IsExhausted)
            {
                return new WalkResult([], [], new ScanError(
                    FixtureVaultContract.FilesystemTraversalErrorCode,
                    FixtureVaultContract.FilesystemTraversalErrorMessage));
            }

            if (!SafePathBoundary.TryCreate(repositoryRoot, out ownedBoundary) || ownedBoundary is null)
            {
                return new WalkResult([], [], new ScanError(
                    budget.IsExhausted
                        ? FixtureVaultContract.FilesystemTraversalErrorCode
                        : "FV-E002",
                    budget.IsExhausted
                        ? FixtureVaultContract.FilesystemTraversalErrorMessage
                        : "A configured fixture root could not be inspected completely."));
            }

            boundary = ownedBoundary;
            boundaryScope = SafePathBoundaryContext.Push(boundary);
        }

        try
        {
            string relativeRoot = PathUtilities.NormalizeRelative(repositoryRoot, root);
            if (relativeRoot == ".")
            {
                relativeRoot = string.Empty;
            }

            return OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()
                ? WalkUnix(repositoryRoot, relativeRoot, failOnAccessErrors, shouldPruneDirectory, boundary, budget)
                : WalkPath(repositoryRoot, root, relativeRoot, failOnAccessErrors, shouldPruneDirectory, boundary, budget);
        }
        finally
        {
            boundaryScope?.Dispose();
            ownedBoundary?.Dispose();
        }
    }

    private static WalkResult WalkUnix(
        string repositoryRoot,
        string relativeRoot,
        bool failOnAccessErrors,
        Func<string, GlobMatchStatus>? shouldPruneDirectory,
        SafePathBoundary boundary,
        FilesystemTraversalBudget budget)
    {
        var files = new List<SafeFileEntry>();
        var reparsePaths = new List<string>();
        GlobMatchStatus startingStatus = shouldPruneDirectory?.Invoke(relativeRoot) ?? GlobMatchStatus.NoMatch;
        if (startingStatus == GlobMatchStatus.Failure)
        {
            return new WalkResult(files, reparsePaths, new ScanError(
                FixtureVaultContract.IgnoredPathMatchingErrorCode,
                "Ignored path matching could not be completed safely."));
        }

        if (startingStatus == GlobMatchStatus.Match)
        {
            return new WalkResult(files, reparsePaths, null);
        }

        if (!boundary.TryDuplicateDirectory(
                relativeRoot,
                expectedIdentity: null,
                out int startingDescriptor,
                out FileSystemIdentity startingIdentity))
        {
            return new WalkResult(files, reparsePaths, new ScanError(
                budget.IsExhausted
                    ? FixtureVaultContract.FilesystemTraversalErrorCode
                    : "FV-E002",
                budget.IsExhausted
                    ? FixtureVaultContract.FilesystemTraversalErrorMessage
                    : "A configured fixture root could not be inspected completely."));
        }

        var pending = new Stack<UnixPendingDirectory>();
        pending.Push(new UnixPendingDirectory(
            startingDescriptor,
            relativeRoot,
            startingIdentity));

        try
        {
            while (pending.Count > 0)
            {
                UnixPendingDirectory directory = pending.Pop();
                if (!SafePathBoundary.TryGetDescriptorIdentity(directory.Descriptor, out FileSystemIdentity descriptorIdentity) ||
                    descriptorIdentity != directory.ExpectedIdentity)
                {
                    SafePathBoundary.CloseDescriptor(directory.Descriptor);
                    return new WalkResult(files, reparsePaths, new ScanError(
                        budget.IsExhausted
                            ? FixtureVaultContract.FilesystemTraversalErrorCode
                            : "FV-E002",
                        budget.IsExhausted
                            ? FixtureVaultContract.FilesystemTraversalErrorMessage
                            : "A configured fixture root could not be inspected completely."));
                }

                IntPtr directoryStream = SafePathBoundary.OpenDirectoryStream(directory.Descriptor);
                if (directoryStream == IntPtr.Zero)
                {
                    SafePathBoundary.CloseDescriptor(directory.Descriptor);
                    return new WalkResult(files, reparsePaths, new ScanError(
                        "FV-E002",
                        "A configured fixture root could not be inspected completely."));
                }

                try
                {
                    BeforeEnumerationForTesting?.Invoke(directory.RelativePath);
                    // The directory stream is bound to the descriptor, but the pathname is
                    // still part of the authorization contract. Validate it after the test seam
                    // (which models a replacement during enumeration) and before consuming any
                    // entries. Run the second seam even when validation fails so the deterministic
                    // replacement-and-restoration schedule is fully exercised before failing closed.
                    bool trustedPathAfterEnumerationHook = IsTrustedCurrentDirectory(
                        boundary,
                        directory.RelativePath,
                        directory.ExpectedIdentity);
                    AfterEnumerationOpenedForTesting?.Invoke(directory.RelativePath);
                    if (!trustedPathAfterEnumerationHook)
                    {
                        return new WalkResult(files, reparsePaths, new ScanError(
                            budget.IsExhausted
                                ? FixtureVaultContract.FilesystemTraversalErrorCode
                                : "FV-E002",
                            budget.IsExhausted
                                ? FixtureVaultContract.FilesystemTraversalErrorMessage
                                : "A configured fixture root could not be inspected completely."));
                    }

                    int readIndex = 0;
                    while (true)
                    {
                        DirectoryEntryReadResult readResult = SafePathBoundary.ReadDirectoryEntry(
                            directory.RelativePath,
                            directoryStream,
                            readIndex++);
                        if (readResult.Entry == IntPtr.Zero)
                        {
                            if (readResult.ErrorNumber != 0)
                            {
                                return new WalkResult(files, reparsePaths, new ScanError(
                                    "FV-E002",
                                    "A configured fixture root could not be inspected completely."));
                            }

                            break;
                        }

                        string? name = SafePathBoundary.ReadDirectoryEntryName(
                            readResult.Entry,
                            out bool unrepresentableName);
                        if (name is null)
                        {
                            return new WalkResult(files, reparsePaths, new ScanError(
                                unrepresentableName
                                    ? FixtureVaultContract.UnrepresentableNativeNameErrorCode
                                    : "FV-E002",
                                "A configured fixture root could not be inspected completely."));
                        }

                        if (name is "." or "..")
                        {
                            continue;
                        }

                        if (!IsTrustedCurrentDirectory(boundary, directory.RelativePath, directory.ExpectedIdentity))
                        {
                            return new WalkResult(files, reparsePaths, new ScanError(
                                budget.IsExhausted
                                    ? FixtureVaultContract.FilesystemTraversalErrorCode
                                    : "FV-E002",
                                budget.IsExhausted
                                    ? FixtureVaultContract.FilesystemTraversalErrorMessage
                                    : "A configured fixture root could not be inspected completely."));
                        }

                        if (!budget.TryConsumeEntry())
                        {
                            return new WalkResult(files, reparsePaths, new ScanError(
                                FixtureVaultContract.FilesystemTraversalErrorCode,
                                FixtureVaultContract.FilesystemTraversalErrorMessage));
                        }

                        if (!FilesystemTraversalBudgetContext.TryConsumeDirectoryEntryWork())
                        {
                            return new WalkResult(files, reparsePaths, new ScanError(
                                FixtureVaultContract.FilesystemTraversalErrorCode,
                                FixtureVaultContract.FilesystemTraversalErrorMessage));
                        }

                        string relativePath = string.IsNullOrEmpty(directory.RelativePath)
                            ? name
                            : directory.RelativePath + "/" + name;
                        if (!FilesystemTraversalBudgetContext.TryConsumePathWork(relativePath))
                        {
                            return new WalkResult(files, reparsePaths, new ScanError(
                                FixtureVaultContract.FilesystemTraversalErrorCode,
                                FixtureVaultContract.FilesystemTraversalErrorMessage));
                        }

                        bool entryTypeKnown = SafePathBoundary.TryGetDirectoryEntryType(
                            readResult.Entry,
                            out bool directoryHint,
                            out bool symbolicLinkHint);
                        bool directoryStatusKnown = false;
                        GlobMatchStatus directoryStatus = GlobMatchStatus.NoMatch;
                        if (entryTypeKnown && symbolicLinkHint)
                        {
                            if (SafePathBoundary.IsSymbolicLinkAt(directory.Descriptor, name))
                            {
                                reparsePaths.Add(relativePath);
                                continue;
                            }

                            entryTypeKnown = false;
                        }

                        if (entryTypeKnown && directoryHint)
                        {
                            directoryStatusKnown = true;
                            directoryStatus = shouldPruneDirectory?.Invoke(relativePath) ?? GlobMatchStatus.NoMatch;
                            if (directoryStatus == GlobMatchStatus.Failure)
                            {
                                return new WalkResult(
                                    files,
                                    reparsePaths,
                                    new ScanError(FixtureVaultContract.IgnoredPathMatchingErrorCode, "Ignored path matching could not be completed safely."));
                            }

                            if (directoryStatus == GlobMatchStatus.Match)
                            {
                                continue;
                            }
                        }

                        if (!SafePathBoundary.TryGetEntryMetadataAt(
                                directory.Descriptor,
                                name,
                                out FileSystemIdentity identity,
                                out bool isDirectory,
                                out bool isSymbolicLink))
                        {
                            return new WalkResult(files, reparsePaths, new ScanError(
                                budget.IsExhausted
                                    ? FixtureVaultContract.FilesystemTraversalErrorCode
                                    : "FV-E002",
                                budget.IsExhausted
                                    ? FixtureVaultContract.FilesystemTraversalErrorMessage
                                : "A configured fixture root could not be inspected completely."));
                        }

                        if (directoryStatusKnown && !isDirectory)
                        {
                            return new WalkResult(files, reparsePaths, new ScanError(
                                budget.IsExhausted
                                    ? FixtureVaultContract.FilesystemTraversalErrorCode
                                    : "FV-E002",
                                budget.IsExhausted
                                    ? FixtureVaultContract.FilesystemTraversalErrorMessage
                                    : "A configured fixture root could not be inspected completely."));
                        }

                        if (isSymbolicLink)
                        {
                            if (!SafePathBoundary.IsSymbolicLinkAt(directory.Descriptor, name))
                            {
                                return new WalkResult(files, reparsePaths, new ScanError(
                                    budget.IsExhausted
                                        ? FixtureVaultContract.FilesystemTraversalErrorCode
                                        : "FV-E002",
                                    budget.IsExhausted
                                        ? FixtureVaultContract.FilesystemTraversalErrorMessage
                                        : "A configured fixture root could not be inspected completely."));
                            }

                            reparsePaths.Add(relativePath);
                            continue;
                        }

                        if (isDirectory)
                        {
                            if (!directoryStatusKnown)
                            {
                                directoryStatus = shouldPruneDirectory?.Invoke(relativePath) ?? GlobMatchStatus.NoMatch;
                                if (directoryStatus == GlobMatchStatus.Failure)
                                {
                                    return new WalkResult(
                                        files,
                                        reparsePaths,
                                        new ScanError(FixtureVaultContract.IgnoredPathMatchingErrorCode, "Ignored path matching could not be completed safely."));
                                }

                                if (directoryStatus == GlobMatchStatus.Match)
                                {
                                    continue;
                                }
                            }

                            if (!SafePathBoundary.TryOpenEntryAt(
                                    directory.Descriptor,
                                    name,
                                    out int childDescriptor,
                                    out FileSystemIdentity openedIdentity,
                                    out bool openedIsDirectory) ||
                                !openedIsDirectory ||
                                openedIdentity != identity)
                            {
                                if (childDescriptor >= 0)
                                {
                                    SafePathBoundary.CloseDescriptor(childDescriptor);
                                }

                                return new WalkResult(files, reparsePaths, new ScanError(
                                    budget.IsExhausted
                                        ? FixtureVaultContract.FilesystemTraversalErrorCode
                                        : "FV-E002",
                                    budget.IsExhausted
                                        ? FixtureVaultContract.FilesystemTraversalErrorMessage
                                        : "A configured fixture root could not be inspected completely."));
                            }

                            // A callback can replace the queued directory or one of its
                            // ancestors after the child descriptor was opened. The descriptor is
                            // safe to hold, but accepting the changed pathname would make the
                            // scan report a state different from the authorized tree. Require the
                            // current pathname to resolve through the same trusted root before
                            // queueing the child.
                            if (!IsTrustedCurrentDirectory(boundary, relativePath, identity))
                            {
                                SafePathBoundary.CloseDescriptor(childDescriptor);
                                return new WalkResult(files, reparsePaths, new ScanError(
                                    budget.IsExhausted
                                        ? FixtureVaultContract.FilesystemTraversalErrorCode
                                        : "FV-E002",
                                    budget.IsExhausted
                                        ? FixtureVaultContract.FilesystemTraversalErrorMessage
                                    : "A configured fixture root could not be inspected completely."));
                            }

                            pending.Push(new UnixPendingDirectory(
                                childDescriptor,
                                relativePath,
                                identity));

                            continue;
                        }

                        if (!SafePathBoundary.TryOpenEntryAt(
                                directory.Descriptor,
                                name,
                                out int fileDescriptor,
                                out FileSystemIdentity openedFileIdentity,
                                out bool openedFileIsDirectory) ||
                            openedFileIsDirectory ||
                            openedFileIdentity != identity)
                        {
                            if (fileDescriptor >= 0)
                            {
                                SafePathBoundary.CloseDescriptor(fileDescriptor);
                            }

                            return new WalkResult(files, reparsePaths, new ScanError(
                                budget.IsExhausted
                                    ? FixtureVaultContract.FilesystemTraversalErrorCode
                                    : "FV-E002",
                                budget.IsExhausted
                                    ? FixtureVaultContract.FilesystemTraversalErrorMessage
                                    : "A configured fixture root could not be inspected completely."));
                        }

                        files.Add(new SafeFileEntry(
                            Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)),
                            relativePath,
                            identity));
                        SafePathBoundary.CloseDescriptor(fileDescriptor);
                    }
                }
                finally
                {
                    SafePathBoundary.CloseDirectoryStream(directoryStream);
                }
            }
        }
        finally
        {
            while (pending.Count > 0)
            {
                SafePathBoundary.CloseDescriptor(pending.Pop().Descriptor);
            }
        }

        return new WalkResult(files, reparsePaths, null);
    }

    private static WalkResult WalkPath(
        string repositoryRoot,
        string root,
        string relativeRoot,
        bool failOnAccessErrors,
        Func<string, GlobMatchStatus>? shouldPruneDirectory,
        SafePathBoundary boundary,
        FilesystemTraversalBudget budget)
    {
        var files = new List<SafeFileEntry>();
        var reparsePaths = new List<string>();
        var pending = new Stack<WindowsPendingDirectory>();
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

        if (!boundary.TryOpenDirectory(
                relativeRoot,
                expectedIdentity: null,
                out SafeFileHandle? startingHandle,
                out FileSystemIdentity startingIdentity) ||
            startingHandle is null)
        {
            return new WalkResult(files, reparsePaths, new ScanError(
                budget.IsExhausted
                    ? FixtureVaultContract.FilesystemTraversalErrorCode
                    : "FV-E002",
                budget.IsExhausted
                    ? FixtureVaultContract.FilesystemTraversalErrorMessage
                    : "A configured fixture root could not be inspected completely."));
        }

        startingHandle.Dispose();
        pending.Push(new WindowsPendingDirectory(startingDirectory, startingIdentity));

        while (pending.Count > 0)
        {
            WindowsPendingDirectory pendingDirectory = pending.Pop();
            DirectoryInfo directory = pendingDirectory.Directory;
            if (!IsSafeDirectoryForEnumeration(
                    repositoryRoot,
                    directory,
                    boundary,
                    pendingDirectory.ExpectedIdentity))
            {
                return new WalkResult(files, reparsePaths, new ScanError(
                    budget.IsExhausted
                        ? FixtureVaultContract.FilesystemTraversalErrorCode
                        : "FV-E002",
                    budget.IsExhausted
                        ? FixtureVaultContract.FilesystemTraversalErrorMessage
                        : "A configured fixture root could not be inspected completely."));
            }

            try
            {
                BeforeEnumerationForTesting?.Invoke(PathUtilities.NormalizeRelative(repositoryRoot, directory.FullName));
                using IEnumerator<FileSystemInfo> enumerator = directory.EnumerateFileSystemInfos().GetEnumerator();
                bool hasEntry = enumerator.MoveNext();
                AfterEnumerationOpenedForTesting?.Invoke(PathUtilities.NormalizeRelative(repositoryRoot, directory.FullName));
                while (hasEntry)
                {
                    FileSystemInfo entry = enumerator.Current;
                    // The directory and every current ancestor were validated immediately before
                    // enumeration started, and again before each yielded entry. This prevents a
                    // queued directory whose parent was replaced with a link from contributing
                    // entries to the report.
                    if (!IsSafeDirectoryForEnumeration(
                            repositoryRoot,
                            directory,
                            boundary,
                            pendingDirectory.ExpectedIdentity))
                    {
                        return new WalkResult(files, reparsePaths, new ScanError(
                            budget.IsExhausted
                                ? FixtureVaultContract.FilesystemTraversalErrorCode
                                : "FV-E002",
                            budget.IsExhausted
                                ? FixtureVaultContract.FilesystemTraversalErrorMessage
                                : "A configured fixture root could not be inspected completely."));
                    }

                    if (!budget.TryConsumeEntry())
                    {
                        return new WalkResult(files, reparsePaths, new ScanError(
                            FixtureVaultContract.FilesystemTraversalErrorCode,
                            FixtureVaultContract.FilesystemTraversalErrorMessage));
                    }

                    if (!FilesystemTraversalBudgetContext.TryConsumeDirectoryEntryWork())
                    {
                        return new WalkResult(files, reparsePaths, new ScanError(
                            FixtureVaultContract.FilesystemTraversalErrorCode,
                            FixtureVaultContract.FilesystemTraversalErrorMessage));
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
                            hasEntry = enumerator.MoveNext();
                            continue;
                        }

                        return new WalkResult(files, reparsePaths, new ScanError(
                            "FV-E002",
                            "A configured fixture root could not be inspected completely."));
                    }

                    string relativePath = PathUtilities.NormalizeRelative(repositoryRoot, entry.FullName);
                    DirectoryInfo? childDirectory = entry as DirectoryInfo;
                    GlobMatchStatus directoryStatus = GlobMatchStatus.NoMatch;
                    if (childDirectory is not null && (attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        directoryStatus = shouldPruneDirectory?.Invoke(relativePath) ?? GlobMatchStatus.NoMatch;
                        if (directoryStatus == GlobMatchStatus.Failure)
                        {
                            return new WalkResult(
                                files,
                                reparsePaths,
                                new ScanError(FixtureVaultContract.IgnoredPathMatchingErrorCode, "Ignored path matching could not be completed safely."));
                        }

                        if (directoryStatus == GlobMatchStatus.Match)
                        {
                            hasEntry = enumerator.MoveNext();
                            continue;
                        }
                    }

                    if (!FilesystemTraversalBudgetContext.TryConsumePathWork(relativePath))
                    {
                        return new WalkResult(files, reparsePaths, new ScanError(
                            FixtureVaultContract.FilesystemTraversalErrorCode,
                            FixtureVaultContract.FilesystemTraversalErrorMessage));
                    }

                    if (!boundary.TryPathExists(relativePath))
                    {
                        return new WalkResult(files, reparsePaths, new ScanError(
                            budget.IsExhausted
                                ? FixtureVaultContract.FilesystemTraversalErrorCode
                                : "FV-E002",
                            budget.IsExhausted
                                ? FixtureVaultContract.FilesystemTraversalErrorMessage
                                : "A configured fixture root could not be inspected completely."));
                    }

                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        reparsePaths.Add(relativePath);
                        hasEntry = enumerator.MoveNext();
                        continue;
                    }

                    if (childDirectory is not null)
                    {
                        if (!SafePathBoundary.TryGetPathIdentityWithoutBudget(
                                childDirectory.FullName,
                                out FileSystemIdentity childIdentity))
                        {
                            return new WalkResult(files, reparsePaths, new ScanError(
                                budget.IsExhausted
                                    ? FixtureVaultContract.FilesystemTraversalErrorCode
                                    : "FV-E002",
                                budget.IsExhausted
                                    ? FixtureVaultContract.FilesystemTraversalErrorMessage
                                    : "A configured fixture root could not be inspected completely."));
                        }

                        if (!boundary.TryOpenDirectory(
                                relativePath,
                                childIdentity,
                                out SafeFileHandle? childHandle,
                                out FileSystemIdentity openedChildIdentity) ||
                            childHandle is null)
                        {
                            return new WalkResult(files, reparsePaths, new ScanError(
                                budget.IsExhausted
                                    ? FixtureVaultContract.FilesystemTraversalErrorCode
                                    : "FV-E002",
                                budget.IsExhausted
                                    ? FixtureVaultContract.FilesystemTraversalErrorMessage
                                    : "A configured fixture root could not be inspected completely."));
                        }

                        childHandle.Dispose();
                        pending.Push(new WindowsPendingDirectory(childDirectory, openedChildIdentity));
                    }
                    else
                    {
                        if (!IsSafeDirectoryForEnumeration(
                                repositoryRoot,
                                directory,
                                boundary,
                                pendingDirectory.ExpectedIdentity))
                        {
                            return new WalkResult(files, reparsePaths, new ScanError(
                                budget.IsExhausted
                                    ? FixtureVaultContract.FilesystemTraversalErrorCode
                                    : "FV-E002",
                                budget.IsExhausted
                                    ? FixtureVaultContract.FilesystemTraversalErrorMessage
                                    : "A configured fixture root could not be inspected completely."));
                        }

                        if (!SafePathBoundary.TryGetPathIdentityWithoutBudget(entry.FullName, out FileSystemIdentity identity))
                        {
                            return new WalkResult(files, reparsePaths, new ScanError(
                                budget.IsExhausted
                                    ? FixtureVaultContract.FilesystemTraversalErrorCode
                                    : "FV-E002",
                                budget.IsExhausted
                                    ? FixtureVaultContract.FilesystemTraversalErrorMessage
                                    : "A configured fixture root could not be inspected completely."));
                        }

                        files.Add(new SafeFileEntry(entry.FullName, relativePath, identity));
                    }

                    hasEntry = enumerator.MoveNext();
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

    private static bool IsSafeDirectoryForEnumeration(
        string repositoryRoot,
        DirectoryInfo directory,
        SafePathBoundary boundary,
        FileSystemIdentity expectedIdentity)
    {
        SafeFileHandle? handle = null;
        try
        {
            string fullRepositoryRoot = Path.GetFullPath(repositoryRoot);
            string fullDirectoryPath = Path.GetFullPath(directory.FullName);
            if (!PathUtilities.IsWithinUncharged(fullRepositoryRoot, fullDirectoryPath))
            {
                return false;
            }

            string relativePath = Path.GetRelativePath(fullRepositoryRoot, fullDirectoryPath);
            string normalizedRelative = relativePath == "." ? string.Empty : relativePath.Replace('\\', '/');
            return boundary.TryOpenDirectory(
                       normalizedRelative,
                       expectedIdentity,
                       out handle,
                       out _) &&
                   handle is not null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private static bool IsTrustedCurrentDirectory(
        SafePathBoundary boundary,
        string relativePath,
        FileSystemIdentity expectedIdentity)
    {
        SafeFileHandle? handle = null;
        try
        {
            return boundary.TryOpenDirectory(
                       relativePath,
                       expectedIdentity,
                       out handle,
                       out _) &&
                   handle is not null;
        }
        finally
        {
            handle?.Dispose();
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
