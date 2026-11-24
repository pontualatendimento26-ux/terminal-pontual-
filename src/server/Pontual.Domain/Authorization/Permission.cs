using System.Diagnostics.CodeAnalysis;

namespace Pontual.Domain.Authorization;

/// <summary>
/// Código de permissão de docs/SECURITY.md, seção 3. O conjunto é fechado: só existem as instâncias declaradas aqui.
/// </summary>
public sealed class Permission
{
    // Precisa vir antes dos campos abaixo: inicializadores estáticos rodam na ordem do texto.
    private static readonly Dictionary<string, Permission> ByCode = new(StringComparer.Ordinal);

    public static readonly Permission DevicesRead = new("devices.read");
    public static readonly Permission DevicesManage = new("devices.manage");
    public static readonly Permission DevicesRevoke = new("devices.revoke");
    public static readonly Permission EnrollmentCreate = new("enrollment.create");
    public static readonly Permission SitesManage = new("sites.manage");
    public static readonly Permission UsageRead = new("usage.read");
    public static readonly Permission UsageReadIndividual = new("usage.read.individual");
    public static readonly Permission ScreenControl = new("screen.control");
    public static readonly Permission AlertsRead = new("alerts.read");
    public static readonly Permission AlertsHandle = new("alerts.handle");
    public static readonly Permission AlertRulesManage = new("alert-rules.manage");
    public static readonly Permission AttendantsRead = new("attendants.read");
    public static readonly Permission AttendantsManage = new("attendants.manage");
    public static readonly Permission AppsRead = new("apps.read");
    public static readonly Permission AppsWebsitesManage = new("apps.websites.manage");
    public static readonly Permission AppsExecutablesPropose = new("apps.executables.propose");
    public static readonly Permission AppsExecutablesApprove = new("apps.executables.approve");
    public static readonly Permission ConfigRead = new("config.read");
    public static readonly Permission ConfigEdit = new("config.edit");
    public static readonly Permission ConfigPublish = new("config.publish");
    public static readonly Permission MaintenanceManage = new("maintenance.manage");
    public static readonly Permission SupportRequest = new("support.request");
    public static readonly Permission SupportFormalAuthorize = new("support.formal.authorize");
    public static readonly Permission SupportRead = new("support.read");
    public static readonly Permission UpdatesRead = new("updates.read");
    public static readonly Permission UpdatesRelease = new("updates.release");
    public static readonly Permission LayoutsManage = new("layouts.manage");
    public static readonly Permission AuditRead = new("audit.read");
    public static readonly Permission SubjectExport = new("subject.export");
    public static readonly Permission AdminsManage = new("admins.manage");
    public static readonly Permission KeysManage = new("keys.manage");

    private Permission(string code)
    {
        Code = code;
        ByCode.Add(code, this);
    }

    /// <summary>Código exatamente como aparece no documento e na API.</summary>
    public string Code { get; }

    /// <summary>Busca exata; diferença de maiúsculas e minúsculas não é aceita.</summary>
    public static bool TryParse([NotNullWhen(true)] string? code, [NotNullWhen(true)] out Permission? permission)
    {
        if (code is null)
        {
            permission = null;
            return false;
        }

        return ByCode.TryGetValue(code, out permission);
    }

    public override string ToString() => Code;
}
