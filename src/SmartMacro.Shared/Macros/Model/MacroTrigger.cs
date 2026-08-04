using System.Text.Json.Serialization;
using SmartMacro.Native;

namespace SmartMacro.Macros.Model;

/// <summary>
/// Как макрос запускается сам. Полиморфен в JSON через <c>$type</c>
/// (<c>"hotkey"</c> / <c>"process"</c>).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(HotkeyTrigger), typeDiscriminator: "hotkey")]
[JsonDerivedType(typeof(ProcessAppearedTrigger), typeDiscriminator: "process")]
public abstract record MacroTrigger;

/// <summary>
/// Триггер глобальной горячей клавиши: аккорд с клавиатуры ЛИБО аккорд с кнопкой мыши. Ровно
/// одно из <paramref name="Key"/> / <paramref name="MouseButton"/> обязано отличаться от
/// значения по умолчанию — форма ровно такая же плоская, как у старых привязок горячих
/// клавиш, поэтому прежние аккорды переводятся 1:1:
///   * <c>Key != 0</c> → клавиатурная горячая клавиша (Win32 RegisterHotKey).
///   * <c>MouseButton != None</c> → мышиная горячая клавиша (низкоуровневый хук WH_MOUSE_LL).
/// У прогона по горячей клавише нет контекстного окна; триггер засевает переменную
/// <c>cursor</c>.
/// </summary>
public sealed record HotkeyTrigger(
    HotkeyModifiers Modifiers,
    VirtualKey Key,
    MouseButton MouseButton = MouseButton.None) : MacroTrigger
{
    /// <summary>Аккорд собран на кнопке мыши.</summary>
    [JsonIgnore]
    public bool IsMouse => MouseButton != MouseButton.None;

    /// <summary>Аккорд собран на клавиатуре.</summary>
    [JsonIgnore]
    public bool IsKeyboard => Key != 0 && MouseButton == MouseButton.None;
}

/// <summary>
/// Срабатывает, когда появляется новое окно процесса <paramref name="ProcessName"/>. Это новое
/// окно становится контекстным окном прогона (макросы «загрузочного» типа).
/// </summary>
public sealed record ProcessAppearedTrigger(string ProcessName) : MacroTrigger;
