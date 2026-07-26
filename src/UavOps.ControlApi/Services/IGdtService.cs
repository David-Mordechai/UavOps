using UavOps.ControlApi.Models;

namespace UavOps.ControlApi.Services;

/// <summary>
/// Ground Data Terminal (the ground-side antenna/datalink equipment) status and control, per
/// UAV. Placeholder mock tools until real GDT tool definitions are available — the seam a real
/// implementation replaces later.
/// </summary>
public interface IGdtService
{
    (bool Found, GdtLinkStatus? Status) GetLinkStatus(string tailNumber);
    (bool Found, GdtLinkStatus? Status, string? Error) SetTrackingMode(string tailNumber, string mode);
}
