using System.Collections.Concurrent;
using System.Security.Claims;
using ClinicReport.Api.Auth;
using ClinicReport.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ClinicReport.Api.Reports;

public record Viewer(string ConnectionId, int UserId, string Name, string Role, string? FieldId);

/// <summary>
/// An open report: the clinic and the report's Id in that clinic's database. Every clinic numbers its reports
/// from 1, so the Id alone would put two clinics' reports in the same room.
/// </summary>
public readonly record struct ReportRoom(string Clinic, int ReportId);

/// <summary>
/// Who has which report open, and which field they are on. Kept in memory: one API instance serves all clinics.
/// Running several instances would need a SignalR backplane and a shared store here.
/// </summary>
public class PresenceTracker
{
    private readonly ConcurrentDictionary<string, (ReportRoom Room, Viewer Viewer)> _connections = new();

    public void Join(ReportRoom room, Viewer viewer) => _connections[viewer.ConnectionId] = (room, viewer);

    public ReportRoom? Leave(string connectionId) =>
        _connections.TryRemove(connectionId, out var entry) ? entry.Room : null;

    public void Focus(string connectionId, string? fieldId)
    {
        if (_connections.TryGetValue(connectionId, out var entry))
            _connections[connectionId] = (entry.Room, entry.Viewer with { FieldId = fieldId });
    }

    public ReportRoom? RoomOf(string connectionId) =>
        _connections.TryGetValue(connectionId, out var entry) ? entry.Room : null;

    public IReadOnlyList<Viewer> ViewersOf(ReportRoom room) =>
        _connections.Values.Where(e => e.Room == room).Select(e => e.Viewer).OrderBy(v => v.Name).ToList();
}

/// <summary>
/// Live channel for an open report. Changes themselves go through the REST API (so they are validated
/// and audited in one place); the hub only carries presence and the "reportChanged" broadcasts.
/// </summary>
[Authorize(Policy = Permissions.ReportsView)]
public class ReportHub(PresenceTracker presence, ClinicReportDbContext db) : Hub
{
    /// <summary>Standalone has one database and no clinic claim.</summary>
    public const string StandaloneClinic = "standalone";

    /// <summary>The clinic whose database this person works in: the session's "clinic", or ClinicHub's own "clinic_slug".</summary>
    public static string ClinicOf(ClaimsPrincipal? user) =>
        user?.FindFirstValue(ClinicHubClaims.Clinic) ?? user?.FindFirstValue("clinic_slug") ?? StandaloneClinic;

    public static string GroupName(ReportRoom room) => $"report-{room.Clinic}-{room.ReportId}";

    public async Task JoinReport(int reportId)
    {
        var service = await db.Reports.Where(r => r.Id == reportId).Select(r => r.TemplateVersion.Template.Service).SingleOrDefaultAsync();
        if (service is null || !Context.User!.CanAccessService(service))
            throw new HubException("Report not found.");

        var room = new ReportRoom(ClinicOf(Context.User), reportId);
        if (presence.RoomOf(Context.ConnectionId) is { } previous && previous != room)
            await LeaveCurrent();

        var user = Context.User!;
        presence.Join(room, new Viewer(Context.ConnectionId, user.RequiredUserId(), user.Identity!.Name ?? "?",
            string.Join(", ", user.FindAll(ClaimTypes.Role).Select(c => c.Value)), null));
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(room));
        await BroadcastPresence(room);
    }

    public Task LeaveReport() => LeaveCurrent();

    /// <summary>Tells the others which field this person is typing in (null when none).</summary>
    public async Task Focus(string? fieldId)
    {
        if (fieldId is { Length: > 64 })
            return;
        presence.Focus(Context.ConnectionId, fieldId);
        if (presence.RoomOf(Context.ConnectionId) is { } room)
            await BroadcastPresence(room);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await LeaveCurrent();
        await base.OnDisconnectedAsync(exception);
    }

    private async Task LeaveCurrent()
    {
        if (presence.Leave(Context.ConnectionId) is { } room)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(room));
            await BroadcastPresence(room);
        }
    }

    private Task BroadcastPresence(ReportRoom room) =>
        Clients.Group(GroupName(room)).SendAsync("presence",
            presence.ViewersOf(room).Select(v => new { v.ConnectionId, v.UserId, v.Name, v.Role, v.FieldId }));
}
