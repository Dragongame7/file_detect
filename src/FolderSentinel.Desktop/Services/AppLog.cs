namespace FolderSentinel.Desktop.Services;

public sealed class AppLog(string directory)
{
    private readonly object gate = new();
    public void Write(string message, Exception? error = null)
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var file = Path.Combine(directory, "app.log");
                if (File.Exists(file) && new FileInfo(file).Length > 2_000_000) File.Move(file, file + ".previous", true);
                File.AppendAllText(file, $"{DateTimeOffset.Now:O} {message} {error}\n");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
