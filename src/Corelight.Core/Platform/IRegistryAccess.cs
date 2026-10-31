namespace Corelight.Core.Platform;

/// <summary>
/// Доступ к реестру. Пути ключей задаются целиком: "HKLM\SOFTWARE\..." или "HKCU\Software\...".
/// </summary>
public interface IRegistryAccess
{
    /// <returns>null, если значения (или ключа) нет.</returns>
    RegValue? GetValue(string keyPath, string name);

    /// <summary>Записывает значение, создавая ключ при необходимости.</summary>
    void SetValue(string keyPath, string name, RegValue value);

    /// <summary>Удаляет значение; отсутствие значения ошибкой не считается.</summary>
    void DeleteValue(string keyPath, string name);

    IReadOnlyList<string> GetValueNames(string keyPath);

    IReadOnlyList<string> GetSubKeyNames(string keyPath);
}
