using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Windows;

namespace SmartMacro.Tests.Macros;

// W0.2a: selectors are how macros pick windows — AND semantics over require/exclude,
// evaluated against real WindowRegistry snapshots (the executor's exact input).
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

    // D4 moved the RULE to TargetSelector.Matches in Contracts and left this class as the
    // typed wrapper the executor calls, so the panel can evaluate the same selectors against
    // its own WindowDto snapshot without a second implementation. This pins the two together:
    // if anyone reintroduces the loops here, a divergence shows up as a failure rather than
    // as a badge that quietly lies about what a run will hit.
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
