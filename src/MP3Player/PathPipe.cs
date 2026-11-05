using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace MP3Player
{
    internal sealed class PathPipe : IDisposable
    {
        private const int MaxMessageBytes = 128 * 1024;
        private readonly NamedPipeServerStream server;
        private volatile bool closing;

        public PathPipe(string name, SecurityIdentifier user)
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
            server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Message,
                PipeOptions.Asynchronous, 4096, 4096, security);
        }

        public void Start(Action<string[]> receive)
        {
            _ = Task.Run(() => ListenAsync(receive));
        }

        public static bool TrySend(string name, string[] paths)
        {
            using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                try
                {
                    client.Connect(250);
                    byte[] bytes = Encoding.UTF8.GetBytes(string.Join("\0", paths));
                    Task<bool> transfer = SendAsync(client, bytes);
                    return transfer.Wait(3000) && transfer.Result;
                }
                catch (TimeoutException)
                {
                    return false;
                }
                catch (AggregateException ex) when (ex.InnerException is IOException)
                {
                    Trace.WriteLine(ex.InnerException);
                    return false;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Trace.WriteLine(ex);
                    return false;
                }
            }
        }

        private static async Task<bool> SendAsync(Stream client, byte[] bytes)
        {
            await client.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            var ack = new byte[1];
            return await client.ReadAsync(ack, 0, ack.Length).ConfigureAwait(false) == 1 && ack[0] == 1;
        }

        private async Task ListenAsync(Action<string[]> receive)
        {
            var buffer = new byte[4096];
            try
            {
                while (!closing)
                {
                    await server.WaitForConnectionAsync().ConfigureAwait(false);
                    try
                    {
                        using (var message = new MemoryStream())
                        {
                            do
                            {
                                int count = await server.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                                if (count == 0 || message.Length + count > MaxMessageBytes)
                                    break;
                                message.Write(buffer, 0, count);
                                if (server.IsMessageComplete && !closing)
                                {
                                    string[] paths = Encoding.UTF8.GetString(message.ToArray()).Split('\0');
                                    receive(paths);
                                    await server.WriteAsync(new byte[] { 1 }, 0, 1).ConfigureAwait(false);
                                    // Keep the connection open until the client has read the acknowledgement.
                                    await server.ReadAsync(buffer, 0, 1).ConfigureAwait(false);
                                    break;
                                }
                            } while (!closing);
                        }
                    }
                    catch (IOException ex)
                    {
                        Trace.WriteLine(ex);
                    }
                    finally
                    {
                        if (!closing)
                            server.Disconnect();
                    }
                }
            }
            catch (Exception ex) when (closing && (ex is InvalidOperationException || ex is IOException))
            {
                // Closing the pipe interrupts the pending connection or read.
            }
            catch (InvalidOperationException ex)
            {
                // The form handle can disappear during shutdown.
                Trace.WriteLine(ex);
            }
        }

        public void Dispose()
        {
            closing = true;
            server.Dispose();
        }
    }
}
