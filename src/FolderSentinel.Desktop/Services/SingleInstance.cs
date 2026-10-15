using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace FolderSentinel.Desktop.Services;

public sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly string name;
    private readonly CancellationTokenSource stop = new();
    public bool IsPrimary { get; }
    public SingleInstance(string dataDirectory)
    {
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataDirectory.ToUpperInvariant())))[..20];
        name = "FolderSentinel-" + suffix;
        mutex = new Mutex(false, @"Local\" + name);
        try { IsPrimary = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsPrimary = true; }
    }
    public void Listen(Action<string> action, AppLog log) => _ = Task.Run(async () =>
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); timeout.CancelAfter(3000);
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                var buffer = new char[1]; var value = new StringBuilder();
                while (value.Length < 512)
                {
                    int count = await reader.ReadAsync(buffer.AsMemory(), timeout.Token);
                    if (count == 0 || buffer[0] == '\n') break;
                    value.Append(buffer[0]);
                }
                if (value.Length > 0) action(value.ToString().Trim());
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { log.Write("起動操作の受信に失敗", ex); if (!stop.IsCancellationRequested) await Task.Delay(500); }
        }
    });
    public async Task SendAsync(string value)
    {
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(2500);
        var bytes = Encoding.UTF8.GetBytes(value + "\n"); await pipe.WriteAsync(bytes); await pipe.FlushAsync();
    }
    public void Dispose() { stop.Cancel(); if (IsPrimary) mutex.ReleaseMutex(); mutex.Dispose(); }
}
