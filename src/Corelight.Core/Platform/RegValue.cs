using System.Globalization;
using Microsoft.Win32;

namespace Corelight.Core.Platform;

/// <summary>
/// Значение реестра в текстовом представлении, удобном для JSON-каталога и файла бэкапа.
/// DWord/QWord — десятичное или 0x-шестнадцатеричное число, Binary — hex-строка,
/// MultiString — строки, разделённые символом '\0'.
/// </summary>
public sealed record RegValue(RegistryValueKind Kind, string Data)
{
    public static RegValue DWord(uint value) => new(RegistryValueKind.DWord, value.ToString(CultureInfo.InvariantCulture));
    public static RegValue String(string value) => new(RegistryValueKind.String, value);

    /// <summary>Приводит Data к каноническому виду, чтобы "0x1" и "1" считались одинаковыми.</summary>
    public RegValue Normalize() => FromObject(Kind, ToObject());

    public object ToObject() => Kind switch
    {
        RegistryValueKind.DWord => unchecked((int)ParseUInt64(Data, uint.MaxValue)),
        RegistryValueKind.QWord => unchecked((long)ParseUInt64(Data, ulong.MaxValue)),
        RegistryValueKind.String or RegistryValueKind.ExpandString => Data,
        RegistryValueKind.MultiString => Data.Length == 0 ? Array.Empty<string>() : Data.Split('\0'),
        RegistryValueKind.Binary => Convert.FromHexString(Data),
        _ => throw new NotSupportedException($"Тип значения реестра {Kind} не поддерживается."),
    };

    public static RegValue FromObject(RegistryValueKind kind, object value) => kind switch
    {
        RegistryValueKind.DWord => new(kind, unchecked((uint)Convert.ToInt32(value, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture)),
        RegistryValueKind.QWord => new(kind, unchecked((ulong)Convert.ToInt64(value, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture)),
        RegistryValueKind.String or RegistryValueKind.ExpandString => new(kind, (string)value),
        RegistryValueKind.MultiString => new(kind, string.Join('\0', (string[])value)),
        RegistryValueKind.Binary => new(kind, Convert.ToHexString((byte[])value)),
        _ => throw new NotSupportedException($"Тип значения реестра {kind} не поддерживается."),
    };

    public bool SameAs(RegValue? other) =>
        other is not null && other.Kind == Kind && string.Equals(Normalize().Data, other.Normalize().Data, StringComparison.Ordinal);

    private static ulong ParseUInt64(string text, ulong max)
    {
        var s = text.Trim();
        var value = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.Parse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : ulong.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
        if (value > max)
            throw new FormatException($"Значение {text} не помещается в тип реестра.");
        return value;
    }
}
