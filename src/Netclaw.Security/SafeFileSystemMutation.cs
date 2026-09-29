// -----------------------------------------------------------------------
// <copyright file="SafeFileSystemMutation.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Netclaw.Security;

/// <summary>
/// A use-time check found a link, a reparse point, or a non-directory where the
/// operation needs a real directory or a real file. The operation was not done.
/// </summary>
internal sealed class UnsafePathException(string message) : IOException(message);

/// <summary>
/// Opens a directory below a trusted root so that later file changes stay in that
/// directory, also when another process changes the tree at the same time.
/// </summary>
/// <remarks>
/// <para>
/// A path check followed by a path-based operation has a time-of-check to
/// time-of-use gap. Between the check and the operation, a process that can write
/// in the tree can put a link at a directory name or at the file name. The
/// operation then follows the link out of the tree. This type removes the gap:
/// </para>
/// <list type="bullet">
/// <item>POSIX (Linux, macOS): each directory below the root is opened relative to
/// its parent handle with <c>O_DIRECTORY | O_NOFOLLOW</c>. All later operations use
/// <c>openat</c>, <c>mkdirat</c>, <c>renameat</c>, and <c>unlinkat</c> on the final
/// handle. A name change after the open does not move the operation.</item>
/// <item>Windows: each directory below the root is opened with
/// <c>FILE_FLAG_OPEN_REPARSE_POINT</c> and is held without <c>FILE_SHARE_DELETE</c>.
/// While the handle is open, no other process can rename, delete, or replace that
/// directory. Final-name operations do not follow a reparse point.</item>
/// </list>
/// <para>
/// The trusted root and its ancestors belong to the operator. The root is opened
/// normally, so a link at the root or above it is followed, as in
/// <see cref="PathUtility.ContainsSymlinkSegment"/>. An unsupported OS or CPU
/// fails closed with <see cref="PlatformNotSupportedException"/>.
/// </para>
/// </remarks>
internal static partial class SafeFileSystemMutation
{
    /// <summary>
    /// Opens <paramref name="relativePath"/> below <paramref name="trustedRoot"/>.
    /// Returns <c>null</c> when a directory is missing and
    /// <paramref name="createMissing"/> is false.
    /// </summary>
    /// <exception cref="UnsafePathException">A segment is a link or is not a directory.</exception>
    /// <exception cref="PlatformNotSupportedException">This OS or CPU is not supported.</exception>
    public static VerifiedDirectory? OpenDirectory(string trustedRoot, string relativePath, bool createMissing)
    {
        var segments = SplitRelativePath(relativePath);
        if (OperatingSystem.IsWindows())
            return WindowsDirectory.Open(trustedRoot, segments, createMissing);

        var abi = PosixAbi.Current
            ?? throw new PlatformNotSupportedException(
                "Safe file system changes are not supported on this OS or CPU.");
        return PosixDirectory.Open(abi, trustedRoot, segments, createMissing);
    }

