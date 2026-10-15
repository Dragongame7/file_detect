using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FolderSentinel.Desktop.Services;

public sealed class WindowsFileScanner
{
    public ScanResult Scan(string rootPath, CancellationToken token)
    {
        var files = new List<FileStamp>();
        var failed = new List<string>();
        var errors = new List<string>();
        var directories = new Stack<string>(); directories.Push(rootPath);
        bool rootAccessible = false;
        while (directories.TryPop(out string? directory))
        {
            token.ThrowIfCancellationRequested();
            var relativeDirectory = Path.GetRelativePath(rootPath, directory);
            if (relativeDirectory == ".") relativeDirectory = "";
            try
            {
                // Materialize each directory so an enumeration failure cannot falsely mark it as complete.
                var children = Directory.GetFileSystemEntries(directory);
                if (relativeDirectory.Length == 0) rootAccessible = true;
                foreach (var path in children)
                {
                    token.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(rootPath, path);
                    try
                    {
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if ((attributes & FileAttributes.Directory) != 0) { directories.Push(path); continue; }
                        var info = new FileInfo(path);
                        var identity = FileIdentity.Read(path);
                        files.Add(new(relative, identity, info.Length, info.LastWriteTimeUtc.Ticks));
                    }
                    catch (FileNotFoundException) { /* A concurrent removal will be reconciled on the next pass. */ }
                    catch (DirectoryNotFoundException) { }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
                    { failed.Add(relative); if (errors.Count < 3) errors.Add($"{relative}: {ex.Message}"); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { failed.Add(relativeDirectory); if (errors.Count < 3) errors.Add($"{directory}: {ex.Message}"); }
        }
        return new(files, failed, rootAccessible, errors.Count == 0 ? null : string.Join(Environment.NewLine, errors));
    }
}

public static class FileIdentity
{
    [StructLayout(LayoutKind.Sequential)] private struct FileIdInfo { public ulong Volume; public ulong Low; public ulong High; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicInfo
    {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileIdInfo info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out BasicInfo info);

    public static string Read(string path)
    {
        var absolute = Path.GetFullPath(path);
        var nativePath = absolute.StartsWith("\\\\?\\", StringComparison.Ordinal) ? absolute : absolute.StartsWith("\\\\", StringComparison.Ordinal) ? "\\\\?\\UNC\\" + absolute[2..] : "\\\\?\\" + absolute;
        using var handle = CreateFileW(nativePath, 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (GetFileInformationByHandleEx(handle, 18, out var id, (uint)Marshal.SizeOf<FileIdInfo>()) && (id.Low != 0 || id.High != 0))
            return $"128:{id.Volume:X16}:{id.High:X16}{id.Low:X16}";
        if (GetFileInformationByHandle(handle, out var basic) && (basic.IndexHigh != 0 || basic.IndexLow != 0))
            return $"64:{basic.Volume:X8}:{basic.IndexHigh:X8}{basic.IndexLow:X8}";
        throw new IOException("ファイルIDを取得できません。この保存先の対応可否を確認してください。");
    }
}
