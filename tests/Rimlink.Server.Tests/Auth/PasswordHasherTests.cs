using Rimlink.Server.Application.Auth;
using Xunit;

namespace Rimlink.Server.Tests.Auth;

public class PasswordHasherTests
{
    [Fact]
    public void VerifyAcceptsOriginalAndRejectsOther()
    {
        string stored = PasswordHasher.Hash("пароль123");

        Assert.StartsWith("argon2id$", stored);
        Assert.True(PasswordHasher.Verify("пароль123", stored));
        Assert.False(PasswordHasher.Verify("пароль124", stored));
        Assert.False(PasswordHasher.Verify("", stored));
    }

    [Fact]
    public void SamePasswordHashesDifferentlyEachTime()
    {
        Assert.NotEqual(PasswordHasher.Hash("secret"), PasswordHasher.Hash("secret"));
    }

    [Fact]
    public void VerifyRejectsMalformedStoredValue()
    {
        Assert.False(PasswordHasher.Verify("secret", "not-a-hash"));
    }
}
