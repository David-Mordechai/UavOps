namespace UavOps.Agent.Contracts;

/// <summary>
/// Applies a <c>disabled</c> change to a <see cref="ServiceConfigEntry"/> — shared by the Real and
/// Fake <c>IWatchdogConfigService</c> implementations so behavior can't drift between them.
/// <c>disabled</c> is the single on/off toggle chat exposes (see
/// <c>IWatchdogConfigService.AddConfiguredService</c>'s doc comment for why); the legacy
/// <see cref="ServiceConfigEntry.Enabled"/> field is never set by these tools, and is explicitly
/// cleared whenever <c>disabled</c> is touched, so an entry can't end up with the two implying
/// different things (e.g. a hand-authored <c>enabled: true</c> silently contradicting a later
/// chat-driven <c>disabled: true</c>).
/// </summary>
public static class ServiceConfigEntryMutation
{
    public static void ApplyDisabled(ServiceConfigEntry entry, bool? disabled)
    {
        if (disabled is null)
        {
            return;
        }

        entry.Disabled = disabled;
        entry.Enabled = null;
    }
}
