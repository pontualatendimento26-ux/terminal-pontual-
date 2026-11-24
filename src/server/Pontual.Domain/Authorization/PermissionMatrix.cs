using System.Collections.Frozen;
using static Pontual.Domain.Authorization.Requirement;

namespace Pontual.Domain.Authorization;

/// <summary>
/// Transcrição de docs/SECURITY.md, seção 3 — a única definição de autorização do sistema.
/// Não há hierarquia entre funções: vale apenas o que está marcado em cada linha.
/// </summary>
public static class PermissionMatrix
{
    [Flags]
    private enum Grants
    {
        None = 0,
        Auditor = 1 << 0,
        Support = 1 << 1,
        Admin = 1 << 2,
        SuperAdmin = 1 << 3,
    }

    private const Grants Everyone = Grants.Auditor | Grants.Support | Grants.Admin | Grants.SuperAdmin;
    private const Grants Administrators = Grants.Admin | Grants.SuperAdmin;

    private readonly record struct Rule(Grants Grants, Requirement Requirements);

    // Mesma ordem das linhas do documento, para a conferência ser visual.
    private static readonly (Permission Permission, Rule Rule)[] Rows =
    [
        (Permission.DevicesRead,            new(Everyone, None)),
        (Permission.DevicesManage,          new(Administrators, None)),
        (Permission.DevicesRevoke,          new(Administrators, Reauthentication | Justification)),
        (Permission.EnrollmentCreate,       new(Administrators, Reauthentication)),
        (Permission.SitesManage,            new(Administrators, None)),
        (Permission.UsageRead,              new(Everyone, None)),
        (Permission.UsageReadIndividual,    new(Grants.Auditor | Administrators, AuditedRead)),
        (Permission.ScreenControl,          new(Administrators, AuditedRead)),
        (Permission.AlertsRead,             new(Everyone, None)),
        (Permission.AlertsHandle,           new(Grants.Support | Administrators, None)),
        (Permission.AlertRulesManage,       new(Administrators, Reauthentication | Justification)),
        (Permission.AttendantsRead,         new(Everyone, None)),
        (Permission.AttendantsManage,       new(Administrators, None)),
        (Permission.AppsRead,               new(Everyone, None)),
        (Permission.AppsWebsitesManage,     new(Administrators, None)),
        (Permission.AppsExecutablesPropose, new(Administrators, Reauthentication | Justification)),
        (Permission.AppsExecutablesApprove, new(Administrators, Reauthentication | TwoAdministrators)),
        (Permission.ConfigRead,             new(Everyone, None)),
        (Permission.ConfigEdit,             new(Administrators, None)),
        (Permission.ConfigPublish,          new(Administrators, Reauthentication | Justification)),
        (Permission.MaintenanceManage,      new(Administrators, Reauthentication | Justification | Deadline)),
        (Permission.SupportRequest,         new(Grants.Support | Administrators, Reauthentication | Justification | Deadline)),
        (Permission.SupportFormalAuthorize, new(Administrators, Reauthentication | Justification | TwoAdministrators)),
        (Permission.SupportRead,            new(Everyone, None)),
        (Permission.UpdatesRead,            new(Everyone, None)),
        (Permission.UpdatesRelease,         new(Administrators, Reauthentication | Justification)),
        (Permission.LayoutsManage,          new(Administrators, None)),
        (Permission.AuditRead,              new(Grants.Auditor | Administrators, None)),
        (Permission.SubjectExport,          new(Administrators, Reauthentication | Justification)),
        (Permission.AdminsManage,           new(Grants.SuperAdmin, Reauthentication | Justification)),
        (Permission.KeysManage,             new(Grants.SuperAdmin, Reauthentication | Justification)),
    ];

    private static readonly FrozenDictionary<Permission, Rule> Rules =
        Rows.ToFrozenDictionary(row => row.Permission, row => row.Rule);

    /// <summary>Todas as permissões, na ordem do documento.</summary>
    public static IReadOnlyList<Permission> Permissions { get; } =
        Array.AsReadOnly(Array.ConvertAll(Rows, row => row.Permission));

    public static bool IsAllowed(Role role, Permission permission)
    {
        ArgumentNullException.ThrowIfNull(permission);

        return (Rules[permission].Grants & GrantOf(role)) != 0;
    }

    public static Requirement RequirementsFor(Permission permission)
    {
        ArgumentNullException.ThrowIfNull(permission);

        return Rules[permission].Requirements;
    }

    // Mapeamento explícito em vez de deslocamento de bits: um valor de Role fora do enum
    // (vindo de um cast ou de dado corrompido) não pode coincidir com uma função válida.
    private static Grants GrantOf(Role role) => role switch
    {
        Role.Auditor => Grants.Auditor,
        Role.Support => Grants.Support,
        Role.Admin => Grants.Admin,
        Role.SuperAdmin => Grants.SuperAdmin,
        _ => Grants.None,
    };
}
