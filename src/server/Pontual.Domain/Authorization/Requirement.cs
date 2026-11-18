namespace Pontual.Domain.Authorization;

/// <summary>Exigências da coluna "Exigências" de docs/SECURITY.md, seção 3.</summary>
[Flags]
public enum Requirement
{
    None = 0,

    /// <summary>R — reautenticação.</summary>
    Reauthentication = 1 << 0,

    /// <summary>J — justificativa obrigatória.</summary>
    Justification = 1 << 1,

    /// <summary>P — prazo obrigatório.</summary>
    Deadline = 1 << 2,

    /// <summary>2A — dois administradores; quem aprova não pode ser quem propôs.</summary>
    TwoAdministrators = 1 << 3,

    /// <summary>Linhas marcadas "leitura auditada".</summary>
    AuditedRead = 1 << 4,
}
