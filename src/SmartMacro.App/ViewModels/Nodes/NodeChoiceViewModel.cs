using SmartMacro.App.Mvvm;
using SmartMacro.Resources;

namespace SmartMacro.App.ViewModels.Nodes;

/// <summary>
/// Одна строка выпадающего списка «куда ведёт этот исход» и «с какой ноды начинать».
///
/// Существует потому, что связь в модели идёт по <see cref="Guid"/>, а показывать guid
/// пользователю нельзя. Раньше списки были просто <c>ObservableCollection&lt;string&gt;</c> с
/// id внутри — id и был подписью.
///
/// <b>Экземпляры живут дольше пересборок списка.</b> Редактор держит по одному объекту на ноду и
/// при переименовании меняет у него <see cref="Display"/>, а не подменяет элемент коллекции.
/// Это не оптимизация: <c>ComboBox</c> хранит ССЫЛКУ на выбранный элемент, и подмена объекта на
/// равный по значению обновила бы список, но оставила в закрытом поле старую подпись. Заодно
/// исчезает и мимолётное «SelectedItem выпал из ItemsSource», ради которого вокруг пересборки
/// приходилось снимать и восстанавливать снимок значений.
/// </summary>
public sealed class NodeChoiceViewModel : ObservableObject
{
    private string _display;

    private NodeChoiceViewModel(Guid? id, string display)
    {
        Id = id;
        _display = display;
    }

    /// <summary>
    /// Единственный элемент «цели нет»: на этом исходе прогон заканчивается. Полноправное
    /// значение, а не пустота, — потому и подписан словами: пустая строка в списке читалась бы
    /// как «ещё не заполнено».
    /// </summary>
    public static NodeChoiceViewModel End { get; } = new(null, Strings.Node_Edge_End);

    /// <summary>Нода, на которую указывает выбор, либо <c>null</c> у <see cref="End"/>.</summary>
    public Guid? Id { get; }

    /// <summary>Подпись ноды — то, что видно в списке.</summary>
    public string Display
    {
        get => _display;
        set => SetField(ref _display, value);
    }

    /// <summary><c>true</c> у элемента «конец прогона».</summary>
    public bool IsEnd => Id is null;

    /// <summary>Заводит элемент для существующей ноды.</summary>
    public static NodeChoiceViewModel ForNode(Guid id, string display) => new(id, display);

    /// <summary>
    /// Элемент для ссылки в НЕСУЩЕСТВУЮЩУЮ ноду — такую способен оставить правленный руками
    /// файл. Остаётся выбираемым намеренно: редактор обязан показывать правду, чтобы валидатор
    /// мог на неё пожаловаться, а не тихо переписывать битое ребро в «конец прогона».
    /// </summary>
    public static NodeChoiceViewModel Dangling(Guid id) => new(id, Strings.Node_Edge_Dangling);

    public override string ToString() => _display;
}
