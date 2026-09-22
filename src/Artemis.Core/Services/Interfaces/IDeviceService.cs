using System;
using System.Collections.Generic;
using Artemis.Core.DeviceProviders;
using Artemis.Storage.Entities.Surface;

namespace Artemis.Core.Services;

/// <summary>
///     A service that allows you manage an <see cref="ArtemisDevice" />
/// </summary>
public interface IDeviceService : IArtemisService
{
    /// <summary>
    ///     Gets a read-only collection containing all enabled but suspended device providers
    /// </summary>
    IReadOnlyCollection<DeviceProvider> SuspendedDeviceProviders { get; }

    /// <summary>
    ///     Gets a read-only collection containing all enabled devices
    /// </summary>
    IReadOnlyCollection<ArtemisDevice> EnabledDevices { get; }

    /// <summary>
    ///     Gets a read-only collection containing all currently connected devices.
    ///     Disconnected logical identities are retained internally so their bindings can be restored.
    /// </summary>
    IReadOnlyCollection<ArtemisDevice> Devices { get; }

    /// <summary>
    ///     Gets disconnected devices whose persistent identities and bindings are retained for reconnection.
    /// </summary>
    IReadOnlyCollection<ArtemisDevice> MissingDevices { get; }

    /// <summary>
    ///     Gets saved device records which have not been claimed by a connected device in this session.
    /// </summary>
    IReadOnlyCollection<DeviceEntity> MissingStoredDevices { get; }

    /// <summary>
    ///     Identifies the device by making it blink white 5 times
    /// </summary>
    /// <param name="device"></param>
    void IdentifyDevice(ArtemisDevice device);

    /// <summary>
    ///     Adds the given device provider and its devices.
    /// </summary>
    /// <param name="deviceProvider"></param>
    void AddDeviceProvider(DeviceProvider deviceProvider);

    /// <summary>
    ///     Removes the given device provider and its devices.
    /// </summary>
    /// <param name="deviceProvider"></param>
    void RemoveDeviceProvider(DeviceProvider deviceProvider);

    /// <summary>
    ///     Applies auto-arranging logic to the surface
    /// </summary>
    /// <param name="leftHanded"></param>
    void AutoArrangeDevices(bool leftHanded);

    /// <summary>
    ///     Apples the best available to the provided <see cref="ArtemisDevice" />
    /// </summary>
    /// <param name="device"></param>
    void LoadDeviceLayout(ArtemisDevice device);

    /// <summary>
    ///     Enables the provided device
    /// </summary>
    /// <param name="device">The device to enable</param>
    void EnableDevice(ArtemisDevice device);

    /// <summary>
    ///     Disables the provided device
    /// </summary>
    /// <param name="device">The device to disable</param>
    void DisableDevice(ArtemisDevice device);

    /// <summary>
    ///     Saves the configuration of the provided device to persistent storage
    /// </summary>
    /// <param name="artemisDevice"></param>
    void SaveDevice(ArtemisDevice artemisDevice);

    /// <summary>
    ///     Permanently removes a missing device and notifies consumers to purge its saved bindings.
    /// </summary>
    void ForgetDevice(ArtemisDevice artemisDevice);

    /// <summary>
    ///     Permanently removes a saved device which has not appeared in this session.
    /// </summary>
    void ForgetDevice(DeviceEntity deviceEntity);

    /// <summary>
    ///     Saves the configuration of all current devices to persistent storage
    /// </summary>
    void SaveDevices();

    /// <summary>
    ///     Suspends all active device providers
    /// </summary>
    void SuspendDeviceProviders();

    /// <summary>
    ///     Resumes all previously active device providers
    /// </summary>
    void ResumeDeviceProviders();

    /// <summary>
    ///     Occurs when a single device was added.
    /// </summary>
    event EventHandler<DeviceEventArgs> DeviceAdded;

    /// <summary>
    ///     Occurs when a single device was removed.
    /// </summary>
    event EventHandler<DeviceEventArgs> DeviceRemoved;

    /// <summary>
    ///     Occurs when a device temporarily loses its live provider backing while its logical Artemis identity is retained.
    /// </summary>
    event EventHandler<DeviceEventArgs> DeviceDisconnected;

    /// <summary>
    ///     Occurs when a disconnected device receives a new live provider backing.
    /// </summary>
    event EventHandler<DeviceEventArgs> DeviceReconnected;

    /// <summary>
    ///     Occurs after a missing device has been permanently removed from storage.
    /// </summary>
    event EventHandler<DeviceEventArgs> DeviceForgotten;

    /// <summary>
    ///     Occurs after an unclaimed saved device has been permanently removed.
    /// </summary>
    event EventHandler<StoredDeviceEventArgs> StoredDeviceForgotten;

    /// <summary>
    ///     Occurs when the set of unclaimed saved device records changes.
    /// </summary>
    event EventHandler MissingStoredDevicesChanged;

    /// <summary>
    ///     Occurs when a single device was disabled
    /// </summary>
    event EventHandler<DeviceEventArgs> DeviceEnabled;

    /// <summary>
    ///     Occurs when a single device was disabled.
    /// </summary>
    event EventHandler<DeviceEventArgs> DeviceDisabled;

    /// <summary>
    ///     Occurs when a device provider was added.
    /// </summary>
    event EventHandler<DeviceProviderEventArgs> DeviceProviderAdded;

    /// <summary>
    ///     Occurs when a device provider was removed.
    /// </summary>
    event EventHandler<DeviceProviderEventArgs> DeviceProviderRemoved;

    /// <summary>
    ///     Occurs when the surface has had modifications to its LED collection
    /// </summary>
    event EventHandler LedsChanged;
}
