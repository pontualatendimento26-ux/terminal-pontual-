using System.Collections.Concurrent;

namespace Pontual.Server.Api;

/// <summary>
/// Bloqueio por conta: depois de <see cref="MaxFailures"/> senhas erradas seguidas para o mesmo e-mail,
/// aquele e-mail fica <see cref="LockFor"/> sem poder entrar, venha o pedido de qualquer PC.
/// Vale para o login e para a senha do superadministrador que autoriza "Adicionar conta".
/// Fica em memória: reiniciar a Central zera a contagem, o que só libera antes da hora.
/// </summary>
public sealed class LoginLockout(TimeProvider time)
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan LockFor = TimeSpan.FromMinutes(15);

    private sealed record State(int Failures, DateTimeOffset? LockedUntil);

    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Recusa o pedido se o e-mail estiver bloqueado.</summary>
    public void ThrowIfLocked(string email)
    {
        if (email.Length == 0 || !_states.TryGetValue(email, out var state) || state.LockedUntil is not { } until)
        {
            return;
        }

        var left = until - time.GetUtcNow();
        if (left <= TimeSpan.Zero)
        {
            _states.TryRemove(email, out _);
            return;
        }

        var minutes = Math.Max(1, (int)Math.Ceiling(left.TotalMinutes));
        throw new ApiError(StatusCodes.Status429TooManyRequests,
            $"Muitas senhas erradas para esta conta. Tente de novo em {minutes} minuto(s).");
    }

    /// <summary>Conta uma senha errada. Devolve true quando esta falha acabou de bloquear a conta.</summary>
    public bool Fail(string email)
    {
        if (email.Length == 0)
        {
            return false;
        }

        lock (_gate)
        {
            var now = time.GetUtcNow();
            _states.TryGetValue(email, out var old);
            if (old?.LockedUntil is { } until && until > now)
            {
                return false;
            }

            var failures = (old is { LockedUntil: null } ? old.Failures : 0) + 1;
            var locked = failures >= MaxFailures;
            _states[email] = new State(failures, locked ? now + LockFor : null);
            return locked;
        }
    }

    public void Succeed(string email) => _states.TryRemove(email, out _);
}
