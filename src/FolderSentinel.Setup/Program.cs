using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace FolderSentinel.Setup;

public static class Program
{
    public static readonly string InstallPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "FolderSentinel");
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\FolderSentinel";
    private static readonly string ShortcutPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Folder Sentinel.lnk");
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--extract-only") { ExtractPayload(Path.GetFullPath(args[1])); return 0; }
            if (args.Length == 2 && args[0] == "--remove-installed") { RemoveInstalled(int.Parse(args[1])); return 0; }
            if (args.Contains("--uninstall"))
            {
                if (MessageBox.Show("Folder Sentinelをアンインストールしますか？\n監視先のファイル、および保存済みの設定・履歴は削除しません。", "Folder Sentinel", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return 0;
                RequireAppClosed();
                var temp = Path.Combine(Path.GetTempPath(), "FolderSentinel-Removal", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
                var copy = Path.Combine(temp, "Remove.exe"); File.Copy(Environment.ProcessPath!, copy);
                var info = new ProcessStartInfo(copy) { UseShellExecute = false }; info.ArgumentList.Add("--remove-installed"); info.ArgumentList.Add(Environment.ProcessId.ToString()); Process.Start(info); return 0;
            }
            var app = new Application(); app.Run(new SetupWindow()); return 0;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Folder Sentinel セットアップ", MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
    }
    public static void ExtractPayload(string directory)
    {
        var destination = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(destination);
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("Payload.zip") ?? throw new InvalidOperationException("アプリの配布データが見つかりません。");
        using var archive = new ZipArchive(resource, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(destination, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("配布データに無効なパスがあります。");
            if (entry.Name.Length == 0) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); entry.ExtractToFile(target, true);
        }
    }
    public static void RequireAppClosed()
    {
        foreach (var process in Process.GetProcessesByName("FolderSentinel"))
        { using (process) if (!process.HasExited) throw new InvalidOperationException("Folder Sentinelが動作しています。タスクトレイの「終了」からアプリを終了して、もう一度実行してください。"); }
    }
    public static void Install(bool startup)
    {
        RequireAppClosed();
        var installedSetup = Path.Combine(InstallPath, "FolderSentinel.Setup.exe");
        if (!string.Equals(Environment.ProcessPath, installedSetup, StringComparison.OrdinalIgnoreCase)) File.Copy(Environment.ProcessPath!, installedSetup, true);
        CreateShortcut();
        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", "Folder Sentinel"); key.SetValue("DisplayVersion", Assembly.GetExecutingAssembly().GetName().Version!.ToString(3)); key.SetValue("InstallLocation", InstallPath);
            key.SetValue("UninstallString", $"\"{installedSetup}\" --uninstall"); key.SetValue("DisplayIcon", Path.Combine(InstallPath, "FolderSentinel.exe"));
            key.SetValue("NoModify", 1, RegistryValueKind.DWord); key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        using (var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
        {
            if (startup) run.SetValue("FolderSentinel", $"\"{Path.Combine(InstallPath, "FolderSentinel.exe")}\" --background");
            else if (run.GetValue("FolderSentinel") is string value && value.Contains(InstallPath, StringComparison.OrdinalIgnoreCase)) run.DeleteValue("FolderSentinel", false);
        }
    }
    private static void CreateShortcut()
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("スタートメニューのショートカットを作成できません。");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(ShortcutPath);
            try { shortcut.TargetPath = Path.Combine(InstallPath, "FolderSentinel.exe"); shortcut.WorkingDirectory = InstallPath; shortcut.Description = "フォルダ追加通知"; shortcut.Save(); }
            finally { Marshal.FinalReleaseComObject(shortcut); }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }
    private static void RemoveInstalled(int parentId)
    {
        try { using var parent = Process.GetProcessById(parentId); parent.WaitForExit(10000); } catch (ArgumentException) { }
        RequireAppClosed();
        var expectedParent = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(InstallPath);
        if (!target.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetFileName(target), "FolderSentinel", StringComparison.Ordinal)) throw new InvalidOperationException("削除先のパスを確認できません。");
        if (Directory.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("インストール先がリンクに変更されているため削除を中止しました。");
        var exe = Path.Combine(target, "FolderSentinel.exe");
        if (File.Exists(exe))
        {
            using var process = Process.Start(new ProcessStartInfo(exe, "--unregister-notifications --no-startup") { UseShellExecute = false, CreateNoWindow = true });
            if (process is not null && !process.WaitForExit(15000)) throw new InvalidOperationException("アプリの終了を確認できませんでした。");
        }
        using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
            if (run?.GetValue("FolderSentinel") is string value && value.Contains(target, StringComparison.OrdinalIgnoreCase)) run.DeleteValue("FolderSentinel", false);
        if (Directory.Exists(target)) Directory.Delete(target, true);
        if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        MessageBox.Show("アンインストールしました。\n設定と履歴は %LOCALAPPDATA%\\FolderSentinel に残しています。", "Folder Sentinel");
    }
}

public sealed class SetupWindow : Window
{
    private readonly Button install;
    private readonly CheckBox startup;
    private readonly TextBlock status;
    private bool busy;
    public SetupWindow()
    {
        Title = "Folder Sentinel セットアップ"; Width = 560; Height = 480; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Closing += (_, args) => { if (busy) args.Cancel = true; };
        FontFamily = new FontFamily("Yu Gothic UI"); FontSize = 13; Background = Brushes.White;
        var panel = new StackPanel { Margin = new Thickness(30) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "Folder Sentinel", FontSize = 30, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(28, 48, 70)) });
        panel.Children.Add(new TextBlock { Text = "フォルダへの新しいファイルを、必要なタイミングで。", Margin = new Thickness(0, 7, 0, 24) });
        panel.Children.Add(new TextBlock { Text = "このユーザー用にインストールします。\nNode.jsや.NETなどを別にインストールする必要はありません。", TextWrapping = TextWrapping.Wrap, LineHeight = 22 });
        panel.Children.Add(new TextBlock { Text = Program.InstallPath, FontSize = 11, Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 15) });
        startup = new CheckBox { Content = "Windowsへのサインイン時に自動起動する", IsChecked = true, Margin = new Thickness(0, 0, 0, 20) }; panel.Children.Add(startup);
        install = new Button { Content = "インストールして起動", Padding = new Thickness(18, 10, 18, 10), Background = new SolidColorBrush(Color.FromRgb(36, 94, 199)), Foreground = Brushes.White, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left };
        install.Click += InstallClick; panel.Children.Add(install);
        status = new TextBlock { Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray }; panel.Children.Add(status);
    }
    private async void InstallClick(object sender, RoutedEventArgs e)
    {
        busy = true; install.IsEnabled = false; startup.IsEnabled = false; status.Text = "インストールしています…";
        bool enableStartup = startup.IsChecked == true;
        try
        {
            // Shortcut COM automation requires the UI's STA apartment; extraction happens off the UI thread.
            await Task.Run(() => { Program.RequireAppClosed(); Program.ExtractPayload(Program.InstallPath); });
            Program.Install(enableStartup);
            Process.Start(new ProcessStartInfo(Path.Combine(Program.InstallPath, "FolderSentinel.exe"), enableStartup ? "" : "--no-startup") { UseShellExecute = false });
            busy = false; Close();
        }
        catch (Exception ex) { busy = false; status.Text = ex.Message; install.IsEnabled = true; startup.IsEnabled = true; }
    }
}
