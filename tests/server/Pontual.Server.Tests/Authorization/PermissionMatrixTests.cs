using Pontual.Domain.Authorization;

namespace Pontual.Server.Tests.Authorization;

public class PermissionMatrixTests
{
    private const Requirement R = Requirement.Reauthentication;
    private const Requirement J = Requirement.Justification;
    private const Requirement P = Requirement.Deadline;
    private const Requirement TwoA = Requirement.TwoAdministrators;

    private sealed record Row(string Code, bool Auditor, bool Support, bool Admin, bool SuperAdmin, Requirement Requirements);

    // Espelho, linha por linha, da tabela em docs/SECURITY.md, seção 3.
    private static readonly Row[] Document =
    [
        new("devices.read",             true,  true,  true,  true,  Requirement.None),
        new("devices.manage",           false, false, true,  true,  Requirement.None),
        new("devices.revoke",           false, false, true,  true,  R | J),
        new("enrollment.create",        false, false, true,  true,  R),
        new("sites.manage",             false, false, true,  true,  Requirement.None),
        new("usage.read",               true,  true,  true,  true,  Requirement.None),
        new("usage.read.individual",    true,  false, true,  true,  Requirement.AuditedRead),
        new("screen.control",           false, false, true,  true,  Requirement.AuditedRead),
        new("alerts.read",              true,  true,  true,  true,  Requirement.None),
        new("alerts.handle",            false, true,  true,  true,  Requirement.None),
        new("alert-rules.manage",       false, false, true,  true,  R | J),
        new("attendants.read",          true,  true,  true,  true,  Requirement.None),
        new("attendants.manage",        false, false, true,  true,  Requirement.None),
        new("apps.read",                true,  true,  true,  true,  Requirement.None),
        new("apps.websites.manage",     false, false, true,  true,  Requirement.None),
        new("apps.executables.propose", false, false, true,  true,  R | J),
        new("apps.executables.approve", false, false, true,  true,  R | TwoA),
        new("config.read",              true,  true,  true,  true,  Requirement.None),
        new("config.edit",              false, false, true,  true,  Requirement.None),
        new("config.publish",           false, false, true,  true,  R | J),
        new("maintenance.manage",       false, false, true,  true,  R | J | P),
        new("support.request",          false, true,  true,  true,  R | J | P),
        new("support.formal.authorize", false, false, true,  true,  R | J | TwoA),
        new("support.read",             true,  true,  true,  true,  Requirement.None),
        new("updates.read",             true,  true,  true,  true,  Requirement.None),
        new("updates.release",          false, false, true,  true,  R | J),
        new("layouts.manage",           false, false, true,  true,  Requirement.None),
        new("audit.read",               true,  false, true,  true,  Requirement.None),
        new("subject.export",           false, false, true,  true,  R | J),
        new("admins.manage",            false, false, false, true,  R | J),
        new("keys.manage",              false, false, false, true,  R | J),
    ];

    public static TheoryData<string, Role, bool> AccessCases()
    {
        var data = new TheoryData<string, Role, bool>();
        foreach (var row in Document)
        {
            data.Add(row.Code, Role.Auditor, row.Auditor);
            data.Add(row.Code, Role.Support, row.Support);
            data.Add(row.Code, Role.Admin, row.Admin);
            data.Add(row.Code, Role.SuperAdmin, row.SuperAdmin);
        }

        return data;
    }

    public static TheoryData<string, Requirement> RequirementCases()
    {
        var data = new TheoryData<string, Requirement>();
        foreach (var row in Document)
        {
            data.Add(row.Code, row.Requirements);
        }

        return data;
    }

    private static Permission Parse(string code)
    {
        Assert.True(Permission.TryParse(code, out var permission), $"Código desconhecido: {code}");
        return permission!;
    }

    [Theory]
    [MemberData(nameof(AccessCases))]
    public void IsAllowed_matches_the_document(string code, Role role, bool expected)
    {
        Assert.Equal(expected, PermissionMatrix.IsAllowed(role, Parse(code)));
    }

    [Theory]
    [MemberData(nameof(RequirementCases))]
    public void RequirementsFor_matches_the_document(string code, Requirement expected)
    {
        Assert.Equal(expected, PermissionMatrix.RequirementsFor(Parse(code)));
    }

    [Fact]
    public void The_mirror_table_has_124_access_cases()
    {
        Assert.Equal(31, Document.Length);
        Assert.Equal(124, AccessCases().Count);
    }

