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

            var handler = new EmptyCommandHandler();
            var connection = new FleetClientConnection(hubUrl, handler);
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
                StartKeyCommands(connection, handler, exitSignal);
            }

            exitSignal.Wait();

            connection.StopAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Keys, only when a real console is attached (see the Ctrl+C comment above):
        /// T stands in for a joystick talk button - a console can't see a key being *released*,
        /// so it toggles instead: first press = button down, second press = button up.
        /// D reports a fake search detection (a white van inside ZoneA, or whatever the last
        /// search target was), to exercise the host's detection handling without UavOps.Simulator.
        /// </summary>
        private static void StartKeyCommands(FleetClientConnection connection, EmptyCommandHandler handler, ManualResetEventSlim exitSignal)
        {
            Console.WriteLine("Press T to hold the push-to-talk button, T again to release it.");
            Console.WriteLine("Press D to report a fake search detection.");
            var thread = new Thread(() =>
            {
                var pressed = false;
                var detections = 0;
                while (!exitSignal.IsSet)
                {
                    if (!Console.KeyAvailable)
                    {
                        Thread.Sleep(50);
                        continue;
                    }

                    var key = Console.ReadKey(intercept: true).Key;
                    if (key == ConsoleKey.T)
                    {
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
                    else if (key == ConsoleKey.D)
                    {
                        // Each press is ~200 m further north, so the host's dedupe doesn't drop it.
                        var report = handler.FakeDetection(offsetMeters: 200 * detections++);
                        try
                        {
                            connection.ReportDetectionAsync(report).GetAwaiter().GetResult();
                            Console.WriteLine("Reported detection: {0} at {1:F5}, {2:F5} by {3}", report.Prompt, report.Lat, report.Lng, report.TailNumber);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("Reporting the detection failed: {0}", ex.Message);
                        }
                    }
                }
            })
            { IsBackground = true };
            thread.Start();
        }
    }
}
