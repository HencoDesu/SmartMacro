namespace PerfectWorldAgent.Models;

// Полный набор классов персонажей PW (русская локализация). Имена в идентификаторах —
// прямой transliteration не делаем, потому что:
//   1. Игрок узнаёт класс по русскому названию — UI, ростер, логи будут совпадать с тем,
//      что видит на экране;
//   2. C# позволяет кириллические идентификаторы, ToString() возвращает имя как есть,
//      энум-сериализация в roster.json даёт читаемое значение ("Маг" вместо "Wizard").
//
// Unknown — sentinel для placeholder Character'а до идентификации.
public enum CharacterClass
{
    Unknown,
    Маг,
    Воин,
    Стрелок,
    Друид,
    Оборотень,
    Странник,
    Жрец,
    Лучник,
    Паладин,
    Шаман,
    Убийца,
    Бард,
    Мистик,
    Страж,
    // C# identifiers can't contain spaces; in-game name is "Дух Крови". UI displays
    // ToString() which gives "ДухКрови" — acceptable for now, swap to a DisplayAttribute
    // / converter if presentation polish becomes worth it.
    ДухКрови,
    Жнец,
    Призрак,
    Канлонг,
}
