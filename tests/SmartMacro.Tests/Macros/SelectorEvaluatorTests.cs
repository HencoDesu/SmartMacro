using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Windows;

namespace SmartMacro.Tests.Macros;

// W0.2a: селекторы — это то, чем макросы выбирают окна: семантика И по require/exclude,
// вычисляемая на настоящих снимках WindowRegistry (ровно то, что подаётся исполнителю).
public class SelectorEvaluatorTests
{
    private static readonly IntPtr Archer = new(1);
    private static readonly IntPtr Master = new(2);
    private static readonly IntPtr Untagged = new(3);

    private static WindowRegistry BuildRegistry()
    {
        var registry = new WindowRegistry(NullLogger<WindowRegistry>.Instance);
        registry.Register(Archer, "elementclient");
        registry.AddTag(Archer, "перс");
        registry.AddTag(Archer, "лучник");
        registry.Register(Master, "elementclient");
        registry.AddTag(Master, "перс");
        registry.AddTag(Master, "МАСТЕР");
        registry.Register(Untagged, "elementclient");
        return registry;
    }

    private static IReadOnlyList<IntPtr> SelectHwnds(TargetSelector selector) =>
        SelectorEvaluator.Select(BuildRegistry().Snapshot(), selector).Select(w => w.Hwnd).ToList();

    [Test]
    public async Task EmptySelector_MatchesEveryWindow()
    {
        var matched = SelectHwnds(new TargetSelector());

        await Assert.That(matched).Count().IsEqualTo(3);
    }

    [Test]
    public async Task RequireTags_AndSemantics_AllMustBePresent()
    {
        var onlyPersonaje = SelectHwnds(new TargetSelector { RequireTags = ["перс"] });
        var personajeAndArcher = SelectHwnds(new TargetSelector { RequireTags = ["перс", "лучник"] });

        await Assert.That(onlyPersonaje).Count().IsEqualTo(2);
        await Assert.That(onlyPersonaje.Contains(Untagged)).IsFalse();
        await Assert.That(personajeAndArcher).Count().IsEqualTo(1);
        await Assert.That(personajeAndArcher[0]).IsEqualTo(Archer);
    }

    [Test]
    public async Task ExcludeTags_AnyPresentDisqualifies()
    {
        var withoutMaster = SelectHwnds(new TargetSelector { ExcludeTags = ["МАСТЕР"] });

        await Assert.That(withoutMaster).Count().IsEqualTo(2);
        await Assert.That(withoutMaster.Contains(Master)).IsFalse();
    }

    [Test]
    public async Task RequireAndExclude_Combine()
    {
        var matched = SelectHwnds(new TargetSelector { RequireTags = ["перс"], ExcludeTags = ["МАСТЕР"] });

        await Assert.That(matched).Count().IsEqualTo(1);
        await Assert.That(matched[0]).IsEqualTo(Archer);
    }

    [Test]
    public async Task UnknownRequiredTag_MatchesNothing()
    {
        var matched = SelectHwnds(new TargetSelector { RequireTags = ["жрец"] });

        await Assert.That(matched).Count().IsEqualTo(0);
    }

    [Test]
    public async Task Tags_AreCaseSensitive()
    {
        var matched = SelectHwnds(new TargetSelector { RequireTags = ["ПЕРС"] });

        await Assert.That(matched).Count().IsEqualTo(0);
    }

    // D4 перенесла само ПРАВИЛО в TargetSelector.Matches (Contracts тогда, Shared с F1),
    // оставив этот класс
    // типизированной обёрткой, которую зовёт исполнитель, — чтобы панель могла вычислять те же
    // селекторы на своём снимке WindowDto без второй реализации. Этот тест сшивает обе:
    // если кто-нибудь вернёт циклы сюда, расхождение всплывёт как упавший тест, а не как бейдж,
    // втихую врущий о том, кого прогон на самом деле заденет.
    [Test]
    public async Task Evaluator_DelegatesToTheSelectorsOwnRule()
    {
        var snapshot = BuildRegistry().Snapshot();
        TargetSelector[] cases =
        [
            new(),
            new() { RequireTags = ["перс"] },
            new() { RequireTags = ["перс", "лучник"] },
            new() { ExcludeTags = ["МАСТЕР"] },
            new() { RequireTags = ["перс"], ExcludeTags = ["МАСТЕР"] },
            new() { RequireTags = ["ПЕРС"] },
        ];

        foreach (var selector in cases)
        {
            foreach (var window in snapshot)
            {
                await Assert.That(SelectorEvaluator.Matches(window, selector))
                    .IsEqualTo(selector.Matches(window.Tags));
            }
        }
    }
}
