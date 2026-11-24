using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Pontual.Domain.Authorization;
using Pontual.Server.Data;

namespace Pontual.Server.Api;

/// <summary>Primeiro uso, sessão e administradores (contrato, seção 4).</summary>
public static class AccountEndpoints
{
    public const string CookieName = "pontual_admin";
    public const string LoginRateLimit = "login";

    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    // Usado quando o e-mail não existe, para o tempo de resposta não revelar quais e-mails são válidos.
    private static readonly Lazy<string> DummyHash = new(() =>
        new PasswordHasher<Admin>().HashPassword(null!, Secrets.NewToken()));

    private static readonly SemaphoreSlim SetupGate = new(1, 1);
    private static readonly SemaphoreSlim RoleGate = new(1, 1);

    private sealed record SetupBody(string? Name, string? Email, string? Password);
    private sealed record LoginBody(string? Email, string? Password);
    private sealed record NewAdminBody(string? Name, string? Email, string? Password, string? Role);
    private sealed record RoleBody(string? Role);
    private sealed record PasswordBody(string? Password);
    private sealed record RegisterBody(string? Name, string? Email, string? Password, string? Role, string? AuthEmail, string? AuthPassword);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/setup/status", async (PontualDb db, CancellationToken ct) =>
            Results.Ok(new { needsSetup = !await db.Admins.AnyAsync(ct) })).PanelPublic();

        api.MapPost("/setup", SetupAsync).PanelPublic().RequireRateLimiting(LoginRateLimit);
        api.MapPost("/auth/login", LoginAsync).PanelPublic().RequireRateLimiting(LoginRateLimit);
        api.MapPost("/auth/register", RegisterAsync).PanelPublic().RequireRateLimiting(LoginRateLimit);
        api.MapPost("/auth/logout", LogoutAsync).PanelPublic();
        api.MapGet("/auth/me", (HttpContext context) => Results.Ok(MeDto.From(context.Admin()))).PanelLoggedIn();

        api.MapGet("/admins", async (PontualDb db, CancellationToken ct) =>
        {
            var admins = await db.Admins.AsNoTracking().OrderBy(a => a.CreatedAt).ToListAsync(ct);
            return Results.Ok(admins.Select(AdminDto.From));
        }).Panel(Permission.AdminsManage);

        api.MapPost("/admins", CreateAdminAsync).Panel(Permission.AdminsManage);
        api.MapDelete("/admins/{id:guid}", DeleteAdminAsync).Panel(Permission.AdminsManage);
        api.MapPost("/admins/{id:guid}/password", ChangePasswordAsync).Panel(Permission.AdminsManage);
        api.MapPost("/admins/{id:guid}/role", ChangeRoleAsync).Panel(Permission.AdminsManage);
    }

    /// <summary>Apaga outra conta (só superadministrador). A própria conta não pode ser apagada, o que garante que sempre sobra um superadministrador.</summary>
    private static async Task<IResult> DeleteAdminAsync(Guid id, HttpContext context, PontualDb db, AuditWriter audit, CancellationToken ct)
    {
        var actor = context.Admin();
        if (id == actor.Id)
        {
            throw ApiError.BadRequest("Você não pode apagar a sua própria conta.");
        }

        var admin = await db.Admins.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw ApiError.NotFound("Conta não encontrada.");

        // As sessões abertas da conta caem junto: quem estava logado com ela sai na hora.
        db.Sessions.RemoveRange(await db.Sessions.Where(s => s.AdminId == id).ToListAsync(ct));
        db.Admins.Remove(admin);

        await audit.SaveAsync(db, AuditInfo.By(actor, "admins.delete",
            $"{actor.Name} apagou a conta de {admin.Name} ({admin.Email}), que tinha a função {Roles.Label(admin.Role)}", "admin", admin.Id), cancellationToken: ct);
        return Results.NoContent();
    }

    /// <summary>Muda a função de outra conta (só superadministrador). As sessões daquela conta são encerradas.</summary>
    private static async Task<IResult> ChangeRoleAsync(Guid id, HttpContext context, PontualDb db, AuditWriter audit, CancellationToken ct)
    {
        var actor = context.Admin();
        if (id == actor.Id)
        {
            throw ApiError.BadRequest("Você não pode mudar o seu próprio cargo.");
        }

        var role = Roles.Parse((await context.ReadBodyAsync<RoleBody>()).Role);

        // Uma troca por vez: dois superadministradores não se rebaixam ao mesmo tempo, o que deixaria a Central sem nenhum.
        await RoleGate.WaitAsync(ct);
        try
        {
            var admin = await db.Admins.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw ApiError.NotFound("Conta não encontrada.");
            if (admin.Role == role)
            {
                return Results.NoContent();
            }

            if (admin.Role == Role.SuperAdmin && !await db.Admins.AnyAsync(a => a.Id != id && a.Active && a.Role == Role.SuperAdmin, ct))
            {
                throw ApiError.Conflict("A Central precisa de pelo menos um superadministrador ativo.");
            }

            var before = admin.Role;
            admin.Role = role;
            // A pessoa entra de novo já com a tela da função nova.
            db.Sessions.RemoveRange(await db.Sessions.Where(s => s.AdminId == id).ToListAsync(ct));
            await audit.SaveAsync(db, AuditInfo.By(actor, "admins.role",
                $"{actor.Name} mudou a função de {admin.Name} ({admin.Email}) de {Roles.Label(before)} para {Roles.Label(role)}", "admin", admin.Id), cancellationToken: ct);
            return Results.NoContent();
        }
        finally
        {
            RoleGate.Release();
        }
    }

    /// <summary>Define uma senha nova para qualquer conta (só superadministrador). As outras sessões daquela conta são encerradas.</summary>
    private static async Task<IResult> ChangePasswordAsync(
        Guid id, HttpContext context, PontualDb db, AuditWriter audit, IPasswordHasher<Admin> hasher, CancellationToken ct)
    {
        var actor = context.Admin();
        var body = await context.ReadBodyAsync<PasswordBody>();
        var password = Input.Password(body.Password);

        var admin = await db.Admins.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw ApiError.NotFound("Conta não encontrada.");
        admin.PasswordHash = hasher.HashPassword(admin, password);

        // Quem estava logado com a senha antiga precisa entrar de novo; a sessão de quem fez a troca continua.
        var current = context.Request.Cookies.TryGetValue(CookieName, out var token) && !string.IsNullOrEmpty(token) ? Secrets.Sha256(token) : "";
        db.Sessions.RemoveRange(await db.Sessions.Where(s => s.AdminId == id && s.TokenHash != current).ToListAsync(ct));

        var whose = admin.Id == actor.Id ? "a própria senha" : $"a senha de {admin.Name} ({admin.Email})";
        await audit.SaveAsync(db, AuditInfo.By(actor, "admins.password", $"{actor.Name} trocou {whose}", "admin", admin.Id), cancellationToken: ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SetupAsync(
        HttpContext context, PontualDb db, AuditWriter audit, IPasswordHasher<Admin> hasher, TimeProvider time, CancellationToken ct)
    {
        var body = await context.ReadBodyAsync<SetupBody>();
        var name = Input.Required(body.Name, "o nome", 80);
        var email = Input.Email(body.Email);
        var password = Input.Password(body.Password);

        // Uma chamada por vez: duas simultâneas não criam dois primeiros administradores.
        await SetupGate.WaitAsync(ct);
        try
        {
            if (await db.Admins.AnyAsync(ct))
            {
                throw ApiError.Conflict("A configuração inicial já foi feita.");
            }

            return await CreateFirstAdminAsync(db, audit, hasher, time, name, email, password, ct);
        }
        finally
        {
            SetupGate.Release();
        }
    }

    private static async Task<IResult> CreateFirstAdminAsync(
        PontualDb db, AuditWriter audit, IPasswordHasher<Admin> hasher, TimeProvider time, string name, string email, string password, CancellationToken ct)
    {

        var admin = new Admin
        {
            Id = Guid.NewGuid(),
            Name = name,
            Email = email,
            Role = Role.SuperAdmin,
            CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        admin.PasswordHash = hasher.HashPassword(admin, password);
        db.Admins.Add(admin);

        await audit.SaveAsync(db, AuditInfo.By(admin, "setup", $"{admin.Name} fez a configuração inicial e é o primeiro superadministrador", "admin", admin.Id), cancellationToken: ct);
        return Results.NoContent();
    }

    private static async Task<IResult> LoginAsync(
        HttpContext context, PontualDb db, AuditWriter audit, IPasswordHasher<Admin> hasher, TimeProvider time, CancellationToken ct)
    {
        var body = await context.ReadBodyAsync<LoginBody>();
        var email = Input.Clean(body.Email).ToLowerInvariant();
        var password = body.Password is { Length: <= 128 } ? body.Password : "";

        var admin = email.Length is > 0 and <= 254
            ? await db.Admins.FirstOrDefaultAsync(a => a.Email == email, ct)
            : null;

        var result = hasher.VerifyHashedPassword(admin!, admin?.PasswordHash ?? DummyHash.Value, password);
        if (admin is null || !admin.Active || result == PasswordVerificationResult.Failed)
        {
            throw new ApiError(StatusCodes.Status401Unauthorized, "E-mail ou senha incorretos.");
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            admin.PasswordHash = hasher.HashPassword(admin, password);
        }

        var now = time.GetUtcNow().UtcDateTime;
        await db.Sessions.Where(s => s.ExpiresAt < now).ExecuteDeleteAsync(ct);

        var token = Secrets.NewToken();
        db.Sessions.Add(new AdminSession
        {
            Id = Guid.NewGuid(),
            TokenHash = Secrets.Sha256(token),
            AdminId = admin.Id,
            ExpiresAt = now + SessionLifetime,
        });

        await audit.SaveAsync(db, AuditInfo.By(admin, "auth.login", $"{admin.Name} entrou no painel", "admin", admin.Id), cancellationToken: ct);

        context.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            Path = "/",
            MaxAge = SessionLifetime,
            IsEssential = true,
        });

        return Results.Ok(new SessionDto(admin.Id, admin.Name, admin.Email, Roles.Code(admin.Role)));
    }

    private static async Task<IResult> LogoutAsync(HttpContext context, PontualDb db, AuditWriter audit, CancellationToken ct)
    {
        if (context.Items[HttpContextExtensions.AdminKey] is Admin admin
            && context.Request.Cookies.TryGetValue(CookieName, out var token) && !string.IsNullOrEmpty(token))
        {
            var hash = Secrets.Sha256(token);
            db.Sessions.RemoveRange(await db.Sessions.Where(s => s.TokenHash == hash).ToListAsync(ct));
            await audit.SaveAsync(db, AuditInfo.By(admin, "auth.logout", $"{admin.Name} saiu do painel", "admin", admin.Id), cancellationToken: ct);
        }

        context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/" });
        return Results.NoContent();
    }

    private static async Task<IResult> CreateAdminAsync(
        HttpContext context, PontualDb db, AuditWriter audit, IPasswordHasher<Admin> hasher, TimeProvider time, CancellationToken ct)
    {
        var actor = context.Admin();
        var body = await context.ReadBodyAsync<NewAdminBody>();
        var name = Input.Required(body.Name, "o nome", 80);
        var email = Input.Email(body.Email);
        var password = Input.Password(body.Password);
        var role = Roles.Parse(body.Role);

        return Results.Ok(AdminDto.From(await AddAdminAsync(db, audit, hasher, time, actor, name, email, password, role, "", ct)));
    }

    /// <summary>
    /// Conta criada pela tela de login, sem sessão: quem autoriza é um superadministrador, que informa
    /// o próprio e-mail e senha no mesmo pedido. Só cria as funções administrador e superadministrador.
    /// </summary>
    private static async Task<IResult> RegisterAsync(
        HttpContext context, PontualDb db, AuditWriter audit, IPasswordHasher<Admin> hasher, TimeProvider time, CancellationToken ct)
    {
        var body = await context.ReadBodyAsync<RegisterBody>();
        var name = Input.Required(body.Name, "o nome", 80);
        var email = Input.Email(body.Email);
        var password = Input.Password(body.Password);

        // Pela tela de login, sem sessão, só nasce administrador. Superadministrador novo só
        // é criado por um superadministrador já logado, na seção Administradores.
        var role = body.Role switch
        {
            null or "" or "admin" => Role.Admin,
            "superadmin" => throw ApiError.Forbidden("Superadministrador só pode ser criado de dentro do painel, na seção Administradores."),
            _ => throw ApiError.BadRequest("Tipo de conta inválido."),
        };

        var authEmail = Input.Clean(body.AuthEmail).ToLowerInvariant();
        var authPassword = body.AuthPassword is { Length: <= 128 } ? body.AuthPassword : "";

        var authorizer = authEmail.Length is > 0 and <= 254
            ? await db.Admins.FirstOrDefaultAsync(a => a.Email == authEmail, ct)
            : null;

        var result = hasher.VerifyHashedPassword(authorizer!, authorizer?.PasswordHash ?? DummyHash.Value, authPassword);
        if (authorizer is null || !authorizer.Active || result == PasswordVerificationResult.Failed)
        {
            throw new ApiError(StatusCodes.Status401Unauthorized, "E-mail ou senha do superadministrador incorretos.");
        }

        if (!PermissionMatrix.IsAllowed(authorizer.Role, Permission.AdminsManage))
        {
            throw ApiError.Forbidden("Só um superadministrador pode autorizar a criação de contas.");
        }

        await AddAdminAsync(db, audit, hasher, time, authorizer, name, email, password, role, " pela tela de login", ct);
        return Results.NoContent();
    }

    private static async Task<Admin> AddAdminAsync(
        PontualDb db, AuditWriter audit, IPasswordHasher<Admin> hasher, TimeProvider time,
        Admin actor, string name, string email, string password, Role role, string how, CancellationToken ct)
    {
        if (await db.Admins.AnyAsync(a => a.Email == email, ct))
        {
            throw ApiError.Conflict("Já existe um administrador com este e-mail.");
        }

        var admin = new Admin
        {
            Id = Guid.NewGuid(),
            Name = name,
            Email = email,
            Role = role,
            CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        admin.PasswordHash = hasher.HashPassword(admin, password);
        db.Admins.Add(admin);

        await audit.SaveAsync(db, AuditInfo.By(actor, "admins.create",
            $"{actor.Name} criou o administrador {admin.Name} ({admin.Email}) com a função {Roles.Label(role)}{how}", "admin", admin.Id), cancellationToken: ct);

        return admin;
    }
}
