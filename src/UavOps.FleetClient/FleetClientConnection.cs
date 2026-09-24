using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json;

namespace UavOps.FleetClient
{
    /// <summary>
    /// Connects to UavOps.Agent's fleet command hub as a SignalR client and dispatches every
    /// incoming command to the supplied <see cref="IUavCommandHandler"/>, replying with its
    /// result. This is the reusable piece — a host app (the dev-only mock, or the real
    /// fleet-commanding application) only needs to implement <see cref="IUavCommandHandler"/> and
    /// construct this class; no SignalR code of its own.
    /// </summary>
    public sealed class FleetClientConnection
    {
        private readonly HubConnection _connection;
        private readonly IUavCommandHandler _handler;

        public event Action<string> Connected;
        public event Action<Exception> Disconnected;
        public event Action<Exception> Reconnecting;

        public FleetClientConnection(string hubUrl, IUavCommandHandler handler)
        {
            _handler = handler;
            _connection = new HubConnectionBuilder()
                .WithUrl(hubUrl)
                .WithAutomaticReconnect()
                .Build();

            _connection.Closed += ex => { Disconnected?.Invoke(ex); return Task.CompletedTask; };
            _connection.Reconnecting += ex => { Reconnecting?.Invoke(ex); return Task.CompletedTask; };
            _connection.Reconnected += connectionId => { Connected?.Invoke(connectionId); return Task.CompletedTask; };

            RegisterHandlers();
        }

        public async Task StartAsync()
        {
            await _connection.StartAsync();
            Connected?.Invoke(_connection.ConnectionId);
        }

        public Task StopAsync()
        {
            return _connection.StopAsync();
        }

        /// <summary>
        /// Push-to-talk for UavOps.Agent's chat window: call with <c>true</c> when the operator
        /// presses the talk button (e.g. on the joystick) and <c>false</c> when they release it.
        /// The chat tab the operator used most recently starts recording on press, and on release
        /// stops, transcribes and sends what was said - same as clicking its mic button twice.
        /// Returns <c>false</c> if nothing acted on it (no chat window open on press; the mic
        /// wasn't on for a release). If this connection drops while the button is held, the host
        /// releases the mic itself.
        /// </summary>
        public Task<bool> SetPushToTalkAsync(bool pressed)
        {
            return _connection.InvokeAsync<bool>("SetPushToTalk", pressed);
        }

        private void RegisterHandlers()
        {
            _connection.On<string>("ListFleet", correlationId =>
                RunAsync(correlationId, () => _handler.ListFleet()));

            _connection.On<string, string>("GetTelemetry", (correlationId, tailNumber) =>
                RunAsync(correlationId, () => _handler.GetTelemetry(tailNumber)));

            _connection.On<string, string, string>("Navigate", (correlationId, tailNumber, location) =>
                RunAsync(correlationId, () => _handler.Navigate(tailNumber, location)));

            _connection.On<string, string, int>("SetSpeed", (correlationId, tailNumber, speedKts) =>
                RunAsync(correlationId, () => _handler.SetSpeed(tailNumber, speedKts)));

            _connection.On<string, string, int>("SetAltitude", (correlationId, tailNumber, altitudeFt) =>
                RunAsync(correlationId, () => _handler.SetAltitude(tailNumber, altitudeFt)));

            _connection.On<string, string>("ReturnToLaunch", (correlationId, tailNumber) =>
                RunAsync(correlationId, () => _handler.ReturnToLaunch(tailNumber)));

            _connection.On<string, string, string>("PointPayload", (correlationId, tailNumber, location) =>
                RunAsync(correlationId, () => _handler.PointPayload(tailNumber, location)));

            _connection.On<string, string>("ResetPayload", (correlationId, tailNumber) =>
                RunAsync(correlationId, () => _handler.ResetPayload(tailNumber)));

            _connection.On<string, string, List<Waypoint>>("UploadWaypoints", (correlationId, tailNumber, waypoints) =>
                RunAsync(correlationId, () => _handler.UploadWaypoints(tailNumber, waypoints)));

            _connection.On<string, string>("GetMissionStatus", (correlationId, tailNumber) =>
                RunAsync(correlationId, () => _handler.GetMissionStatus(tailNumber)));

            _connection.On<string, string>("GetLinkStatus", (correlationId, tailNumber) =>
                RunAsync(correlationId, () => _handler.GetLinkStatus(tailNumber)));

            _connection.On<string, string, string>("SetTrackingMode", (correlationId, tailNumber, mode) =>
                RunAsync(correlationId, () => _handler.SetTrackingMode(tailNumber, mode)));
        }

        /// <summary>
        /// Runs the handler call on a background thread (so a slow/blocking implementation
        /// doesn't stall the connection's receive loop), catches any exception it throws and
        /// reports that as a failure instead of crashing the connection, and always replies via
        /// SubmitCommandResult — success or failure.
        /// </summary>
        private Task RunAsync<T>(string correlationId, Func<CommandResult<T>> handle)
        {
            return Task.Run(async () =>
            {
                CommandResult<T> result;
                try
                {
                    result = handle();
                }
                catch (Exception ex)
                {
                    result = CommandResult<T>.Fail(ex.Message);
                }

                var resultJson = result.Success ? JsonConvert.SerializeObject(result.Value) : null;
                await _connection.InvokeAsync("SubmitCommandResult", correlationId, result.Success, result.ErrorMessage, resultJson);
            });
        }
    }
}
