using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace MP3Player
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string channel;
            SecurityIdentifier user;
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            using (Process process = Process.GetCurrentProcess())
            {
                user = identity.User;
                channel = $"MP3Player.{user.Value}.{process.SessionId}";
            }

            // Resolve relative paths in the launching process before forwarding them.
            string[] paths = Mp3Paths.Resolve(args);
            using (var mutex = new Mutex(false, @"Local\" + channel))
            {
                var elapsed = Stopwatch.StartNew();
                while (!TryAcquire(mutex))
                {
                    if (paths.Length == 0 || PathPipe.TrySend(channel, paths))
                        return 0;
                    if (elapsed.Elapsed >= TimeSpan.FromSeconds(5))
                        return 1;
                }

                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (var form = new MainForm())
                    using (var pipe = new PathPipe(channel, user))
                    {
                        form.Load += (sender, e) =>
                        {
                            pipe.Start(received => form.BeginInvoke(new Action(() => form.AddPaths(received))));
                            form.AddPaths(paths);
                        };
                        Application.Run(form);
                    }
                    return 0;
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }
        }

        private static bool TryAcquire(Mutex mutex)
        {
            try
            {
                return mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                return true;
            }
        }
    }
}
