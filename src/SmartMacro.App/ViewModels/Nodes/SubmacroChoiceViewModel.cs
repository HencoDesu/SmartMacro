using SmartMacro.App.Mvvm;

namespace SmartMacro.App.ViewModels.Nodes;

/// <summary>
/// Одна строка выпадающего списка «какой под-макрос звать» (волна F4).
///
/// Отдельный тип от <see cref="NodeChoiceViewModel"/>, хотя форма та же (id плюс подпись), —
/// потому что множества разные и путать их нельзя: там ноды ЭТОГО графа, здесь под-макросы ЭТОГО
/// бандла. Один список, подходящий обоим, однажды подставил бы в ребро id функции.
///
/// <b>Экземпляры живут дольше пересборок списка</b> — ровно по той же причине, что и у выбора
/// ноды: <c>ComboBox</c> держит ССЫЛКУ на выбранный элемент, и подмена объекта на равный по
/// значению оставила бы в закрытом поле старую подпись.
/// </summary>
public sealed class SubmacroChoiceViewModel : ObservableObject
{
    private string _display;

    private SubmacroChoiceViewModel(Guid id, string display)
    {
        Id = id;
        _display = display;
    }

    /// <summary>Личность под-макроса — то, что нода хранит в модели.</summary>
    public Guid Id { get; }

    /// <summary>Подпись под-макроса — то, что видно в списке.</summary>
    public string Display
    {
        get => _display;
        set => SetField(ref _display, value);
    }

    /// <summary>Элемент для существующего под-макроса.</summary>
    public static SubmacroChoiceViewModel For(Guid id, string display) => new(id, display);

    /// <summary>
    /// Элемент для ссылки на под-макрос, которого в бандле НЕТ, — такую оставит правка руками или
    /// удаление функции мимо редактора. Остаётся выбираемым намеренно: редактор обязан показывать
    /// правду, чтобы валидатор мог на неё пожаловаться, а не тихо обнулять ссылку.
    /// </summary>
    public static SubmacroChoiceViewModel Missing(Guid id) => new(id, "(под-макроса нет в бандле)");

    public override string ToString() => _display;
}
