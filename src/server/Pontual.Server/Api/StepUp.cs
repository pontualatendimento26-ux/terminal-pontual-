using System.Collections.Concurrent;
using Microsoft.AspNetCore.Identity;
using Pontual.Domain.Authorization;
using Pontual.Server.Data;

namespace Pontual.Server.Api;

/// <summary>
/// Aplica as exigências da matriz (docs/SECURITY.md, seção 3) nas ações que mudam algo:
/// reautenticação (a senha de novo) e justificativa (texto que vai para a auditoria).
/// Leituras (GET) não pedem nada. Quando falta algo, responde 428 com <c>need</c> dizendo o quê,
/// e o painel pergunta e reenvia.
/// </summary>
public sealed class StepUp(TimeProvider time, IPasswordHasher<Admin> hasher, LoginLockout lockout)
{
    public const string PasswordHeader = "X-Pontual-Senha";
    public const string JustificationHeader = "X-Pontual-Justificativa";
    public const string JustificationKey = "pontual.justification";

    /// <summary>Depois de confirmar a senha, a mesma sessão não é perguntada de novo por este tempo.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _confirmed = new(StringComparer.Ordinal);

    public void Enforce(HttpContext context, Admin admin, string sessionHash, Permission permission)
    {
        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        {
            return;
        }

        var requirements = PermissionMatrix.RequirementsFor(permission);
        var now = time.GetUtcNow();

        var wantsPassword = requirements.HasFlag(Requirement.Reauthentication)
            && !(_confirmed.TryGetValue(sessionHash, out var at) && now - at <= Grace);
        var password = wantsPassword ? Header(context, PasswordHeader) : null;
        var justification = requirements.HasFlag(Requirement.Justification) ? Header(context, JustificationHeader)?.Trim() : null;

        var need = new List<string>(2);
        if (wantsPassword && string.IsNullOrEmpty(password))
        {
            need.Add("senha");
        }

        if (requirements.HasFlag(Requirement.Justification) && string.IsNullOrEmpty(justification))
        {
            need.Add("justificativa");
        }

        if (need.Count > 0)
        {
            throw new ApiError(StatusCodes.Status428PreconditionRequired,
                need.Count == 2 ? "Confirme a sua senha e diga o motivo." : need[0] == "senha" ? "Confirme a sua senha." : "Diga o motivo desta ação.")
            {
                Need = string.Join(',', need),
            };
        }

        if (justification is not null)
        {
            if (justification.Length is < 5 or > 500)
            {
                throw ApiError.BadRequest("O motivo precisa ter de 5 a 500 caracteres.");
            }

            context.Items[JustificationKey] = justification;
        }

        if (wantsPassword)
        {
            lockout.ThrowIfLocked(admin.Email);
            if (password!.Length > 128 || hasher.VerifyHashedPassword(admin, admin.PasswordHash, password) == PasswordVerificationResult.Failed)
            {
                lockout.Fail(admin.Email);
                // 403 e não 401: senha errada aqui não derruba a sessão.
                throw new ApiError(StatusCodes.Status403Forbidden, "Senha incorreta.") { Need = "senha" };
            }

            lockout.Succeed(admin.Email);
            _confirmed[sessionHash] = now;
        }
    }

    /// <summary>Sessão encerrada: esquece a confirmação.</summary>
    public void Forget(string sessionHash) => _confirmed.TryRemove(sessionHash, out _);

    private static string? Header(HttpContext context, string name)
    {
        var raw = context.Request.Headers[name].ToString();
        if (raw.Length is 0 or > 2048)
        {
            return null;
        }

        try
        {
            return Uri.UnescapeDataString(raw);
        }
        catch (UriFormatException)
        {
            return null;
        }
    }
}
