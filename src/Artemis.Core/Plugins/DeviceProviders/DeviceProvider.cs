using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RGB.NET.Core;

namespace Artemis.Core.DeviceProviders;

/// <inheritdoc />
/// <summary>
///     Allows you to implement and register your own device provider
/// </summary>
public abstract class DeviceProvider : PluginFeature
{
    /// <summary>
    ///     The RGB.NET device provider backing this Artemis device provider
    /// </summary>
    public abstract IRGBDeviceProvider RgbDeviceProvider { get; }

    /// <summary>
    ///     Returns the persistent identifier Artemis uses to associate a physical device with its settings and profile LEDs.
    ///     Providers that can expose a serial number, hardware path, network address, or another stable instance identifier
    ///     should override this method. The returned value must remain stable across provider and application restarts.
    /// </summary>
    /// <param name="device">The RGB.NET device to identify.</param>
    public virtual string GetDeviceIdentifier(IRGBDevice device)
    {
        return device.GetDeviceIdentifier();
    }

    /// <summary>
    /// Returns former persistent identifiers that should be migrated to the current identifier for this device.
    /// This is for a provider that replaces an upstream identifier known to be unstable with a durable one.
    /// </summary>
    /// <param name="device">The current RGB.NET device.</param>
    /// <returns>Identifiers that represent this same device under an older provider identity scheme.</returns>
    public virtual IEnumerable<string> GetLegacyDeviceIdentifiers(IRGBDevice device)
    {
        return [];
    }

    /// <summary>
    /// Returns the persistent identity of the physical parent represented by a split child device identifier.
    /// Providers that expose one physical controller as multiple logical devices can override this method so
    /// Artemis does not label an obsolete child topology entry as a missing physical device while a sibling is present.
    /// </summary>
    /// <param name="deviceIdentifier">The persistent identifier of a current or stored logical device.</param>
    /// <returns>The shared physical-parent identity, or <see langword="null" /> for an independent device.</returns>
    public virtual string? GetParentDeviceIdentifier(string deviceIdentifier)
    {
        return null;
    }

    /// <summary>
    ///     A boolean indicating whether this device provider detects the physical layout of connected keyboards.
    ///     <para>
    ///         Note: <see cref="GetLogicalLayout" /> is only called when this or <see cref="CanDetectLogicalLayout" />
    ///         is <see langword="true" />.
    ///     </para>
    /// </summary>
    public bool CanDetectPhysicalLayout { get; protected set; }

    /// <summary>
    ///     A boolean indicating whether this device provider detects the logical layout of connected keyboards
    ///     <para>
    ///         Note: <see cref="GetLogicalLayout" /> is only called when this or <see cref="CanDetectPhysicalLayout" />
    ///         is <see langword="true" />.
    ///     </para>
    /// </summary>
    public bool CanDetectLogicalLayout { get; protected set; }

    /// <summary>
    ///     Gets or sets a boolean indicating whether adding missing LEDs defined in a layout but missing on the device is
    ///     supported
    ///     <para>Note: Defaults to <see langword="true" />.</para>
    /// </summary>
    public bool CreateMissingLedsSupported { get; protected set; } = true;

    /// <summary>
    ///     Gets or sets a boolean indicating whether removing excess LEDs present in the device but missing in the layout is
    ///     supported
    ///     <para>Note: Defaults to <see langword="true" />.</para>
    /// </summary>
    public bool RemoveExcessiveLedsSupported { get; protected set; } = true;

    /// <summary>
    ///     Gets or sets a boolean indicating whether suspending the device provider is supported
    /// </summary>
    public bool SuspendSupported { get; protected set; }

    /// <summary>
    ///     Loads a layout for the specified device and wraps it in an <see cref="ArtemisLayout" />
    /// </summary>
    /// <param name="device">The device to load the layout for</param>
    /// <returns>The resulting Artemis layout</returns>
    public virtual ArtemisLayout LoadLayout(ArtemisDevice device)
    {
        string layoutDir = Path.Combine(Plugin.Directory.FullName, "Layouts");
        string filePath = Path.Combine(
            layoutDir,
            device.RgbDevice.DeviceInfo.Manufacturer,
            device.DeviceType.ToString(),
            GetDeviceLayoutName(device)
        );
        return new ArtemisLayout(filePath);
    }

    /// <summary>
    ///     Loads a layout from the user layout folder for the specified device and wraps it in an <see cref="ArtemisLayout" />
    /// </summary>
    /// <param name="device">The device to load the layout for</param>
    /// <returns>The resulting Artemis layout</returns>
    public virtual ArtemisLayout LoadUserLayout(ArtemisDevice device)
    {
        string layoutDir = Constants.LayoutsFolder;
        string filePath = Path.Combine(
            layoutDir,
            device.RgbDevice.DeviceInfo.Manufacturer,
            device.DeviceType.ToString(),
            GetDeviceLayoutName(device)
        );
        return new ArtemisLayout(filePath);
    }

    /// <summary>
    ///     Called when a specific RGB device's logical and physical layout must be detected
    ///     <para>
    ///         Note: Only called when <see cref="CanDetectLogicalLayout" /> is <see langword="true" />.
    ///     </para>
    /// </summary>
    /// <param name="keyboard">The device to detect the layout for, always a keyboard</param>
    public virtual string GetLogicalLayout(IKeyboard keyboard)
    {
        throw new NotImplementedException("Device provider does not support detecting logical layouts (don't call base.GetLogicalLayout())");
    }

    /// <summary>
    ///     Called when determining which file name to use when loading the layout of the specified
    ///     <paramref name="device"></paramref>.
    /// </summary>
    /// <param name="device">The device to determine the layout file name for.</param>
    /// <returns>A file name, including an extension</returns>
    public virtual string GetDeviceLayoutName(ArtemisDevice device)
    {
        // Take out invalid file name chars, may not be perfect but neither are you
        string fileName = Path.GetInvalidFileNameChars().Aggregate(device.RgbDevice.DeviceInfo.Model, (current, c) => current.Replace(c, '-'));
        if (device.RgbDevice.DeviceInfo.DeviceType == RGBDeviceType.Keyboard)
            fileName = $"{fileName}-{device.PhysicalLayout.ToString().ToUpper()}";

        return fileName + ".xml";
    }

    /// <summary>
    /// Called when the device provider is being suspended, like when the system is going to sleep.
    /// Note: This will be called while the plugin is disabled.
    /// </summary>
    public virtual void Suspend()
    {
    }
}
