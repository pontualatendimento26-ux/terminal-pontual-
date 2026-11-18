using System.Collections.Immutable;
using Pontual.Domain.Audit;

namespace Pontual.Server.Tests.Audit;

public class AuditChainTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Actor = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Target = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private static AuditEntryData Data(int index = 0) => new(
        OccurredAt: Start.AddMinutes(index),
        ActorType: ActorType.Admin,
        ActorId: Actor,
        Action: "config.publish",
        TargetType: "profile",
        TargetId: Target,
        Justification: $"motivo {index}");

    private static List<AuditEntry> BuildChain(int count)
    {
        var chain = new List<AuditEntry>(count);
        AuditEntry? previous = null;
        for (var i = 0; i < count; i++)
        {
            previous = AuditChain.Append(previous, Data(i));
            chain.Add(previous);
        }

        return chain;
    }

    private static ImmutableArray<byte> FlipFirstBit(ImmutableArray<byte> bytes) =>
        bytes.SetItem(0, (byte)(bytes[0] ^ 0x01));

    [Fact]
    public void First_entry_has_sequence_1_and_zeroed_previous_hash()
    {
        var first = AuditChain.Append(null, Data());

        Assert.Equal(1, first.Sequence);
        Assert.Equal(32, first.PreviousHash.Length);
        Assert.All(first.PreviousHash, b => Assert.Equal(0, b));
        Assert.Equal(32, first.Hash.Length);
        Assert.Contains(first.Hash, b => b != 0);
    }

    [Fact]
    public void Each_entry_links_to_the_previous_one()
    {
        var chain = BuildChain(3);

        Assert.Equal([1L, 2L, 3L], chain.Select(entry => entry.Sequence));
        Assert.Equal(chain[0].Hash.AsEnumerable(), chain[1].PreviousHash);
        Assert.Equal(chain[1].Hash.AsEnumerable(), chain[2].PreviousHash);
    }

    [Fact]
    public void Append_copies_the_data_into_the_entry()
    {
        var entry = AuditChain.Append(null, Data());

        Assert.Equal(Start, entry.OccurredAt);
        Assert.Equal(ActorType.Admin, entry.ActorType);
        Assert.Equal(Actor, entry.ActorId);
        Assert.Equal("config.publish", entry.Action);
        Assert.Equal("profile", entry.TargetType);
        Assert.Equal(Target, entry.TargetId);
        Assert.Equal("motivo 0", entry.Justification);
    }

    [Fact]
    public void Hash_is_deterministic()
    {
        var a = AuditChain.Append(null, Data());
        var b = AuditChain.Append(null, Data());

        Assert.Equal(a.Hash.AsEnumerable(), b.Hash);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Hash_format_is_stable()
    {
        // Vetor fixo: se este teste quebrar, o formato canônico mudou e as cadeias já gravadas deixam de verificar.
        // O valor esperado foi calculado fora do .NET, a partir da descrição do formato em AuditChain.
        var entry = AuditChain.Append(null, Data());

        Assert.Equal(
            "4fd24dbb4d2131fdb929a8d576c9ae11a0e1b94ddba3e1e96d8e1770c5459ce1",
            Convert.ToHexStringLower(entry.Hash.AsSpan()));
    }

    [Fact]
    public void Hash_does_not_depend_on_the_offset_of_the_timestamp()
    {
        var utc = AuditChain.Append(null, Data());
        var local = AuditChain.Append(null, Data() with { OccurredAt = Start.ToOffset(TimeSpan.FromHours(-3)) });

        Assert.Equal(utc.Hash.AsEnumerable(), local.Hash);
        Assert.Equal(TimeSpan.Zero, local.OccurredAt.Offset);
    }

    [Fact]
    public void Timestamp_is_truncated_to_microseconds_so_it_survives_the_database_round_trip()
    {
        var entry = AuditChain.Append(null, Data() with { OccurredAt = Start.AddTicks(1_234_567) });

        Assert.Equal(Start.AddTicks(1_234_560), entry.OccurredAt);
        Assert.True(AuditChain.Verify([entry]).IsIntact);
    }

    [Fact]
    public void Hash_depends_on_the_previous_hash()
    {
        var chain = BuildChain(2);
        var sameDataAfterAnotherEntry = AuditChain.Append(
            AuditChain.Append(null, Data(0) with { Justification = "outro" }),
            Data(1));

        Assert.Equal(chain[1].Sequence, sameDataAfterAnotherEntry.Sequence);
        Assert.NotEqual(chain[1].Hash.AsEnumerable(), sameDataAfterAnotherEntry.Hash);
    }

    [Fact]
    public void Fields_with_the_same_naive_concatenation_produce_different_hashes()
    {
        var a = AuditChain.Append(null, Data() with { Action = "ab", TargetType = "c" });
        var b = AuditChain.Append(null, Data() with { Action = "a", TargetType = "bc" });

        Assert.NotEqual(a.Hash.AsEnumerable(), b.Hash);
    }

    [Fact]
    public void Text_moved_between_adjacent_optional_fields_produces_different_hashes()
    {
        var a = AuditChain.Append(null, Data() with { TargetType = "x", TargetId = null, Justification = null });
        var b = AuditChain.Append(null, Data() with { TargetType = null, TargetId = null, Justification = "x" });

        Assert.NotEqual(a.Hash.AsEnumerable(), b.Hash);
    }

    [Fact]
    public void Null_and_empty_text_produce_different_hashes()
    {
        var withNull = AuditChain.Append(null, Data() with { Justification = null });
        var withEmpty = AuditChain.Append(null, Data() with { Justification = "" });

        Assert.NotEqual(withNull.Hash.AsEnumerable(), withEmpty.Hash);
    }

    [Fact]
    public void Null_and_empty_guid_produce_different_hashes()
    {
        var withNull = AuditChain.Append(null, Data() with { ActorId = null });
        var withEmpty = AuditChain.Append(null, Data() with { ActorId = Guid.Empty });

        Assert.NotEqual(withNull.Hash.AsEnumerable(), withEmpty.Hash);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Append_rejects_a_blank_action(string action)
    {
        Assert.Throws<ArgumentException>(() => AuditChain.Append(null, Data() with { Action = action }));
    }

    [Fact]
    public void Append_rejects_an_undefined_actor_type()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AuditChain.Append(null, Data() with { ActorType = (ActorType)42 }));
    }

    [Fact]
    public void Append_rejects_text_that_cannot_be_encoded_without_loss()
    {
        // Um surrogate solto viraria U+FFFD, e dois textos diferentes teriam o mesmo hash.
        Assert.ThrowsAny<ArgumentException>(() => AuditChain.Append(null, Data() with { Justification = "\uD800" }));

        var forged = AuditChain.Append(null, Data()) with { Justification = "\uD800" };
        Assert.Equal(AuditChainFailure.HashMismatch, AuditChain.Verify([forged]).Failure);
    }

    [Fact]
    public void Append_rejects_null_data()
    {
        Assert.Throws<ArgumentNullException>(() => AuditChain.Append(null, null!));
    }

    [Fact]
    public void Empty_chain_is_intact()
    {
        var result = AuditChain.Verify([]);

        Assert.True(result.IsIntact);
        Assert.Null(result.FailedSequence);
        Assert.Null(result.Failure);
    }

    [Fact]
    public void Intact_chain_passes()
    {
        var result = AuditChain.Verify(BuildChain(5));

        Assert.True(result.IsIntact);
        Assert.Null(result.FailedSequence);
        Assert.Null(result.Failure);
    }

    [Theory]
    [InlineData(nameof(AuditEntry.OccurredAt))]
    [InlineData(nameof(AuditEntry.ActorType))]
    [InlineData(nameof(AuditEntry.ActorId))]
    [InlineData(nameof(AuditEntry.Action))]
    [InlineData(nameof(AuditEntry.TargetType))]
    [InlineData(nameof(AuditEntry.TargetId))]
    [InlineData(nameof(AuditEntry.Justification))]
    [InlineData(nameof(AuditEntry.Hash))]
    public void Changing_a_field_of_a_middle_entry_is_detected_at_that_entry(string field)
    {
        var chain = BuildChain(5);
        var original = chain[2];
        chain[2] = field switch
        {
            nameof(AuditEntry.OccurredAt) => original with { OccurredAt = original.OccurredAt.AddSeconds(1) },
            nameof(AuditEntry.ActorType) => original with { ActorType = ActorType.System },
            nameof(AuditEntry.ActorId) => original with { ActorId = null },
            nameof(AuditEntry.Action) => original with { Action = "config.edit" },
            nameof(AuditEntry.TargetType) => original with { TargetType = null },
            nameof(AuditEntry.TargetId) => original with { TargetId = Guid.Empty },
            nameof(AuditEntry.Justification) => original with { Justification = "motivo 2 " },
            nameof(AuditEntry.Hash) => original with { Hash = FlipFirstBit(original.Hash) },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        var result = AuditChain.Verify(chain);

        Assert.False(result.IsIntact);
        Assert.Equal(3, result.FailedSequence);
        Assert.Equal(AuditChainFailure.HashMismatch, result.Failure);
    }

    [Fact]
    public void Changing_the_previous_hash_of_a_middle_entry_is_detected_at_that_entry()
    {
        var chain = BuildChain(5);
        chain[2] = chain[2] with { PreviousHash = FlipFirstBit(chain[2].PreviousHash) };

        var result = AuditChain.Verify(chain);

        Assert.False(result.IsIntact);
        Assert.Equal(3, result.FailedSequence);
        Assert.Equal(AuditChainFailure.PreviousHashMismatch, result.Failure);
    }

    [Fact]
    public void Changing_the_sequence_of_a_middle_entry_is_detected_at_that_entry()
    {
        var chain = BuildChain(5);
        chain[2] = chain[2] with { Sequence = 30 };

        var result = AuditChain.Verify(chain);

        Assert.False(result.IsIntact);
        Assert.Equal(30, result.FailedSequence);
        Assert.Equal(AuditChainFailure.SequenceGap, result.Failure);
    }

    [Fact]
    public void Rewriting_an_entry_with_a_recomputed_hash_breaks_the_link_of_the_next_one()
    {
        var chain = BuildChain(5);
        chain[2] = AuditChain.Append(chain[1], Data(2) with { Justification = "reescrito" });

        var result = AuditChain.Verify(chain);

        Assert.False(result.IsIntact);
        Assert.Equal(4, result.FailedSequence);
        Assert.Equal(AuditChainFailure.PreviousHashMismatch, result.Failure);
    }

    [Fact]
    public void Removing_a_middle_entry_is_detected()
    {
        var chain = BuildChain(5);
        chain.RemoveAt(2);

        var result = AuditChain.Verify(chain);

        Assert.False(result.IsIntact);
        Assert.Equal(4, result.FailedSequence);
        Assert.Equal(AuditChainFailure.SequenceGap, result.Failure);
    }

    [Fact]
    public void Removing_a_middle_entry_and_renumbering_is_detected_by_the_broken_link()
    {
        var chain = BuildChain(5);
        chain.RemoveAt(2);
        chain[2] = chain[2] with { Sequence = 3 };

        var result = AuditChain.Verify(chain);

        Assert.False(result.IsIntact);
        Assert.Equal(3, result.FailedSequence);
        Assert.Equal(AuditChainFailure.PreviousHashMismatch, result.Failure);
    }

    [Fact]
    public void Swapping_two_entries_is_detected()
    {
        var chain = BuildChain(5);
        (chain[1], chain[2]) = (chain[2], chain[1]);

        var result = AuditChain.Verify(chain);

        Assert.False(result.IsIntact);
        Assert.Equal(3, result.FailedSequence);
        Assert.Equal(AuditChainFailure.SequenceGap, result.Failure);
    }

    [Fact]
    public void Removing_the_first_entry_is_detected()
    {
        var chain = BuildChain(3);
        chain.RemoveAt(0);

        var result = AuditChain.Verify(chain);

        Assert.False(result.IsIntact);
        Assert.Equal(2, result.FailedSequence);
        Assert.Equal(AuditChainFailure.SequenceGap, result.Failure);
    }

    [Fact]
    public void A_first_entry_with_a_non_zero_previous_hash_is_detected()
    {
        var chain = BuildChain(2);
        chain[0] = chain[0] with { PreviousHash = FlipFirstBit(chain[0].PreviousHash) };

        var result = AuditChain.Verify(chain);

        Assert.False(result.IsIntact);
        Assert.Equal(1, result.FailedSequence);
        Assert.Equal(AuditChainFailure.PreviousHashMismatch, result.Failure);
    }

    [Fact]
    public void Truncating_the_tail_is_not_detected_without_an_external_anchor()
    {
        // Limite conhecido e documentado em AuditChain: quem cobre este caso é a âncora externa (audit_anchors).
        var chain = BuildChain(5);
        chain.RemoveRange(3, 2);

        Assert.True(AuditChain.Verify(chain).IsIntact);
    }

    [Fact]
    public void Entries_compare_by_value_including_the_hash_bytes()
    {
        var a = AuditChain.Append(null, Data());
        var copy = a with { Hash = [.. a.Hash], PreviousHash = [.. a.PreviousHash] };
        var tampered = a with { Hash = FlipFirstBit(a.Hash) };

        Assert.Equal(a, copy);
        Assert.Equal(a.GetHashCode(), copy.GetHashCode());
        Assert.NotEqual(a, tampered);
    }
}
