using FaceVerification.Domain;

namespace FaceVerification.UnitTests.Domain;

public sealed class VerificationSessionTests
{
    [Fact]
    public void Create_generates_single_use_nonce_hash()
    {
        var now = DateTimeOffset.UtcNow;
        var (session, nonce) = VerificationSession.Create("user", "client", now, TimeSpan.FromMinutes(5));
        Assert.True(session.ValidateNonce(nonce));
        Assert.False(session.ValidateNonce(nonce + "x"));
        Assert.DoesNotContain(nonce, session.NonceHash, StringComparison.Ordinal);
        Assert.Equal(64, session.NonceHash.Length);
    }

    [Fact]
    public void Expired_session_cannot_start()
    {
        var now = DateTimeOffset.UtcNow;
        var (session, _) = VerificationSession.Create("user", "client", now, TimeSpan.FromMinutes(1));
        Assert.Throws<InvalidOperationException>(() => session.Start(now.AddMinutes(2), null, null));
        Assert.Equal(VerificationSessionStatus.Expired, session.Status);
    }

    [Fact]
    public void Processing_session_cannot_be_reused()
    {
        var now = DateTimeOffset.UtcNow;
        var (session, _) = VerificationSession.Create("user", "client", now, TimeSpan.FromMinutes(5));
        session.Start(now, null, null);
        Assert.Throws<InvalidOperationException>(() => session.Start(now, null, null));
        session.Complete(now);
        Assert.Throws<InvalidOperationException>(() => session.Complete(now));
    }
}
