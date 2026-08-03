using SmartMacro.Contracts.Dto;

namespace SmartMacro.Hotkeys;

/// <summary>
/// Выключение и обратное включение глобальных аккордов демона. Реализуется
/// <see cref="HotkeyListener"/>.
///
/// Почему эта операция вообще видна в протоколе: Win32 <c>RegisterHotKey</c> проглатывает
/// нажатия уже зарегистрированного аккорда — WM_HOTKEY получает окно-владелец, и клавишу больше
/// не видит никто. То есть пока в процессе UI на экране висит выбор хоткея, ровно те сочетания,
/// которые пользователь скорее всего и хочет переназначить, до него бы не доходили. UI
/// оборачивает выбор в <c>SuspendHotkeys</c> / <c>ResumeHotkeys</c>.
///
/// Объявлено интерфейсом по той же причине, что и <c>IMacroRunner</c>, — ради шва для тестов:
/// настоящий <see cref="HotkeyListener"/> владеет двумя Win32-мониторами.
/// </summary>
public interface IHotkeyRegistration
{
    /// <summary>Снимает регистрацию со всех аккордов. Идемпотентно.</summary>
    Task SuspendAsync(CancellationToken cancellationToken = default);

    /// <summary>Регистрирует заново по текущей библиотеке макросов. Идемпотентно.</summary>
    Task ResumeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Аккорды, которые были привязаны к макросу, но которые <c>RegisterHotKey</c> отверг при
    /// последней попытке регистрации; зачем это вообще кому-то нужно, см.
    /// <see cref="HotkeyFailureDto"/>.
    ///
    /// Намеренно НЕ очищается на время приостановки: панель держит приостановку всё то время,
    /// что на экране режим «Макросы», а это ровно тот момент, когда она хочет это нарисовать, —
    /// и «мы минуту назад всё разрегистрировали» не есть ответ на вопрос «свободен ли этот
    /// аккорд».
    /// </summary>
    IReadOnlyList<HotkeyFailureDto> Failures { get; }
}
