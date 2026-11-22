using Penrill.Domain.Constants;

namespace Penrill.Domain.UnitTests;

public class AccountRulesTests
{
    [TestCase(" Ann ", "ann")]
    [TestCase("kid-1f2e", "kid-1f2e")]
    [TestCase("ab", null)]
    [TestCase("ann smith", null)]
    [TestCase("анна", null)]
    public void NormalizesUserNames(string input, string? expected) => AccountRules.NormalizeUserName(input).ShouldBe(expected);

    [TestCase("ann@example.com", true)]
    [TestCase("ann@example", false)]
    [TestCase("a@b@c.d", false)]
    [TestCase("ann @example.com", false)]
    public void ChecksEmailsLoosely(string input, bool valid) => (AccountRules.CleanEmail(input) is not null).ShouldBe(valid);

    [Test]
    public void BoardIdsAreLowerCaseHex()
    {
        BoardLimits.IsBoardId("00112233445566778899aabbccddeeff").ShouldBeTrue();
        BoardLimits.IsBoardId("00112233445566778899AABBCCDDEEFF").ShouldBeFalse();
        BoardLimits.IsBoardId("0011").ShouldBeFalse();
    }
}
