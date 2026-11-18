using Microsoft.EntityFrameworkCore;
using Pontual.Domain.Authorization;
using Pontual.Server.Data;

namespace Pontual.Server.Api;

public sealed record AdminDto(Guid Id, string Name, string Email, string Role, bool Active)
{
    public static AdminDto From(Admin admin) => new(admin.Id, admin.Name, admin.Email, Roles.Code(admin.Role), admin.Active);
}

public sealed record SessionDto(Guid Id, string Name, string Email, string Role);

public sealed record MeDto(Guid Id, string Name, string Email, string Role, IReadOnlyList<string> Permissions)
{
    public static MeDto From(Admin admin) => new(
        admin.Id, admin.Name, admin.Email, Roles.Code(admin.Role),
        [.. PermissionMatrix.Permissions.Where(p => PermissionMatrix.IsAllowed(admin.Role, p)).Select(p => p.Code)]);
}

public sealed record MaintenanceDto(string Reason, DateTime Until)
{
    public static MaintenanceDto? From(Device device, DateTime now) =>
        device.InMaintenance(now) ? new(device.MaintenanceReason ?? "", device.MaintenanceUntil!.Value) : null;
}

public sealed record DeviceDto(
    Guid Id,
    string Hostname,
    string? AttendantName,
    string State,
    string? Windows,
    IReadOnlyList<string> AppsInUse,
    DateTime? SessionStartedAt,
    DateTime? LastSeenAt,
    int OpenAlerts,
    MaintenanceDto? Maintenance,
    bool ObservationMode)
{
    /// <summary>Sem sinal há mais de 45 segundos = fora do ar.</summary>
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(45);

    public static DeviceDto From(Device device, int openAlerts, DateTime now)
    {
        var connected = device.LastSeenAt is { } seen && now - seen <= OfflineAfter;
        var state =
            !device.Approved ? "pending"
            : device.InMaintenance(now) ? "maintenance"
            : !connected ? "offline"
            : openAlerts > 0 ? "alert"
            : "online";

        return new DeviceDto(
            device.Id,
            device.Hostname,
            device.AttendantName,
            state,
            device.Windows,
            // O que estava aberto no último sinal não vale mais se a máquina saiu do ar.
            connected ? device.AppsInUse : [],
            connected ? device.SessionStartedAt : null,
            device.LastSeenAt,
            openAlerts,
            MaintenanceDto.From(device, now),
            device.ObservationMode);
    }

    public static async Task<DeviceDto> LoadAsync(PontualDb db, Device device, DateTime now, CancellationToken ct)
    {
        var open = await db.Alerts.CountAsync(a => a.DeviceId == device.Id && a.ResolvedAt == null, ct);
        return From(device, open, now);
    }
}

public sealed record ProposalDto(string By, DateTime At, string Reason);

public sealed record ItemDto(
    Guid Id,
    string Name,
    string Kind,
    string Target,
    string Group,
    IReadOnlyList<string> ExtraHosts,
    bool Enabled,
    string Status,
    IReadOnlyList<Guid>? Audience,
    ProposalDto? Proposal)
{
    public static ItemDto From(Item item) => new(
        item.Id,
        item.Name,
        item.Kind,
        item.Target,
        item.Group,
        item.ExtraHosts,
        item.Enabled,
        item.Status,
        item.Audience,
        item.Pending
            ? new ProposalDto(item.ProposedByName ?? "", item.ProposedAt ?? item.CreatedAt, item.ProposalReason ?? "")
            : null);
}

public sealed record ConfigItemDto(Guid Id, string Name, string Kind, string Target, string Group, IReadOnlyList<string> Hosts);

public sealed record ConfigDto(long Version, string? AttendantName, bool ObservationMode, IReadOnlyList<ConfigItemDto> Items);

/// <param name="Assistant">A assistente de IA está ligada para esta máquina (o terminal mostra o botão flutuante).</param>
public sealed record HeartbeatDto(string State, long ConfigVersion, string? AttendantName, MaintenanceDto? Maintenance, bool ObservationMode, bool Assistant);
