using System;
using System.Threading;
using UavOps.FleetClient;

namespace UavOps.MockFleetClient
{
    /// <summary>
    /// Dev/test stand-in for the real fleet-commanding .NET Framework application. Connects to
    /// UavOps.ControlApi's fleet command hub and answers every command with a hardcoded
    /// placeholder via EmptyCommandHandler — see that class and the project README for why this
    /// deliberately does not simulate fleet state.
    /// </summary>
    public static class Program
    {
        public static void Main(string[] args)
        {
            var hubUrl = args.Length > 0 ? args[0] : "http://localhost:5250/uavCommandHub";
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
            exitSignal.Wait();

            connection.StopAsync().GetAwaiter().GetResult();
        }
    }
}
