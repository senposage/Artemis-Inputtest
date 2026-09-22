using System;
using Artemis.Storage.Entities.Surface;

namespace Artemis.Core;

/// <summary>
///     Provides data for events concerning a saved device which is not currently connected.
/// </summary>
public sealed class StoredDeviceEventArgs : EventArgs
{
    internal StoredDeviceEventArgs(DeviceEntity deviceEntity)
    {
        DeviceEntity = deviceEntity;
    }

    /// <summary>
    ///     Gets the saved device record.
    /// </summary>
    public DeviceEntity DeviceEntity { get; }
}
