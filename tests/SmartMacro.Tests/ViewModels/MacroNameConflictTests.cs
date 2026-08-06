using SmartMacro.Resources;
using SmartMacro.App.Services;

namespace SmartMacro.Tests.ViewModels;

// Формулировки вопроса о занятом имени. Живут они в записи, а не в разметке, ровно затем, чтобы
// их можно было проверить здесь: копия текста в axaml разъезжается незаметно, а «Заменить?» без
// названной цены — это вопрос, на который отвечают «да» не читая.
public class MacroNameConflictTests
{
    private static MacroNameConflict Conflict(int templates, int submacros = 0, string? fault = null) =>
        new(MacroNameConflictKind.Save, "pw-login", "pw-login-2", templates, submacros, fault);

    // «1 окон» — уже случавшийся здесь дефект, и цена ему доверие к остальному тексту.
    [Test]
    [Arguments(1, "1 шаблон")]
    [Arguments(2, "2 шаблона")]
    [Arguments(5, "5 шаблонов")]
    [Arguments(11, "11 шаблонов")]
    [Arguments(21, "21 шаблон")]
    public async Task TheLossIsCountedInProperRussian(int templates, string expected)
    {
        await Assert.That(Conflict(templates).Loss).Contains(expected);
    }

    [Test]
    public async Task BothKindsOfAttachmentAreNamed()
    {
        // Проверяется, что названы ОБА вида вложений и оба числа, а не то, какими словами. Иначе
        // тест ломался бы от вычитки формулировки, ради возможности которой строки и вынесли.
        var loss = Conflict(11, submacros: 2).Loss;
        await Assert.That(loss).Contains("11");
        await Assert.That(loss).Contains("2");
        await Assert.That(loss).IsNotEqualTo(Strings.Dialog_NameConflict_LossNothing);
        await Assert.That(loss).IsNotEqualTo(Strings.Dialog_NameConflict_LossUnreadable);
    }

    // Пустой макрос — тоже честный ответ: «ничего страшного» пользователь должен прочитать, а не
    // вывести из отсутствия строки.
    [Test]
    public async Task AnEmptyBundleSaysSoOutLoud()
    {
        await Assert.That(Conflict(0).Loss).Contains(Strings.Dialog_NameConflict_LossNothing);
    }

    // Про нечитаемый бандл сказать «0 шаблонов» было бы ложью: внутри неизвестно что.
    [Test]
    public async Task AnUnreadableBundleCarriesTheReadersVerdictInstead()
    {
        var loss = Conflict(0, fault: "Бандл сделан более новой версией формата (v7); эта сборка читает v0.").Loss;

        // Вердикт читателя уехал внутрь предупреждения дословно — на нём и держится «внутри
        // неизвестно что», а числа шаблонов тут быть не может по определению.
        await Assert.That(Msg.Arg(loss, Strings.Dialog_NameConflict_LossUnreadable))
            .Contains("более новой версией формата");
        await Assert.That(Msg.Is(loss, Strings.Dialog_NameConflict_LossList)).IsFalse();
    }

    [Test]
    public async Task TheFreeNameIsSpelledOutOnTheButton()
    {
        await Assert.That(Msg.Arg(Conflict(1).FreeNameLabel, Strings.Dialog_NameConflict_FreeName)).IsEqualTo("pw-login-2");
    }

    // Импорт и сохранение — разные глаголы: «импорт заменит его» на кнопке «Сохранить» читался бы
    // как чужое сообщение.
    [Test]
    public async Task SaveAndImportAreWordedApart()
    {
        var save = Conflict(1).Action;
        var import = Conflict(1) with { Kind = MacroNameConflictKind.Import };

        await Assert.That(save).IsEqualTo(Strings.Dialog_NameConflict_ActionSave);
        await Assert.That(import.Action).IsEqualTo(Strings.Dialog_NameConflict_ActionImport);
        // И они РАЗНЫЕ — ровно то, чего требует имя теста.
        await Assert.That(import.Action).IsNotEqualTo(save);
    }
}
