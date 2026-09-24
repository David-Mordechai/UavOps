using System;
using System.Threading;
using UavOps.FleetClient;

namespace UavOps.MockFleetClient
{
    /// <summary>
    /// Dev/test stand-in for the real fleet-commanding .NET Framework application. Connects to
    /// UavOps.Agent's fleet command hub and answers every command with a hardcoded placeholder
    /// via EmptyCommandHandler — see that class and the project README for why this deliberately
    /// does not simulate fleet state.
    /// </summary>
    public static class Program
    {
        public static void Main(string[] args)
        {
            var hubUrl = args.Length > 0 ? args[0] : "http://localhost:5262/uavCommandHub";
            Console.WriteLine("UavOps.MockFleetClient - connecting to " + hubUrl);

            var connection = new FleetClientConnection(hubUrl, new EmptyCommandHandler());
            connection.Connected += id => Console.WriteLine("Connected (connectionId={0})", id);
            connection.Disconnected += ex => Console.WriteLine("Disconnected: {0}", ex != null ? ex.Message : "(no error)");
            connection.Reconnecting += ex => Console.WriteLine("Reconnecting: {0}", ex != null ? ex.Message : "(no error)");

            connection.StartAsync().GetAwaiter().GetResult();

            // Waits on Ctrl+C rather than Console.ReadLine() — this needs to run correctly both
            // interactively and under non-interactive hosting (a script, a service wrapper, CI),
            // where stdin isn't a real console and ReadLine() would return immediately.
            Console.WriteLine("Running. Press Ctrl+C to exit.");
            var exitSignal = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (sender, eventArgs) =>
            {
                eventArgs.Cancel = true;
                exitSignal.Set();
            };

            if (!Console.IsInputRedirected)
            {
                StartPushToTalkKey(connection, exitSignal);
            }

            exitSignal.Wait();

            connection.StopAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Stands in for a joystick talk button: a console can't see a key being *released*, so
        /// the T key toggles instead - first press = button down, second press = button up.
        /// Only when a real console is attached (see the Ctrl+C comment above).
        /// </summary>
        private static void StartPushToTalkKey(FleetClientConnection connection, ManualResetEventSlim exitSignal)
        {
            Console.WriteLine("Press T to hold the push-to-talk button, T again to release it.");
            var thread = new Thread(() =>
            {
                var pressed = false;
                while (!exitSignal.IsSet)
                {
                    if (!Console.KeyAvailable)
                    {
                        Thread.Sleep(50);
                        continue;
                    }
                    if (Console.ReadKey(intercept: true).Key != ConsoleKey.T)
                    {
                        continue;
                    }

                    pressed = !pressed;
                    try
                    {
                        var handled = connection.SetPushToTalkAsync(pressed).GetAwaiter().GetResult();
                        Console.WriteLine("Push-to-talk {0}{1}", pressed ? "PRESSED" : "RELEASED",
                            handled ? "" : " (no chat tab acted on it)");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Push-to-talk failed: {0}", ex.Message);
                        pressed = !pressed;
                    }
                }
            })
            { IsBackground = true };
            thread.Start();
        }
    }
}
