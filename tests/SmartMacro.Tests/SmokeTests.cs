using FakeItEasy;
using SmartMacro.Native;
using SmartMacro.Vision;

namespace SmartMacro.Tests;

// Дымовые тесты W0.0: доказывают, что прогон TUnit запускается из CLI и что FakeItEasy умеет
// подделать интерфейс из Core. Настоящее покрытие приезжает с каждой волной
// (см. docs/refactoring-plan-split.md §0.6).
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
        var expected = new TagMatch("Лучник", 0.97);
        A.CallTo(() => matcher.Match(A<byte[]>._, A<IReadOnlyDictionary<string, byte[]>>._, A<ScreenRect>._))
            .Returns(expected);

        var result = matcher.Match([], new Dictionary<string, byte[]>(), new ScreenRect(0, 0, 10, 10));

        await Assert.That(result).IsEqualTo(expected);
        A.CallTo(() => matcher.Match(A<byte[]>._, A<IReadOnlyDictionary<string, byte[]>>._, A<ScreenRect>._))
            .MustHaveHappenedOnceExactly();
    }
}
