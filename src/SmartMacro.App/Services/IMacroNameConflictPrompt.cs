using System.Globalization;

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
    public string Heading => $"Макрос «{Name}» в библиотеке уже есть";

    /// <summary>Вторая строка: что произойдёт, если согласиться.</summary>
    public string Action => Kind == MacroNameConflictKind.Save
        ? "Сохранение под этим именем заменит его."
        : "Импорт под этим именем заменит его.";

    /// <summary>Третья строка: ЧТО именно будет потеряно. Главная строка окна.</summary>
    public string Loss
    {
        get
        {
            if (Fault is { Length: > 0 } fault)
            {
                return $"Его бандл не читается, так что неизвестно даже, что в нём: {fault} " +
                       "Замена уничтожит файл целиком.";
            }

            var parts = new List<string>(2);
            if (Templates > 0)
            {
                parts.Add(Count(Templates, "шаблон", "шаблона", "шаблонов"));
            }

            if (Submacros > 0)
            {
                parts.Add(Count(Submacros, "под-макрос", "под-макроса", "под-макросов"));
            }

            return parts.Count == 0
                ? "Ни шаблонов, ни под-макросов в нём нет — потеряется только его граф."
                : $"Будет потеряно: {string.Join(", ", parts)}.";
        }
    }

    /// <summary>Подпись кнопки «взять свободное имя» — с самим именем, чтобы не гадать, каким.</summary>
    public string FreeNameLabel => $"Взять имя «{FreeName}»";

    // Расписано руками, а не взято из библиотеки склонений: слов два, а интерфейс всё равно
    // только русский. Тот же приём, что у счётчика окон в бейдже целей.
    private static string Count(int count, string one, string few, string many)
    {
        var mod100 = count % 100;
        var word = mod100 is >= 11 and <= 14
            ? many
            : (count % 10) switch
            {
                1 => one,
                2 or 3 or 4 => few,
                _ => many,
            };

        return string.Create(CultureInfo.CurrentCulture, $"{count} {word}");
    }
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
