using SmartMacro.Macros.Execution;
using SmartMacro.Native;

namespace SmartMacro.Tests.Macros;

// W0.2a: переменные прогона — триггер кладёт затравкой `cursor`, чтение отсутствующего имени
// бросает исключение (исполнитель превращает это в прерывание прогона), подстановка {var} и
// клоны для под-прогонов, которые никогда не делят состояние с родителем.
public class MacroVariablesTests
{
    [Test]
    public async Task ForTrigger_SeedsCursorPoint()
    {
        var variables = MacroVariables.ForTrigger(new ScreenPoint(10, 20));

        await Assert.That(variables.Count).IsEqualTo(1);
        await Assert.That(variables.GetPoint(MacroVariables.CursorVariableName)).IsEqualTo(new ScreenPoint(10, 20));
    }

    [Test]
    public async Task Get_MissingVariable_Throws()
    {
        var variables = new MacroVariables();

        await Assert.That(() => variables.Get("нет")).Throws<MacroVariableNotFoundException>();
    }

    [Test]
    public async Task GetPoint_OnStringVariable_ThrowsTypeMismatch()
    {
        var variables = new MacroVariables();
        variables.Set("tag", "виз");

        await Assert.That(() => variables.GetPoint("tag")).Throws<MacroVariableTypeMismatchException>();
    }

    [Test]
    public async Task Set_Overwrites()
    {
        var variables = new MacroVariables();
        variables.Set("tag", "виз");
        variables.Set("tag", "жрец");

        await Assert.That(variables.Get("tag")).IsEqualTo((VariableValue)"жрец");
    }

    [Test]
    public async Task Interpolate_ReplacesAllPlaceholderKinds()
    {
        var variables = MacroVariables.ForTrigger(new ScreenPoint(3, 4));
        variables.Set("tag", "виз");
        variables.Set("n", 2.5);

        var result = variables.Interpolate("icons/{tag}-{n}{cursor}.png");

        // Числа печатаются в инвариантной культуре, точки — через ScreenPoint.ToString().
        await Assert.That(result).IsEqualTo("icons/виз-2.5(3,4).png");
    }

    [Test]
    public async Task Interpolate_WithoutPlaceholders_ReturnsInput()
    {
        var variables = new MacroVariables();

        await Assert.That(variables.Interpolate("статичный текст")).IsEqualTo("статичный текст");
    }

    [Test]
    public async Task Interpolate_MissingVariable_Throws()
    {
        var variables = new MacroVariables();

        await Assert.That(() => variables.Interpolate("icons/{нет}.png")).Throws<MacroVariableNotFoundException>();
    }

    [Test]
    public async Task Clone_IsIndependentInBothDirections()
    {
        var parent = new MacroVariables();
        parent.Set("общий", "родитель");

        var child = parent.Clone();
        child.Set("общий", "потомок");
        child.Set("только-потомок", "x");
        parent.Set("только-родитель", "y");

        await Assert.That(parent.Get("общий")).IsEqualTo((VariableValue)"родитель");
        await Assert.That(child.Get("общий")).IsEqualTo((VariableValue)"потомок");
        await Assert.That(parent.TryGet("только-потомок", out _)).IsFalse();
        await Assert.That(child.TryGet("только-родитель", out _)).IsFalse();
    }
}
