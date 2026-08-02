using FakeItEasy;
using SmartMacro.Models;
using SmartMacro.Vision;

namespace SmartMacro.Tests;

// W0.0 smoke tests: prove the TUnit runner executes from CLI and FakeItEasy can fake
// a Core interface. Real coverage arrives with each wave (see docs/refactoring-plan-split.md §0.6).
public class SmokeTests
{
    [Test]
    public async Task TUnit_Runner_Executes()
    {
        var sum = 1 + 1;
        await Assert.That(sum).IsEqualTo(2);
    }

    [Test]
    public async Task FakeItEasy_Fakes_Core_Interface()
    {
        var matcher = A.Fake<IClassMatcher>();
        var expected = new ClassMatch(CharacterClass.Лучник, 0.97);
        A.CallTo(() => matcher.Match(A<byte[]>._, A<IReadOnlyDictionary<CharacterClass, byte[]>>._))
            .Returns(expected);

        var result = matcher.Match([], new Dictionary<CharacterClass, byte[]>());

        await Assert.That(result).IsEqualTo(expected);
        A.CallTo(() => matcher.Match(A<byte[]>._, A<IReadOnlyDictionary<CharacterClass, byte[]>>._))
            .MustHaveHappenedOnceExactly();
    }
}