    private static string[] SplitRelativePath(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || relativePath == ".")
            return [];

        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
            ValidateName(segment);
        return segments;
    }

    internal static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".."
            || name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
            || (OperatingSystem.IsWindows() && name.Contains(':', StringComparison.Ordinal)))
        {
            throw new ArgumentException("The name must be one path segment below the root.", nameof(name));
        }
    }

    private static string ReadAll(SafeFileHandle handle)
    {
        using var stream = new FileStream(handle, FileAccess.Read);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    // Encoding.UTF8 writes a byte order mark, as File.WriteAllText(path, text, Encoding.UTF8) does.
    private static void WriteAll(SafeFileHandle handle, string contents)
    {
        using var stream = new FileStream(handle, FileAccess.Write);
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(contents);
    }

    private static List<string> ListNames(string path)
        => Directory.EnumerateFileSystemEntries(path).Select(entry => Path.GetFileName(entry)).ToList();

    // --- POSIX ---

    private sealed record PosixAbi(
        int OCreat,
        int OExcl,
        int ONonBlock,
        int ODirectory,
        int ONoFollow,
        int OCloExec,
        int AtFdCwd,
        int AtRemoveDir,
        int ENoEnt,
        int EExist,
        int ENotDir,
        int ELoop,
        int ENotEmpty,
        bool VariadicArgumentsOnStack)
    {
        public const int OReadOnly = 0;
        public const int OWriteOnly = 1;

        public static PosixAbi? Current { get; } = Detect();

        private static PosixAbi? Detect()
        {
            var arch = RuntimeInformation.ProcessArchitecture;
            if (OperatingSystem.IsLinux())
            {
                // O_DIRECTORY and O_NOFOLLOW differ between x86_64 and arm64 Linux.
                return arch switch
                {
                    Architecture.X64 => new PosixAbi(0x40, 0x80, 0x800, 0x10000, 0x20000, 0x80000,
                        -100, 0x200, 2, 17, 20, 40, 39, VariadicArgumentsOnStack: false),
                    Architecture.Arm64 => new PosixAbi(0x40, 0x80, 0x800, 0x4000, 0x8000, 0x80000,
                        -100, 0x200, 2, 17, 20, 40, 39, VariadicArgumentsOnStack: false),
                    _ => null,
                };
            }

            if (OperatingSystem.IsMacOS() && arch is Architecture.X64 or Architecture.Arm64)
            {
                return new PosixAbi(0x200, 0x800, 0x4, 0x100000, 0x100, 0x1000000,
                    -2, 0x80, 2, 17, 20, 62, 66, VariadicArgumentsOnStack: arch == Architecture.Arm64);
            }

            return null;
        }
    }

    private sealed class PosixDirectory : VerifiedDirectory
    {
        private const int NewFileMode = 0x1B6;      // 0666, reduced by the umask (the .NET default)
        private const uint NewDirectoryMode = 0x1FF; // 0777, reduced by the umask (the .NET default)

        private readonly PosixAbi _abi;
        private readonly SafeFileHandle _handle;
        private readonly string _logicalPath;

        private PosixDirectory(PosixAbi abi, SafeFileHandle handle, string logicalPath)
        {
            _abi = abi;
            _handle = handle;
            _logicalPath = logicalPath;
        }

        private int Fd => (int)_handle.DangerousGetHandle();

        public static PosixDirectory? Open(PosixAbi abi, string trustedRoot, string[] segments, bool createMissing)
        {
            var rootFd = OpenAt(abi, abi.AtFdCwd, trustedRoot, PosixAbi.OReadOnly | abi.ODirectory | abi.OCloExec);
            if (rootFd < 0)
                throw PosixError(Marshal.GetLastPInvokeError(), "open the trusted root");

            var current = new PosixDirectory(abi, new SafeFileHandle(rootFd, ownsHandle: true), trustedRoot);
            try
            {
                foreach (var segment in segments)
                {
                    var next = current.TryOpenChildDirectory(segment);
                    if (next is null)
                    {
                        if (!createMissing)
                        {
                            current.Dispose();
                            return null;
                        }

                        // mkdirat does not follow a link at the final name. EEXIST means
                        // another entry is there now; the open below checks what it is.
                        if (MkDirAt(current.Fd, segment, NewDirectoryMode) != 0)
                        {
                            var errno = Marshal.GetLastPInvokeError();
                            if (errno != abi.EExist)
                                throw PosixError(errno, $"create directory '{segment}'");
                        }

                        next = current.TryOpenChildDirectory(segment)
                            ?? throw new IOException($"The directory '{segment}' was removed while it was opened.");
                    }

                    current.Dispose();
                    current = next;
                }

                return current;
            }
            catch
            {
                current.Dispose();
                throw;
            }
        }

        public override string? ReadAllText(string name)
        {
            ValidateName(name);
            // O_NONBLOCK: a FIFO at this name must not block the read.
            var fd = OpenAt(_abi, Fd, name,
                PosixAbi.OReadOnly | _abi.ONoFollow | _abi.ONonBlock | _abi.OCloExec);
            if (fd < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (errno == _abi.ENoEnt)
                    return null;
                throw errno == _abi.ELoop
                    ? new UnsafePathException($"The file '{name}' is a link.")
                    : PosixError(errno, $"open '{name}'");
            }

            using var handle = new SafeFileHandle(fd, ownsHandle: true);
            if ((File.GetAttributes(handle) & FileAttributes.Directory) != 0)
                throw new IOException($"'{name}' is a directory.");
            return ReadAll(handle);
        }

        public override void WriteAllTextAtomic(string name, string contents, string tempName)
        {
            ValidateName(name);
            ValidateName(tempName);

            // Remove a stale temp entry. unlinkat removes a link itself, not its target.
            if (UnlinkAt(Fd, tempName, 0) != 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (errno != _abi.ENoEnt)
                    throw PosixError(errno, $"remove '{tempName}'");
            }

            // O_CREAT | O_EXCL fails on any existing entry, also a dangling link.
            var fd = OpenAt(_abi, Fd, tempName,
                PosixAbi.OWriteOnly | _abi.OCreat | _abi.OExcl | _abi.ONoFollow | _abi.OCloExec,
                NewFileMode);
            if (fd < 0)
                throw PosixError(Marshal.GetLastPInvokeError(), $"create '{tempName}'");

            try
            {
                using (var tempHandle = new SafeFileHandle(fd, ownsHandle: true))
                    WriteAll(tempHandle, contents);

                // renameat replaces the entry at the final name. It does not follow a link there.
                if (RenameAt(Fd, tempName, Fd, name) != 0)
                    throw PosixError(Marshal.GetLastPInvokeError(), $"replace '{name}'");
            }
            catch
            {
                // The write failed. Remove the temp entry; the original error is the result.
                _ = UnlinkAt(Fd, tempName, 0);
                throw;
            }
        }

        public override bool DeleteFile(string name)
        {
            ValidateName(name);
            if (UnlinkAt(Fd, name, 0) == 0)
                return true;

            var errno = Marshal.GetLastPInvokeError();
            if (errno == _abi.ENoEnt)
                return false;
            throw PosixError(errno, $"remove '{name}'");
        }

        public override bool DeleteEmptyDirectory(string name)
        {
            ValidateName(name);
            if (UnlinkAt(Fd, name, _abi.AtRemoveDir) == 0)
                return true;

            // Not empty, not there, or not a real directory: this removes only an empty directory.
            var errno = Marshal.GetLastPInvokeError();
            if (errno == _abi.ENoEnt || errno == _abi.ENotEmpty || errno == _abi.EExist || errno == _abi.ENotDir)
                return false;
            throw PosixError(errno, $"remove directory '{name}'");
        }

        public override void DeleteTree(string name)
        {
            ValidateName(name);
            using (var child = TryOpenChildDirectory(name)
                               ?? throw new DirectoryNotFoundException($"The directory '{name}' does not exist."))
            {
                child.DeleteContents();
            }

            if (UnlinkAt(Fd, name, _abi.AtRemoveDir) != 0)
                throw PosixError(Marshal.GetLastPInvokeError(), $"remove directory '{name}'");
        }

        // The listing comes from the logical path and gives names only. Each name is
        // used only relative to this handle. If the path does not point at this
        // directory, the final directory removal fails because entries remain.
        private void DeleteContents()
        {
            foreach (var entry in ListNames(_logicalPath))
            {
                var fd = OpenAt(_abi, Fd, entry,
                    PosixAbi.OReadOnly | _abi.ODirectory | _abi.ONoFollow | _abi.ONonBlock | _abi.OCloExec);
                if (fd >= 0)
                {
                    using (var child = new PosixDirectory(_abi, new SafeFileHandle(fd, ownsHandle: true),
                               Path.Combine(_logicalPath, entry)))
                    {
                        child.DeleteContents();
                    }

                    if (UnlinkAt(Fd, entry, _abi.AtRemoveDir) != 0)
                        throw PosixError(Marshal.GetLastPInvokeError(), $"remove directory '{entry}'");
                    continue;
                }

                var errno = Marshal.GetLastPInvokeError();
                if (errno == _abi.ENoEnt)
                    continue;
                if (errno != _abi.ELoop && errno != _abi.ENotDir)
                    throw PosixError(errno, $"open '{entry}'");

                // A file or a link. unlinkat removes the entry and does not follow a link.
                if (UnlinkAt(Fd, entry, 0) != 0)
                {
                    errno = Marshal.GetLastPInvokeError();
                    if (errno != _abi.ENoEnt)
                        throw PosixError(errno, $"remove '{entry}'");
                }
            }
        }

        private PosixDirectory? TryOpenChildDirectory(string name)
        {
            var fd = OpenAt(_abi, Fd, name,
                PosixAbi.OReadOnly | _abi.ODirectory | _abi.ONoFollow | _abi.ONonBlock | _abi.OCloExec);
            if (fd >= 0)
                return new PosixDirectory(_abi, new SafeFileHandle(fd, ownsHandle: true), Path.Combine(_logicalPath, name));

            var errno = Marshal.GetLastPInvokeError();
            if (errno == _abi.ENoEnt)
                return null;
            // ELOOP: a link at this name. ENOTDIR: not a directory (some systems report a link so).
            if (errno == _abi.ELoop || errno == _abi.ENotDir)
                throw new UnsafePathException($"The path segment '{name}' is a link or is not a directory.");
            throw PosixError(errno, $"open directory '{name}'");
        }

        public override void Dispose() => _handle.Dispose();

        private static int OpenAt(PosixAbi abi, int dirFd, string path, int flags, int mode = 0)
            => abi.VariadicArgumentsOnStack
                ? OpenAtModeOnStack(dirFd, path, flags, 0, 0, 0, 0, 0, mode)
                : OpenAtModeInRegister(dirFd, path, flags, mode);

        private static IOException PosixError(int errno, string operation)
            => new($"Could not {operation}: {Marshal.GetPInvokeErrorMessage(errno)} (errno {errno}).");
    }

    // openat is variadic: it reads the mode as a variadic argument. Linux (x86_64,
    // arm64) and macOS x86_64 pass that argument in the next register.
    [LibraryImport("libc", EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenAtModeInRegister(int dirFd, string path, int flags, int mode);

    // Apple arm64 passes all variadic arguments on the stack. The five padding
    // arguments fill x3 to x7, so the mode goes into the first stack slot, where
    // openat reads it.
    [LibraryImport("libc", EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenAtModeOnStack(
        int dirFd, string path, int flags, nint x3, nint x4, nint x5, nint x6, nint x7, nint mode);

    [LibraryImport("libc", EntryPoint = "mkdirat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int MkDirAt(int dirFd, string path, uint mode);

    [LibraryImport("libc", EntryPoint = "unlinkat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int UnlinkAt(int dirFd, string path, int flags);

    [LibraryImport("libc", EntryPoint = "renameat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int RenameAt(int oldDirFd, string oldPath, int newDirFd, string newPath);

    // --- Windows ---

    private sealed class WindowsDirectory : VerifiedDirectory
    {
        private const uint FileListDirectory = 0x1;
        private const uint FileReadAttributes = 0x80;
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareRead = 0x1;
        private const uint FileShareWrite = 0x2;
        private const uint CreateNew = 1;
        private const uint OpenExisting = 3;
        private const uint FileAttributeNormal = 0x80;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;
        private const int ErrorDirNotEmpty = 145;
        private const int ErrorDirectory = 267;

        // FILE_LIST_DIRECTORY makes the handle take part in share checks. Without
        // FILE_SHARE_DELETE, no other open for DELETE succeeds, so nobody can rename,
        // delete, or replace this directory while the handle is open.
        private const uint HeldDirectoryAccess = FileListDirectory | FileReadAttributes;
        private const uint HeldDirectoryShare = FileShareRead | FileShareWrite;

        private readonly List<SafeFileHandle> _chain;
        private readonly string _path;

        private WindowsDirectory(List<SafeFileHandle> chain, string path)
        {
            _chain = chain;
            _path = path;
        }

        public static WindowsDirectory? Open(string trustedRoot, string[] segments, bool createMissing)
        {
            var chain = new List<SafeFileHandle>();
            try
            {
                // The root may be a link. Children are named from its final path, so a
                // later change of that link cannot move them.
                var root = CreateFileW(trustedRoot, HeldDirectoryAccess, HeldDirectoryShare, 0,
                    OpenExisting, FileFlagBackupSemantics, 0);
                if (root.IsInvalid)
                    throw WindowsError(Marshal.GetLastPInvokeError(), "open the trusted root");
                chain.Add(root);
                var path = GetFinalPath(root);

                foreach (var segment in segments)
                {
                    var childPath = Path.Combine(path, segment);
                    var child = TryOpenDirectory(childPath, segment);
                    if (child is null)
                    {
                        if (!createMissing)
                        {
                            DisposeAll(chain);
                            return null;
                        }

                        // The parent is held, so this name resolves in the held parent. The
                        // open below rejects a reparse point that is at this name.
                        Directory.CreateDirectory(childPath);
                        child = TryOpenDirectory(childPath, segment)
                            ?? throw new IOException($"The directory '{segment}' was removed while it was opened.");
                    }

                    chain.Add(child);
                    path = childPath;
                }

                return new WindowsDirectory(chain, path);
            }
            catch
            {
                DisposeAll(chain);
                throw;
            }
        }

        public override string? ReadAllText(string name)
        {
            ValidateName(name);
            var handle = CreateFileW(Path.Combine(_path, name), GenericRead, FileShareRead, 0,
                OpenExisting, FileFlagOpenReparsePoint, 0);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                if (error is ErrorFileNotFound or ErrorPathNotFound)
                    return null;
                throw WindowsError(error, $"open '{name}'");
            }

            using (handle)
            {
                if ((File.GetAttributes(handle) & FileAttributes.ReparsePoint) != 0)
                    throw new UnsafePathException($"The file '{name}' is a link.");
                return ReadAll(handle);
            }
        }

        public override void WriteAllTextAtomic(string name, string contents, string tempName)
        {
            ValidateName(name);
            ValidateName(tempName);
            var tempPath = Path.Combine(_path, tempName);

            // Remove a stale temp entry. DeleteFileW removes a link itself, not its target.
            if (!DeleteFileW(tempPath))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error is not (ErrorFileNotFound or ErrorPathNotFound))
                    throw WindowsError(error, $"remove '{tempName}'");
            }

            // CREATE_NEW with FILE_FLAG_OPEN_REPARSE_POINT fails on any existing entry,
            // also a link, and does not follow it.
            var handle = CreateFileW(tempPath, GenericWrite, 0, 0, CreateNew,
                FileAttributeNormal | FileFlagOpenReparsePoint, 0);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw WindowsError(error, $"create '{tempName}'");
            }

            try
            {
                WriteAll(handle, contents);

                // MoveFileExW replaces the entry at the final name. It does not follow a link there.
                File.Move(tempPath, Path.Combine(_path, name), overwrite: true);
            }
            catch
            {
                // The write failed. Remove the temp entry; the original error is the result.
                _ = DeleteFileW(tempPath);
                throw;
            }
        }

        public override bool DeleteFile(string name)
        {
            ValidateName(name);
            if (DeleteFileW(Path.Combine(_path, name)))
                return true;

            var error = Marshal.GetLastPInvokeError();
            if (error is ErrorFileNotFound or ErrorPathNotFound)
                return false;
            throw WindowsError(error, $"remove '{name}'");
        }

        public override bool DeleteEmptyDirectory(string name)
        {
            ValidateName(name);
            if (RemoveDirectoryW(Path.Combine(_path, name)))
                return true;

            // Not empty, not there, or not a directory: this removes only an empty directory.
            var error = Marshal.GetLastPInvokeError();
            if (error is ErrorFileNotFound or ErrorPathNotFound or ErrorDirNotEmpty or ErrorDirectory)
                return false;
            throw WindowsError(error, $"remove directory '{name}'");
        }

        public override void DeleteTree(string name)
        {
            ValidateName(name);
            var path = Path.Combine(_path, name);
            using (var handle = TryOpenDirectory(path, name)
                                ?? throw new DirectoryNotFoundException($"The directory '{name}' does not exist."))
            {
                DeleteContents(path);
            }

            // The handle is closed. RemoveDirectoryW does not follow a reparse point at
            // this name, and it fails when the directory is not empty.
            if (!RemoveDirectoryW(path))
                throw WindowsError(Marshal.GetLastPInvokeError(), $"remove directory '{name}'");
        }

        // The caller holds the handle for path, and every ancestor up to the root is held.
        private static void DeleteContents(string path)
        {
            foreach (var entry in ListNames(path))
            {
                var entryPath = Path.Combine(path, entry);
                var handle = CreateFileW(entryPath, HeldDirectoryAccess, HeldDirectoryShare, 0,
                    OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, 0);
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastPInvokeError();
                    handle.Dispose();
                    if (error is ErrorFileNotFound or ErrorPathNotFound)
                        continue;
                    throw WindowsError(error, $"open '{entry}'");
                }

                FileAttributes attributes;
                using (handle)
                {
                    attributes = File.GetAttributes(handle);
                    if ((attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0)
                        DeleteContents(entryPath);
                }

                // A reparse point is removed as an entry. Its target is not changed.
                var removed = (attributes & FileAttributes.Directory) != 0
                    ? RemoveDirectoryW(entryPath)
                    : DeleteFileW(entryPath);
                if (!removed)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error is not (ErrorFileNotFound or ErrorPathNotFound))
                        throw WindowsError(error, $"remove '{entry}'");
                }
            }
        }

        private static SafeFileHandle? TryOpenDirectory(string path, string name)
        {
            var handle = CreateFileW(path, HeldDirectoryAccess, HeldDirectoryShare, 0,
                OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, 0);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                if (error is ErrorFileNotFound or ErrorPathNotFound)
                    return null;
                throw WindowsError(error, $"open directory '{name}'");
            }

            var attributes = File.GetAttributes(handle);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
            {
                handle.Dispose();
                throw new UnsafePathException($"The path segment '{name}' is a link or is not a directory.");
            }

            return handle;
        }

        private static string GetFinalPath(SafeFileHandle handle)
        {
            var buffer = new char[512];
            while (true)
            {
                var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
                if (length == 0)
                    throw WindowsError(Marshal.GetLastPInvokeError(), "resolve the trusted root");
                if (length < buffer.Length)
                    return new string(buffer, 0, (int)length);
                buffer = new char[length + 1];
            }
        }

        private static void DisposeAll(List<SafeFileHandle> handles)
        {
            for (var i = handles.Count - 1; i >= 0; i--)
                handles[i].Dispose();
        }

        public override void Dispose() => DisposeAll(_chain);

        private static IOException WindowsError(int error, string operation)
            => new($"Could not {operation}: {new Win32Exception(error).Message} (error {error}).");
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "DeleteFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteFileW(string fileName);

    [LibraryImport("kernel32.dll", EntryPoint = "RemoveDirectoryW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveDirectoryW(string pathName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint GetFinalPathNameByHandleW(
        SafeFileHandle file, [Out] char[] filePath, uint filePathLength, uint flags);
}

/// <summary>
/// A directory that <see cref="SafeFileSystemMutation.OpenDirectory"/> opened and
/// checked. Each method takes one name in this directory and does not follow a
/// link at that name.
/// </summary>
internal abstract class VerifiedDirectory : IDisposable
{
    /// <summary>Returns the text of a file, or <c>null</c> when the file does not exist.</summary>
    /// <exception cref="UnsafePathException">The name is a link.</exception>
    public abstract string? ReadAllText(string name);

    /// <summary>
    /// Writes <paramref name="contents"/> to <paramref name="tempName"/> as a new file,
    /// then renames it to <paramref name="name"/>.
    /// </summary>
    public abstract void WriteAllTextAtomic(string name, string contents, string tempName);

    /// <summary>Removes a file or a link entry. Returns false when no entry exists.</summary>
    public abstract bool DeleteFile(string name);

    /// <summary>Removes an empty directory. Returns false when it is missing or not empty.</summary>
    public abstract bool DeleteEmptyDirectory(string name);

    /// <summary>Removes a directory and its contents. A link in the tree is removed, not followed.</summary>
    /// <exception cref="UnsafePathException">The name is a link or is not a directory.</exception>
    public abstract void DeleteTree(string name);

    public abstract void Dispose();
}
