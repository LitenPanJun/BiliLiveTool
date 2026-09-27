using BiliLiveTool.Core.Security;
using FluentAssertions;

namespace BiliLiveTool.Tests.Security;

public class SecureCredentialTests
{
    [Fact]
    public void ToString_Never_Leaks_Value()
    {
        using var credential = new SecureCredential("SESSDATA-super-secret");
        credential.ToString().Should().Be("***");
    }

    [Fact]
    public void DangerousGetValue_Returns_Original_Until_Disposed()
    {
        var credential = new SecureCredential("bili_jct_value");
        try
        {
            credential.DangerousGetValue().Should().Be("bili_jct_value");
        }
        finally
        {
            credential.Dispose();
        }
    }

    [Fact]
    public void Dispose_Clears_Memory_And_Is_Idempotent()
    {
        var credential = new SecureCredential("buvid3-value");
        credential.Dispose();

        credential.DangerousGetValue().ToCharArray().Should().OnlyContain(c => c == '\0');

        var act = () => credential.Dispose();
        act.Should().NotThrow();
    }
}
