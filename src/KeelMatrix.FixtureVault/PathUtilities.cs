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

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }
}

internal readonly record struct UnixFileIdentity(long Device, long Inode);

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
    private const int LinuxAtFileDescriptor = -100;
    private const int LinuxAtSymlinkNoFollow = 0x100;
    private const int LinuxAtEmptyPath = 0x1000;
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
    private readonly string? trustedWindowsRootPath;

    private SafePathBoundary(
        string rootPath,
        SafeFileHandle? unixRootHandle,
        SafeFileHandle? windowsRootHandle,
        string? trustedWindowsRootPath)
    {
        RootPath = rootPath;
        this.unixRootHandle = unixRootHandle;
        this.windowsRootHandle = windowsRootHandle;
        this.trustedWindowsRootPath = trustedWindowsRootPath;
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
            if (descriptor < 0 || !TryGetUnixIdentity(descriptor, out UnixFileIdentity identity) ||
                !IsUnixDirectory(identity, descriptor))
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
                null);
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        SafeFileHandle rootHandle = CreateWindowsHandle(fullRoot, FileFlagBackupSemantics | FileFlagOpenReparsePoint);
        if (rootHandle.IsInvalid || !WindowsPathResolver.TryGetFinalPath(rootHandle, out string trustedRootPath))
        {
            rootHandle.Dispose();
            return false;
        }

        boundary = new SafePathBoundary(fullRoot, null, rootHandle, trustedRootPath);
        return true;
    }

    internal bool Matches(string repositoryRoot) =>
        string.Equals(
            RootPath,
            Path.GetFullPath(repositoryRoot),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal bool TryOpenDirectory(string relativePath, out SafeFileHandle? handle)
    {
        handle = null;
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            if (!TryOpenUnixRelative(relativePath, GetDirectoryFlags(), out int descriptor))
            {
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
        SafeFileHandle candidate = CreateWindowsHandle(path, FileFlagBackupSemantics | FileFlagOpenReparsePoint);
        if (candidate.IsInvalid ||
            !WindowsPathResolver.TryGetFinalPath(candidate, out string resolvedPath) ||
            !IsTrustedWindowsPath(resolvedPath) ||
            !TryGetWindowsFileAttributes(candidate, out FileAttributes attributes) ||
            (attributes & FileAttributes.ReparsePoint) != 0 ||
            (attributes & FileAttributes.Directory) == 0)
        {
            candidate.Dispose();
            return false;
        }

        handle = candidate;
        return true;
    }

    internal bool TryOpenRegularFile(
        string relativePath,
        UnixFileIdentity? expectedIdentity,
        out FileStream? stream,
        out SafeFileReadStatus status)
    {
        stream = null;
        status = SafeFileReadStatus.Unsafe;
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
                    if (!TryGetUnixIdentity(descriptor, out UnixFileIdentity identity) ||
                        !IsUnixRegular(identity, descriptor) ||
                        expectedIdentity is not null && expectedIdentity.Value != identity)
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

        SafeFileHandle candidate = CreateWindowsHandle(CombineRelative(relativePath), FileFlagOpenReparsePoint);
        if (candidate.IsInvalid ||
            !WindowsPathResolver.TryGetFinalPath(candidate, out string resolvedPath) ||
            !IsTrustedWindowsPath(resolvedPath) ||
            !TryGetWindowsFileAttributes(candidate, out FileAttributes attributes) ||
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

        SafeFileHandle candidate = CreateWindowsHandle(CombineRelative(relativePath), FileFlagOpenReparsePoint | FileFlagBackupSemantics);
        if (candidate.IsInvalid)
        {
            candidate.Dispose();
            return false;
        }

        bool trusted = WindowsPathResolver.TryGetFinalPath(candidate, out string resolvedPath) && IsTrustedWindowsPath(resolvedPath);
        candidate.Dispose();
        return trusted;
    }

    internal bool TryDuplicateDirectory(string relativePath, out int descriptor)
    {
        descriptor = -1;
        SafeFileHandle? handle = null;
        try
        {
            if (!TryOpenDirectory(relativePath, out handle) || handle is null)
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
        out UnixFileIdentity identity,
        out bool isDirectory)
    {
        descriptor = UnixOpenAt(parentDescriptor, name, GetFileFlags());
        identity = default;
        isDirectory = false;
        if (descriptor < 0 || !TryGetUnixIdentity(descriptor, out identity))
        {
            if (descriptor >= 0)
            {
                _ = UnixClose(descriptor);
            }

            return false;
        }

        isDirectory = IsUnixDirectory(identity, descriptor);
        return true;
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

    internal static IntPtr ReadDirectoryEntry(IntPtr directoryStream) => UnixReadDirectory(directoryStream);

    internal static string? ReadDirectoryEntryName(IntPtr entry)
    {
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
                return Encoding.UTF8.GetString(bytes.ToArray());
            }

            bytes.Add(value);
        }

        return null;
    }

    internal static void CloseDirectoryStream(IntPtr directoryStream)
    {
        _ = UnixCloseDirectory(directoryStream);
    }

    public void Dispose()
    {
        unixRootHandle?.Dispose();
        windowsRootHandle?.Dispose();
    }

    private bool IsTrustedWindowsPath(string path) =>
        trustedWindowsRootPath is not null &&
        (path.Equals(trustedWindowsRootPath, StringComparison.OrdinalIgnoreCase) ||
         path.StartsWith(
             trustedWindowsRootPath.EndsWith('\\') || trustedWindowsRootPath.EndsWith('/')
                 ? trustedWindowsRootPath
                 : trustedWindowsRootPath + '\\',
             StringComparison.OrdinalIgnoreCase));

    private string CombineRelative(string relativePath) =>
        string.IsNullOrEmpty(relativePath) ? RootPath : Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string[] SplitRelativePath(string relativePath) =>
        relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal SystemTime CreationTime;
        internal SystemTime LastAccessTime;
        internal SystemTime LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        internal ushort Year;
        internal ushort Month;
        internal ushort DayOfWeek;
        internal ushort Day;
        internal ushort Hour;
        internal ushort Minute;
        internal ushort Second;
        internal ushort Milliseconds;
    }

    [DllImport("libc", EntryPoint = "open", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int UnixOpen([MarshalAs(UnmanagedType.LPStr)] string path, int flags);

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
        UnixFileIdentity? expectedIdentity,
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
        UnixFileIdentity? expectedIdentity,
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

        SafePathBoundary? boundary = SafePathBoundaryContext.Current;
        if (boundary is not null && boundary.Matches(fullRoot))
        {
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

}

internal sealed record SafeFileEntry(
    string FullPath,
    string RelativePath,
    UnixFileIdentity? ExpectedIdentity = null);

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
    private const int MaximumEntries = 100_000;
    internal static Action<string>? BeforeEnumerationForTesting { get; set; }
    internal static Action<string>? AfterEnumerationOpenedForTesting { get; set; }

    private sealed record UnixPendingDirectory(int Descriptor, string RelativePath);

    internal static WalkResult Walk(
        string repositoryRoot,
        string root,
        bool failOnAccessErrors,
        Func<string, GlobMatchStatus>? shouldPruneDirectory = null)
    {
        SafePathBoundary? ownedBoundary = null;
        IDisposable? boundaryScope = null;
        SafePathBoundary? boundary = SafePathBoundaryContext.Current;
        if (boundary is null || !boundary.Matches(repositoryRoot))
        {
            if (!SafePathBoundary.TryCreate(repositoryRoot, out ownedBoundary) || ownedBoundary is null)
            {
                return new WalkResult([], [], new ScanError("FV-E002", "A configured fixture root could not be inspected completely."));
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
                ? WalkUnix(repositoryRoot, relativeRoot, failOnAccessErrors, shouldPruneDirectory, boundary)
                : WalkPath(repositoryRoot, root, relativeRoot, failOnAccessErrors, shouldPruneDirectory, boundary);
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
        SafePathBoundary boundary)
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

        if (!boundary.TryDuplicateDirectory(relativeRoot, out int startingDescriptor))
        {
            return new WalkResult(files, reparsePaths, new ScanError(
                "FV-E002",
                "A configured fixture root could not be inspected completely."));
        }

        var pending = new Stack<UnixPendingDirectory>();
        pending.Push(new UnixPendingDirectory(startingDescriptor, relativeRoot));
        int entriesSeen = 0;

        try
        {
            while (pending.Count > 0)
            {
                UnixPendingDirectory directory = pending.Pop();
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
                        directory.RelativePath);
                    AfterEnumerationOpenedForTesting?.Invoke(directory.RelativePath);
                    if (!trustedPathAfterEnumerationHook)
                    {
                        return new WalkResult(files, reparsePaths, new ScanError(
                            "FV-E002",
                            "A configured fixture root could not be inspected completely."));
                    }

                    while (true)
                    {
                        IntPtr entry = SafePathBoundary.ReadDirectoryEntry(directoryStream);
                        string? name = SafePathBoundary.ReadDirectoryEntryName(entry);
                        if (name is null)
                        {
                            break;
                        }

                        if (name is "." or "..")
                        {
                            continue;
                        }

                        if (++entriesSeen > MaximumEntries)
                        {
                            return new WalkResult(files, reparsePaths, new ScanError(
                                "FV-E003",
                                "The scan exceeded its filesystem entry safety limit."));
                        }

                        string relativePath = string.IsNullOrEmpty(directory.RelativePath)
                            ? name
                            : directory.RelativePath + "/" + name;
                        if (SafePathBoundary.IsSymbolicLinkAt(directory.Descriptor, name))
                        {
                            reparsePaths.Add(relativePath);
                            continue;
                        }

                        if (!SafePathBoundary.TryOpenEntryAt(
                                directory.Descriptor,
                                name,
                                out int childDescriptor,
                                out UnixFileIdentity identity,
                                out bool isDirectory))
                        {
                            return new WalkResult(files, reparsePaths, new ScanError(
                                "FV-E002",
                                "A configured fixture root could not be inspected completely."));
                        }

                        if (isDirectory)
                        {
                            GlobMatchStatus directoryStatus = shouldPruneDirectory?.Invoke(relativePath) ?? GlobMatchStatus.NoMatch;
                            if (directoryStatus == GlobMatchStatus.Failure)
                            {
                                SafePathBoundary.CloseDescriptor(childDescriptor);
                                return new WalkResult(
                                    files,
                                    reparsePaths,
                                    new ScanError(FixtureVaultContract.IgnoredPathMatchingErrorCode, "Ignored path matching could not be completed safely."));
                            }

                            // A callback can replace the queued directory or one of its
                            // ancestors after the child descriptor was opened. The descriptor is
                            // safe to hold, but accepting the changed pathname would make the
                            // scan report a state different from the authorized tree. Require the
                            // current pathname to resolve through the same trusted root before
                            // queueing the child.
                            if (!IsTrustedCurrentDirectory(boundary, relativePath))
                            {
                                SafePathBoundary.CloseDescriptor(childDescriptor);
                                return new WalkResult(files, reparsePaths, new ScanError(
                                    "FV-E002",
                                    "A configured fixture root could not be inspected completely."));
                            }

                            if (directoryStatus == GlobMatchStatus.Match)
                            {
                                SafePathBoundary.CloseDescriptor(childDescriptor);
                            }
                            else
                            {
                                pending.Push(new UnixPendingDirectory(childDescriptor, relativePath));
                            }

                            continue;
                        }

                        files.Add(new SafeFileEntry(
                            Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)),
                            relativePath,
                            identity));
                        SafePathBoundary.CloseDescriptor(childDescriptor);
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
        SafePathBoundary boundary)
    {
        var files = new List<SafeFileEntry>();
        var reparsePaths = new List<string>();
        var pending = new Stack<DirectoryInfo>();
        DirectoryInfo startingDirectory = new(root);
        if (!boundary.TryOpenDirectory(relativeRoot, out SafeFileHandle? startingHandle) || startingHandle is null)
        {
            return new WalkResult(files, reparsePaths, new ScanError(
                "FV-E002",
                "A configured fixture root could not be inspected completely."));
        }

        startingHandle.Dispose();
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
            if (!IsSafeDirectoryForEnumeration(repositoryRoot, directory, boundary))
            {
                return new WalkResult(files, reparsePaths, new ScanError(
                    "FV-E002",
                    "A configured fixture root could not be inspected completely."));
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
                    if (!IsSafeDirectoryForEnumeration(repositoryRoot, directory, boundary))
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
                            hasEntry = enumerator.MoveNext();
                            continue;
                        }

                        return new WalkResult(files, reparsePaths, new ScanError(
                            "FV-E002",
                            "A configured fixture root could not be inspected completely."));
                    }

                    string relativePath = PathUtilities.NormalizeRelative(repositoryRoot, entry.FullName);
                    if (!boundary.TryPathExists(relativePath))
                    {
                        return new WalkResult(files, reparsePaths, new ScanError(
                            "FV-E002",
                            "A configured fixture root could not be inspected completely."));
                    }

                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        reparsePaths.Add(relativePath);
                        hasEntry = enumerator.MoveNext();
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
                            if (!boundary.TryOpenDirectory(relativePath, out SafeFileHandle? childHandle) || childHandle is null)
                            {
                                return new WalkResult(files, reparsePaths, new ScanError(
                                    "FV-E002",
                                    "A configured fixture root could not be inspected completely."));
                            }

                            childHandle.Dispose();
                            pending.Push(childDirectory);
                        }
                    }
                    else
                    {
                        if (!IsSafeDirectoryForEnumeration(repositoryRoot, directory, boundary))
                        {
                            return new WalkResult(files, reparsePaths, new ScanError(
                                "FV-E002",
                                "A configured fixture root could not be inspected completely."));
                        }

                        files.Add(new SafeFileEntry(entry.FullName, relativePath));
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
        SafePathBoundary boundary)
    {
        SafeFileHandle? handle = null;
        try
        {
            string fullRepositoryRoot = Path.GetFullPath(repositoryRoot);
            string fullDirectoryPath = Path.GetFullPath(directory.FullName);
            if (!PathUtilities.IsWithin(fullRepositoryRoot, fullDirectoryPath))
            {
                return false;
            }

            string relativePath = Path.GetRelativePath(fullRepositoryRoot, fullDirectoryPath);
            string normalizedRelative = relativePath == "." ? string.Empty : relativePath.Replace('\\', '/');
            return boundary.TryOpenDirectory(normalizedRelative, out handle) && handle is not null;
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

    private static bool IsTrustedCurrentDirectory(SafePathBoundary boundary, string relativePath)
    {
        SafeFileHandle? handle = null;
        try
        {
            return boundary.TryOpenDirectory(relativePath, out handle) && handle is not null;
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
