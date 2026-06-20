namespace PerfectWorldAgent.Config;

/// <summary>
/// Примеры UI элементов игры.
/// </summary>
public class GameUiElementExamplesName
{
    /// <summary>
    /// Кнопка выбора сервера.
    /// </summary>
    public string ServerSelect { get; init; } = "ServerSelectButton";

    /// <summary>
    /// Кнопка выбора персонажа.
    /// </summary>
    public string CharacterSelect { get; init; } = "CharacterSelectButton";

    /// <summary>
    /// Иконки чата. Используются для индикации того что персонаж залогинен в игру.
    /// </summary>
    public string ChatSettings { get; init; } = "ChatPanelButtons";
}
