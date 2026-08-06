using System.Globalization;
using SmartMacro.Resources;

namespace SmartMacro.App.Services;

/// <summary>Зачем спрашиваем — от этого зависит одно слово в вопросе и подпись кнопки.</summary>
public enum MacroNameConflictKind
{
    /// <summary>Сохранение открытого макроса под именем, которое занято чужим.</summary>
    Save,

    /// <summary>Импорт чужого <c>.hsm</c> под именем, которое занято.</summary>
    Import,
}

/// <summary>Ответ ЧЕЛОВЕКА на занятое имя.</summary>
public enum MacroNameConflictChoice
{
    /// <summary>Ничего не делать. Умолчание, Esc и закрытие окна.</summary>
    Cancel,

    /// <summary>Заменить существующий макрос — со всем, что в нём лежит.</summary>
    Replace,

    /// <summary>Взять свободное имя с суффиксом (<see cref="MacroNameConflict.FreeName"/>).</summary>
    FreeName,
}

/// <summary>
/// Что именно стоит на пути — всё, что нужно, чтобы вопрос был осмысленным.
///
/// <b>Цена замены названа числами, а не словом «данные».</b> «Заменить?» без уточнения — это
/// вопрос, на который отвечают «да» не читая; «у «pw-login» 11 шаблонов» — вопрос, на который
/// отвечают. Шаблоны здесь не случайно первые: одиннадцать имён классов вырезаны из игры руками
/// на 3840×2160, и без живого клиента их не повторить.
///
/// Формулировки живут ЗДЕСЬ, а не в разметке, по той же причине, по которой описание способов
/// ввода живёт в <c>InputMethodInfo</c>: копия в разметке разъезжается незаметно, а проверить
/// текст в headless-тесте можно только у того, кто им владеет.
/// </summary>
/// <param name="Kind">Сохранение или импорт.</param>
/// <param name="Name">Занятое имя.</param>
/// <param name="FreeName">Свободное имя, предлагаемое взамен.</param>
/// <param name="Templates">Сколько шаблонов у существующего макроса.</param>
/// <param name="Submacros">Сколько у него под-макросов.</param>
/// <param name="Fault">
/// Вердикт читателя, если бандл существующего макроса не читается, — иначе <c>null</c>. Такой
/// случай не выдуман: бандл БУДУЩЕЙ версии формата лежит в библиотеке на месте намеренно, и
/// сказать про него, сколько в нём шаблонов, нельзя.
/// </param>
public sealed record MacroNameConflict(
    MacroNameConflictKind Kind,
    string Name,
    string FreeName,
    int Templates,
    int Submacros,
    string? Fault = null)
{
    /// <summary>Первая строка вопроса.</summary>
    public string Heading =>
        string.Format(CultureInfo.CurrentCulture, Strings.Dialog_NameConflict_Heading, Name);

    /// <summary>Вторая строка: что произойдёт, если согласиться.</summary>
    public string Action => Kind == MacroNameConflictKind.Save
        ? Strings.Dialog_NameConflict_ActionSave
        : Strings.Dialog_NameConflict_ActionImport;

    /// <summary>Третья строка: ЧТО именно будет потеряно. Главная строка окна.</summary>
    public string Loss
    {
        get
        {
            if (Fault is { Length: > 0 } fault)
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Dialog_NameConflict_LossUnreadable,
                    fault);
            }

            var parts = new List<string>(2);
            if (Templates > 0)
            {
                parts.Add(PluralForms.Format(
                    Templates,
                    Strings.Dialog_NameConflict_Templates_One,
                    Strings.Dialog_NameConflict_Templates_Few,
                    Strings.Dialog_NameConflict_Templates_Many));
            }

            if (Submacros > 0)
            {
                parts.Add(PluralForms.Format(
                    Submacros,
                    Strings.Dialog_NameConflict_Submacros_One,
                    Strings.Dialog_NameConflict_Submacros_Few,
                    Strings.Dialog_NameConflict_Submacros_Many));
            }

            return parts.Count == 0
                ? Strings.Dialog_NameConflict_LossNothing
                : string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Dialog_NameConflict_LossList,
                    string.Join(", ", parts));
        }
    }

    /// <summary>Подпись кнопки «взять свободное имя» — с самим именем, чтобы не гадать, каким.</summary>
    public string FreeNameLabel =>
        string.Format(CultureInfo.CurrentCulture, Strings.Dialog_NameConflict_FreeName, FreeName);
}

/// <summary>
/// Спрашивает пользователя, что делать с занятым именем макроса.
///
/// <b>Шов ровно того же рода, что <c>IUiDispatcher</c>, <see cref="IHotkeySuspension"/> и
/// <see cref="IMacroLauncher"/>, и по той же единственной причине.</b> View-model этого проекта не
/// знают ничего Avalonia-образного и гоняются headless против поддельного клиента IPC; вопрос,
/// заданный из VM живым окном, сделал бы половину проверок сохранения невозможной. Поддельная
/// реализация в тестах отвечает наперёд заданным ответом, и проверяется при этом ПОВЕДЕНИЕ
/// сохранения, а не собственный мок.
///
/// <c>Win32MessageBox</c> из <c>Native</c> сюда не годится, хотя он есть: он для фатальных
/// отказов старта, когда логгера ещё не существует, и никакого выбора из трёх вариантов не
/// предлагает.
///
/// <b>Отсутствие реализации — это «Отмена».</b> VM собирается и без неё (путь дизайнера,
/// headless-тесты, где вопрос не проверяют), и молчаливая замена чужого макроса в таком
/// окружении была бы худшим из возможных умолчаний.
/// </summary>
public interface IMacroNameConflictPrompt
{
    /// <summary>Задаёт вопрос и ждёт ответа. Отмена — законный и наиболее вероятный исход.</summary>
    Task<MacroNameConflictChoice> AskAsync(MacroNameConflict conflict);
}
