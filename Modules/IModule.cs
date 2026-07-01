using Winstaller.Configuration;
using Winstaller.Utilities;

namespace Winstaller.Modules;

/// <summary>
/// Interface for all Winstaller modules
/// </summary>
public interface IModule
{
    /// <summary>
    /// Display name of the module
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Short description of what the module does
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Whether the module is enabled in configuration
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Executes the module's main functionality
    /// </summary>
    /// <returns>True if successful, false otherwise</returns>
    Task<bool> ExecuteAsync();

    /// <summary>
    /// Shows a submenu with module-specific options
    /// </summary>
    Task ShowMenuAsync();
}
