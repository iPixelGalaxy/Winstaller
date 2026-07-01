namespace Winstaller.Models;

/// <summary>
/// Represents a registry modification
/// </summary>
public class RegistryModification
{
    public string Key { get; set; } = string.Empty;
    public string ValueName { get; set; } = string.Empty;
    public string ValueType { get; set; } = "REG_SZ"; // REG_SZ, REG_DWORD, REG_EXPAND_SZ
    public string Value { get; set; } = string.Empty;
    public bool Delete { get; set; } = false;
}
