using MevaCare.AI.HealthCompanion.Modules.Integrations.Features;
using MevaCare.AI.HealthCompanion.Modules.Integrations.Security;
using Microsoft.AspNetCore.DataProtection;

namespace MevaCare.AI.HealthCompanion.UnitTests.Integrations;

public class TokenProtectorTests
{
    private static DataProtectionTokenProtector New() => new(new EphemeralDataProtectionProvider());

    [Fact]
    public void Tokens_round_trip_and_the_stored_form_does_not_contain_them()
    {
        var protector = New();

        var stored = protector.Protect("super-secret-refresh-token");

        Assert.DoesNotContain("super-secret", stored, StringComparison.Ordinal);
        Assert.True(protector.TryUnprotect(stored, out var value));
        Assert.Equal("super-secret-refresh-token", value);
    }

    [Fact]
    public void The_same_token_is_stored_differently_each_time()
    {
        var protector = New();

        Assert.NotEqual(protector.Protect("t"), protector.Protect("t"));
    }

    [Fact]
    public void Tampered_values_and_values_from_another_key_ring_cannot_be_read_and_say_so_without_throwing()
    {
        var stored = New().Protect("token");

        Assert.False(New().TryUnprotect(stored, out var other)); // another (ephemeral) key ring, like a lost key ring
        Assert.Equal("", other);
        Assert.False(New().TryUnprotect(stored[..^4] + "AAAA", out _));
        Assert.False(New().TryUnprotect("not protected data", out _));
        Assert.False(New().TryUnprotect("", out _));
    }

    [Fact]
    public void The_state_hash_is_a_stable_64_character_hex_digest_and_differs_per_state()
    {
        var hash = ConnectionService.Hash("some-state");

        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9A-F]{64}$", hash);
        Assert.Equal(hash, ConnectionService.Hash("some-state"));
        Assert.NotEqual(hash, ConnectionService.Hash("some-state2"));
        Assert.DoesNotContain("some-state", hash, StringComparison.Ordinal);
    }
}
