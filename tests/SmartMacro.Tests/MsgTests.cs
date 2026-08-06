using System.Globalization;
using SmartMacro.Resources;

namespace SmartMacro.Tests;

/// <summary>
/// Тесты на сам <see cref="Msg"/>.
///
/// Помощник несущий: через него сверяется больше сотни утверждений, и если <see cref="Msg.Is"/>
/// начнёт отвечать «да» на что угодно, все они разом перестанут уметь падать — молча и с зелёным
/// гейтом. Ровно та беда, ради которой помощник и заводился, только этажом ниже.
/// </summary>
public class MsgTests
{
    [Test]
    public async Task AMessageBuiltFromTheFormatIsRecognised()
    {
        var text = string.Format(CultureInfo.CurrentCulture, Strings.Validation_Node_DuplicateName, "клик-1");

        await Assert.That(Msg.Is(text, Strings.Validation_Node_DuplicateName)).IsTrue();
        await Assert.That(Msg.Arg(text, Strings.Validation_Node_DuplicateName)).IsEqualTo("клик-1");
    }

    [Test]
    public async Task ADifferentKeyIsNotRecognised()
    {
        // Главное свойство: «это тот ключ» обязано отличать ключ от соседнего, иначе проверка
        // выбора ветки ничего не проверяет.
        var text = string.Format(CultureInfo.CurrentCulture, Strings.Validation_Node_DuplicateName, "клик-1");

        await Assert.That(Msg.Is(text, Strings.Validation_Node_Unreachable)).IsFalse();
        await Assert.That(Msg.Is(text, Strings.Validation_Node_DuplicateId)).IsFalse();
    }

    [Test]
    public async Task TextThatMerelyStartsTheSameWayDoesNotPass()
    {
        // Сопоставление заякорено с обоих концов: иначе «сообщение плюс что-то ещё» проходило бы
        // за это сообщение.
        await Assert.That(Msg.Is(Strings.Validation_Node_Unreachable + " и ещё кое-что",
            Strings.Validation_Node_Unreachable)).IsFalse();
    }

    [Test]
    public async Task NullIsNotAMatch()
    {
        await Assert.That(Msg.Is(null, Strings.Validation_Node_Unreachable)).IsFalse();
    }

    [Test]
    public async Task ArgumentsComeBackByPlaceholderNumber_NotByPositionInTheString()
    {
        // «{1} из {0}» в файле встречается, и Args()[0] обязан отдавать то, что подставили под {0}.
        var text = string.Format(CultureInfo.InvariantCulture, "второй {1}, первый {0}", "a", "b");

        await Assert.That(Msg.Args(text, "второй {1}, первый {0}")).IsEquivalentTo(new[] { "a", "b" });
    }

    [Test]
    public async Task RegexMetacharactersInTheWordingAreLiteral()
    {
        // В формулировках есть скобки, точки и звёздочки; экранирование литеральной части — не
        // мелочь, без него «(Ctrl+клик)» стало бы группой.
        await Assert.That(Msg.Arg("вынести (2) ноды", "вынести ({0}) ноды")).IsEqualTo("2");
        await Assert.That(Msg.Is("вынести X ноды", "вынести ({0}) ноды")).IsFalse();
    }

    [Test]
    public async Task ALiteralBraceIsNotAPlaceholder()
    {
        // Node_Tag_Watermark несёт «{переменная}» как ТЕКСТ. Приняв её за место подстановки,
        // помощник сопоставлял бы этот ключ с чем угодно.
        await Assert.That(Msg.Is(Strings.Node_Tag_Watermark, Strings.Node_Tag_Watermark)).IsTrue();
        await Assert.That(Msg.Is("имя тега или что-нибудь", Strings.Node_Tag_Watermark)).IsFalse();
    }

    [Test]
    public async Task PartsSplitsATextGluedTogetherFromSeveralResources()
    {
        // Итог ленты лога: два ключа подряд, и у каждого своё {0}.
        var text = string.Format(CultureInfo.CurrentCulture, Strings.Log_Header_Entries_Many, 5)
                   + string.Format(CultureInfo.CurrentCulture, Strings.Log_Header_Problems, 3);

        await Assert.That(Msg.Parts(text, Strings.Log_Header_Entries_Many, Strings.Log_Header_Problems))
            .IsEquivalentTo(new[] { "5", "3" });
    }

    [Test]
    public async Task PartsRefusesATextMissingOneOfTheResources()
    {
        var onlyTheCount = string.Format(CultureInfo.CurrentCulture, Strings.Log_Header_Entries_Many, 5);

        await Assert.That(() => Msg.Parts(onlyTheCount, Strings.Log_Header_Entries_Many, Strings.Log_Header_Problems))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task AskingForTheArgumentOfAMessageBuiltFromAnotherFormatThrows()
    {
        // Молчаливый null здесь превратил бы проверку подставленного значения в сравнение двух
        // пустот.
        await Assert.That(() => Msg.Args("совсем другое", Strings.Validation_Node_DuplicateName))
            .Throws<InvalidOperationException>();
    }
}