    [Fact]
    public void Permissions_are_exactly_the_31_codes_of_the_document()
    {
        var expected = Document.Select(row => row.Code).Order(StringComparer.Ordinal);
        var actual = PermissionMatrix.Permissions.Select(permission => permission.Code).Order(StringComparer.Ordinal);

        Assert.Equal(expected, actual);
        Assert.Equal(31, PermissionMatrix.Permissions.Count);
    }

    [Fact]
    public void Permission_code_round_trips_and_is_case_sensitive()
    {
        Assert.Equal("apps.executables.approve", Permission.AppsExecutablesApprove.Code);
        Assert.Equal("apps.executables.approve", Permission.AppsExecutablesApprove.ToString());
        Assert.Same(Permission.AppsExecutablesApprove, Parse("apps.executables.approve"));
        Assert.False(Permission.TryParse("Devices.Read", out _));
        Assert.False(Permission.TryParse("devices.delete", out _));
        Assert.False(Permission.TryParse(null, out _));
    }

    [Fact]
    public void Support_cannot_read_the_audit_log()
    {
        Assert.False(PermissionMatrix.IsAllowed(Role.Support, Permission.AuditRead));
    }

    [Fact]
    public void Auditor_reads_but_does_not_act()
    {
        Assert.True(PermissionMatrix.IsAllowed(Role.Auditor, Permission.AuditRead));
        Assert.True(PermissionMatrix.IsAllowed(Role.Auditor, Permission.UsageReadIndividual));
        Assert.False(PermissionMatrix.IsAllowed(Role.Auditor, Permission.AlertsHandle));
    }

    [Fact]
    public void Support_handles_alerts_but_does_not_read_individual_usage()
    {
        Assert.True(PermissionMatrix.IsAllowed(Role.Support, Permission.AlertsHandle));
        Assert.True(PermissionMatrix.IsAllowed(Role.Support, Permission.SupportRequest));
        Assert.False(PermissionMatrix.IsAllowed(Role.Support, Permission.UsageReadIndividual));
    }

    [Theory]
    [InlineData(Role.Auditor, false)]
    [InlineData(Role.Support, false)]
    [InlineData(Role.Admin, false)]
    [InlineData(Role.SuperAdmin, true)]
    public void Only_SuperAdmin_manages_admins_and_keys(Role role, bool expected)
    {
        Assert.Equal(expected, PermissionMatrix.IsAllowed(role, Permission.AdminsManage));
        Assert.Equal(expected, PermissionMatrix.IsAllowed(role, Permission.KeysManage));
    }

    [Fact]
    public void Approving_an_executable_requires_two_administrators()
    {
        var requirements = PermissionMatrix.RequirementsFor(Permission.AppsExecutablesApprove);

        Assert.True(requirements.HasFlag(Requirement.TwoAdministrators));
        Assert.Equal(Requirement.Reauthentication | Requirement.TwoAdministrators, requirements);
    }

    [Fact]
    public void Maintenance_requires_reauthentication_justification_and_deadline()
    {
        Assert.Equal(
            Requirement.Reauthentication | Requirement.Justification | Requirement.Deadline,
            PermissionMatrix.RequirementsFor(Permission.MaintenanceManage));
    }

    [Fact]
    public void Only_individual_usage_and_screen_control_are_marked_as_audited_read()
    {
        var audited = PermissionMatrix.Permissions
            .Where(permission => PermissionMatrix.RequirementsFor(permission).HasFlag(Requirement.AuditedRead));

        Assert.Equal([Permission.UsageReadIndividual, Permission.ScreenControl], audited);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(35)] // 1 << 35 em int equivale a 1 << 3: não pode virar SuperAdmin por acidente.
    [InlineData(99)]
    public void An_undefined_role_is_denied_everything(int rawRole)
    {
        Assert.All(
            PermissionMatrix.Permissions,
            permission => Assert.False(PermissionMatrix.IsAllowed((Role)rawRole, permission)));
    }

    [Fact]
    public void Requirement_flags_do_not_overlap()
    {
        Requirement[] flags =
        [
            Requirement.Reauthentication,
            Requirement.Justification,
            Requirement.Deadline,
            Requirement.TwoAdministrators,
            Requirement.AuditedRead,
        ];

        Assert.Equal(0, (int)Requirement.None);
        Assert.Equal(31, flags.Aggregate(0, (acc, flag) => acc | (int)flag));
        Assert.All(flags, flag => Assert.True(int.IsPow2((int)flag)));
    }
}
