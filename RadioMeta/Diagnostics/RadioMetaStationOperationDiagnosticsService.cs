using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace RadioMeta;

[Route("/RadioMeta/Diagnostics/StationOperationResult", "POST")]
[Authenticated(Roles = "Admin")]
public sealed class ReportRadioMetaStationOperationResult : IReturn<ReportRadioMetaStationOperationResultResponse>
{
    public string OperationId { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string StationName { get; set; } = string.Empty;
    public string FailureCode { get; set; } = string.Empty;
    public string LibraryRefreshStatus { get; set; } = string.Empty;
    public string StationListRefreshStatus { get; set; } = string.Empty;
}

public sealed class ReportRadioMetaStationOperationResultResponse
{
    public bool Success { get; set; }
    public string Error { get; set; } = string.Empty;
}

/// <summary>
/// Receives only bounded status codes from the admin dashboard after its station
/// operation and refresh attempts. It does not accept paths, URLs, IDs from Emby,
/// tokens, or free-form error text.
/// </summary>
[Authenticated(Roles = "Admin")]
public sealed class RadioMetaStationOperationDiagnosticsService : IService
{
    public object Post(ReportRadioMetaStationOperationResult request)
    {
        var plugin = RadioMetaPlugin.Instance;
        if (plugin is null)
            return new ReportRadioMetaStationOperationResultResponse { Error = "Radio Meta is not initialized." };

        if (!string.IsNullOrWhiteSpace(request.OperationId))
        {
            var updated = plugin.StationOperationEvents.UpdateDashboardResults(
                request.OperationId,
                request.LibraryRefreshStatus,
                request.StationListRefreshStatus);
            return new ReportRadioMetaStationOperationResultResponse
            {
                Success = updated,
                Error = updated ? string.Empty : "The operation record was not found or a supplied status value was not recognized."
            };
        }

        // Failed/rejected requests may not have an operation ID because the file
        // operation never returned its success response. Store only the operation
        // label, station display name and one predefined failure category.
        if (string.IsNullOrWhiteSpace(request.FailureCode))
            return new ReportRadioMetaStationOperationResultResponse { Error = "An operation ID or predefined failure code is required." };

        plugin.StationOperationEvents.RecordClientFailure(
            request.Operation,
            request.StationName,
            request.FailureCode);
        return new ReportRadioMetaStationOperationResultResponse { Success = true };
    }
}
