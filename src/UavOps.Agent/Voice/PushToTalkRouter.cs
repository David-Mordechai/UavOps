using Microsoft.AspNetCore.SignalR;
using UavOps.Agent.Hubs;

namespace UavOps.Agent.Voice;

/// <summary>
/// Routes an external push-to-talk button (the fleet app's joystick, via
/// <c>UavOps.FleetClient</c>'s <c>SetPushToTalkAsync</c> → <see cref="ChatHub.SetPushToTalk"/>) to
/// exactly one chat browser tab, as a <c>SetMicActive(bool)</c> event that tab's mic button then
/// acts on - press starts recording, release stops and sends it, same as clicking the button.
///
/// Only one tab, never a broadcast: every tab that received "mic on" would record and send the
/// same spoken command, executing it once per open tab. The target is the tab the operator used
/// most recently, by the tab's own reported last-activity time (<see cref="ReportActivity"/>) -
/// not by arrival order here, so after a host restart every tab reconnecting at once can't
/// reshuffle which one counts as most recent.
///
/// A release always goes to the tab that got the press, even if the operator has since used a
/// different tab - otherwise the first tab would be left recording forever. For the same reason,
/// the fleet client dropping its connection while the button is held releases the mic
/// (<see cref="ConnectionClosedAsync"/>).
/// </summary>
public sealed class PushToTalkRouter(IHubContext<ChatHub> hubContext, ILogger<PushToTalkRouter> logger)
{
    public const string MicActiveEvent = "SetMicActive";

    private readonly object _lock = new();
    private readonly Dictionary<string, long> _chatTabs = new();
    private string? _micOnTabConnectionId;
    private string? _pressedByConnectionId;

    /// <summary>Called by a chat tab when it connects/reconnects and whenever the operator clicks
    /// or types in it. <paramref name="lastActivityUnixMs"/> is the tab's own clock reading of its
    /// last real use.</summary>
    public void ReportActivity(string connectionId, long lastActivityUnixMs)
    {
        lock (_lock)
        {
            _chatTabs[connectionId] = lastActivityUnixMs;
        }
    }

    /// <summary>Returns whether a chat tab was there to receive it - <c>false</c> tells the
    /// caller no chat window is open (press) or nothing was recording (release).</summary>
    public async Task<bool> SetPushToTalkAsync(string callerConnectionId, bool pressed)
    {
        string? target;
        lock (_lock)
        {
            if (pressed)
            {
                target = _chatTabs.Count == 0 ? null : _chatTabs.MaxBy(t => t.Value).Key;
                _micOnTabConnectionId = target;
                _pressedByConnectionId = target is null ? null : callerConnectionId;
            }
            else
            {
                target = _micOnTabConnectionId;
                _micOnTabConnectionId = null;
                _pressedByConnectionId = null;
            }
        }

        if (target is null)
        {
            logger.LogWarning("Push-to-talk {Action} ignored: {Reason}", pressed ? "press" : "release",
                pressed ? "no chat tab is connected" : "the mic was not on");
            return false;
        }

        logger.LogInformation("Push-to-talk {Action} -> chat connection {ConnectionId}", pressed ? "press" : "release", target);
        await hubContext.Clients.Client(target).SendAsync(MicActiveEvent, pressed);
        return true;
    }

    /// <summary>Called for every hub disconnect, whichever role the connection had.</summary>
    public async Task ConnectionClosedAsync(string connectionId)
    {
        bool releaseHeldButton;
        lock (_lock)
        {
            _chatTabs.Remove(connectionId);
            if (_micOnTabConnectionId == connectionId)
            {
                // That tab is gone, and with it its recording - nothing left to release.
                _micOnTabConnectionId = null;
                _pressedByConnectionId = null;
            }
            releaseHeldButton = _pressedByConnectionId == connectionId;
        }

        if (releaseHeldButton)
        {
            logger.LogWarning("Push-to-talk caller {ConnectionId} disconnected with the button held - releasing the mic", connectionId);
            await SetPushToTalkAsync(connectionId, pressed: false);
        }
    }
}
