using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Artemis.Core.DeviceProviders;
using Artemis.Core.Providers;
using Artemis.Core.Services.Models;
using Artemis.Storage.Entities.Surface;
using Artemis.Storage.Repositories.Interfaces;
using RGB.NET.Core;
using Serilog;

namespace Artemis.Core.Services;

internal class DeviceService : IDeviceService
{
    private readonly ILogger _logger;
    private readonly IPluginManagementService _pluginManagementService;
    private readonly IDeviceRepository _deviceRepository;
    private readonly Lazy<IRenderService> _renderService;
    private readonly Func<List<ILayoutProvider>> _getLayoutProviders;
    private readonly List<ArtemisDevice> _enabledDevices = [];
    private readonly List<ArtemisDevice> _devices = [];
    private readonly object _devicesLock = new();
    private readonly Dictionary<DeviceProvider, (IRGBDeviceProvider Provider, EventHandler<DevicesChangedEventArgs> Handler)> _devicesChangedHandlers = [];
    private readonly object _deviceChangeLock = new();
    private readonly List<DeviceProvider> _suspendedDeviceProviders = [];
    private readonly object _suspensionLock = new();
    private volatile IReadOnlyCollection<ArtemisDevice> _enabledDevicesSnapshot = [];
    private volatile IReadOnlyCollection<ArtemisDevice> _devicesSnapshot = [];

    public DeviceService(ILogger logger,
        IPluginManagementService pluginManagementService,
        IDeviceRepository deviceRepository,
        Lazy<IRenderService> renderService,
        Func<List<ILayoutProvider>> getLayoutProviders)
    {
        _logger = logger;
        _pluginManagementService = pluginManagementService;
        _deviceRepository = deviceRepository;
        _renderService = renderService;
        _getLayoutProviders = getLayoutProviders;

        SuspendedDeviceProviders = new ReadOnlyCollection<DeviceProvider>(_suspendedDeviceProviders);
        RenderScale.RenderScaleMultiplierChanged += RenderScaleOnRenderScaleMultiplierChanged;
    }

    public IReadOnlyCollection<DeviceProvider> SuspendedDeviceProviders { get; }
    public IReadOnlyCollection<ArtemisDevice> EnabledDevices => _enabledDevicesSnapshot;
    public IReadOnlyCollection<ArtemisDevice> Devices => _devicesSnapshot;

    /// <inheritdoc />
    public void IdentifyDevice(ArtemisDevice device)
    {
        BlinkDevice(device, 0);
    }

    /// <inheritdoc />
    public void AddDeviceProvider(DeviceProvider deviceProvider)
    {
        _logger.Verbose("[AddDeviceProvider] Adding {DeviceProvider}", deviceProvider.GetType().Name);
        IRGBDeviceProvider rgbDeviceProvider = deviceProvider.RgbDeviceProvider;

        try
        {
            UnsubscribeFromDevicesChanged(deviceProvider);

            List<Exception> providerExceptions = [];

            void DeviceProviderOnException(object? sender, ExceptionEventArgs e)
            {
                if (e.IsCritical)
                    providerExceptions.Add(e.Exception);
                else
                    _logger.Warning(e.Exception, "Device provider {deviceProvider} threw non-critical exception", deviceProvider.GetType().Name);
            }

            _logger.Verbose("[AddDeviceProvider] Initializing device provider");
            rgbDeviceProvider.Exception += DeviceProviderOnException;
            try
            {
                rgbDeviceProvider.Initialize();
            }
            finally
            {
                rgbDeviceProvider.Exception -= DeviceProviderOnException;
            }
            _logger.Verbose("[AddDeviceProvider] Attaching devices of device provider");
            if (providerExceptions.Count == 1)
                throw new ArtemisPluginException("RGB.NET threw exception: " + providerExceptions.First().Message, providerExceptions.First());
            if (providerExceptions.Count > 1)
                throw new ArtemisPluginException("RGB.NET threw multiple exceptions", new AggregateException(providerExceptions));

            SubscribeToDevicesChanged(deviceProvider, rgbDeviceProvider);

            if (!rgbDeviceProvider.Devices.Any())
            {
                _logger.Warning("Device provider {deviceProvider} has no devices", deviceProvider.GetType().Name);
                return;
            }

            List<ArtemisDevice> addedDevices = [];
            List<(ArtemisDevice Device, bool TopologyPreserved)> reconnectedDevices = [];
            foreach (IRGBDevice rgbDevice in rgbDeviceProvider.Devices)
            {
                string identifier = deviceProvider.GetDeviceIdentifier(rgbDevice);
                ArtemisDevice? retained;
                lock (_devicesLock)
                    retained = _devices.FirstOrDefault(d => IsSameProvider(d.DeviceProvider, deviceProvider) &&
                                                            d.Identifier == identifier);

                if (retained != null)
                {
                    if (retained.IsConnected && !ReferenceEquals(retained.RgbDevice, rgbDevice))
                    {
                        OnDeviceDisconnected(new DeviceEventArgs(retained));
                        retained.Disconnect();
                    }
                    reconnectedDevices.Add((retained, retained.Rebind(rgbDevice, deviceProvider)));
                }
                else
                    addedDevices.Add(GetArtemisDevice(rgbDevice, deviceProvider));
                _logger.Debug("Device provider {deviceProvider} added {deviceName}", deviceProvider.GetType().Name, rgbDevice.DeviceInfo.DeviceName);
            }

            lock (_devicesLock)
            {
                _devices.AddRange(addedDevices);
                _enabledDevices.AddRange(addedDevices.Where(d => d.IsEnabled));
                _devices.Sort((a, b) => a.ZIndex - b.ZIndex);
                _enabledDevices.Sort((a, b) => a.ZIndex - b.ZIndex);
                UpdateDeviceSnapshots();
            }

            OnDeviceProviderAdded(new DeviceProviderEventArgs(deviceProvider, addedDevices));
            foreach (ArtemisDevice artemisDevice in addedDevices)
                OnDeviceAdded(new DeviceEventArgs(artemisDevice));
            foreach ((ArtemisDevice device, bool topologyPreserved) in reconnectedDevices)
            {
                OnDeviceReconnected(new DeviceEventArgs(device));
                _logger.Information("Device provider {DeviceProvider} rebound {Device} to retained Artemis identity {Identifier} after provider reload (LED topology preserved: {TopologyPreserved})",
                    deviceProvider.GetType().Name, device.RgbDevice.DeviceInfo.DeviceName, device.Identifier, topologyPreserved);
            }

            if (addedDevices.Count > 0 || reconnectedDevices.Any(d => !d.TopologyPreserved))
                UpdateLeds();
        }
        catch (Exception e)
        {
            _logger.Error(e, "Exception during device loading for device provider {deviceProvider}", deviceProvider.GetType().Name);
            throw;
        }
    }

    /// <inheritdoc />
    public void RemoveDeviceProvider(DeviceProvider deviceProvider)
    {
        _logger.Verbose("[RemoveDeviceProvider] Pausing rendering to remove {DeviceProvider}", deviceProvider.GetType().Name);
        List<ArtemisDevice> toDisconnect;

        try
        {
            UnsubscribeFromDevicesChanged(deviceProvider);
            lock (_devicesLock)
                toDisconnect = _devices.Where(a => ReferenceEquals(a.DeviceProvider, deviceProvider) && a.IsConnected).ToList();

            _logger.Verbose("[RemoveDeviceProvider] Disconnecting {Count} device(s) while retaining their logical identities", toDisconnect.Count);
            foreach (ArtemisDevice artemisDevice in toDisconnect)
            {
                OnDeviceDisconnected(new DeviceEventArgs(artemisDevice));
                artemisDevice.Disconnect();
            }

            // Provider removal is no longer device removal. Consumers that care about
            // provider availability still receive the event, but surface/profile state
            // stays attached to the retained logical devices.
            OnDeviceProviderRemoved(new DeviceProviderEventArgs(deviceProvider, []));
        }
        catch (Exception e)
        {
            _logger.Error(e, "Exception during device removal for device provider {deviceProvider}", deviceProvider.GetType().Name);
            throw;
        }
    }

    /// <param name="leftHanded"></param>
    /// <inheritdoc />
    public void AutoArrangeDevices(bool leftHanded)
    {
        SurfaceArrangement surfaceArrangement = SurfaceArrangement.GetDefaultArrangement(leftHanded);
        IReadOnlyCollection<ArtemisDevice> devices = Devices;
        surfaceArrangement.Arrange(devices.ToList());
        foreach (ArtemisDevice artemisDevice in devices)
            artemisDevice.ApplyDefaultCategories();

        SaveDevices();
    }

    /// <inheritdoc />
    public void LoadDeviceLayout(ArtemisDevice device)
    {
        List<ILayoutProvider> layoutProviders = _getLayoutProviders();
        ILayoutProvider? provider = layoutProviders.FirstOrDefault(p => p.IsMatch(device));
        if (provider == null)
            _logger.Warning("Could not find a layout provider for type {LayoutType} of device {Device}", device.LayoutSelection.Type, device);

        ArtemisLayout? layout = provider?.GetDeviceLayout(device);
        if (layout == null && provider is not DefaultLayoutProvider)
        {
            // A restored database can still reference a Workshop/custom layout whose
            // external files are no longer installed. Keep that selection intact so it
            // becomes active again if the files return, but use the device plugin's
            // bundled layout in the meantime instead of leaving the device unusable.
            DefaultLayoutProvider? fallbackProvider = layoutProviders.OfType<DefaultLayoutProvider>().FirstOrDefault();
            ArtemisLayout? fallbackLayout = fallbackProvider?.GetDeviceLayout(device);
            if (fallbackLayout != null && fallbackLayout.IsValid)
            {
                _logger.Warning("Layout {LayoutType}:{LayoutParameter} is unavailable for {Device}; using the bundled/default layout without changing the saved selection",
                    device.LayoutSelection.Type, device.LayoutSelection.Parameter, device);
                provider = fallbackProvider;
                layout = fallbackLayout;
            }
        }

        if (layout != null && !layout.IsValid)
        {
            _logger.Warning("Got an invalid layout {Layout} from {LayoutProvider}", layout, provider!.GetType().FullName);
            layout = null;
        }

        try
        {
            if (layout == null)
                device.ApplyLayout(null, false, false);
            else
                provider?.ApplyLayout(device, layout);

            UpdateLeds();
        }
        catch (Exception e)
        {
            device.LayoutSelection.ErrorState = e.Message;
            _logger.Error(e, "Failed to apply device layout");
        }
    }

    /// <inheritdoc />
    public void EnableDevice(ArtemisDevice device)
    {
        lock (_devicesLock)
        {
            if (device.IsEnabled)
                return;
            _enabledDevices.Add(device);
            device.IsEnabled = true;
            UpdateDeviceSnapshots();
        }
        device.Save();
        _deviceRepository.Save(device.DeviceEntity);

        OnDeviceEnabled(new DeviceEventArgs(device));
        UpdateLeds();
    }

    /// <inheritdoc />
    public void DisableDevice(ArtemisDevice device)
    {
        lock (_devicesLock)
        {
            if (!device.IsEnabled)
                return;
            _enabledDevices.Remove(device);
            device.IsEnabled = false;
            UpdateDeviceSnapshots();
        }
        device.Save();
        _deviceRepository.Save(device.DeviceEntity);

        OnDeviceDisabled(new DeviceEventArgs(device));
        UpdateLeds();
    }

    /// <inheritdoc />
    public void SaveDevice(ArtemisDevice artemisDevice)
    {
        artemisDevice.Save();
        _deviceRepository.Save(artemisDevice.DeviceEntity);
        UpdateLeds();
    }

    /// <inheritdoc />
    public void SaveDevices()
    {
        IReadOnlyCollection<ArtemisDevice> devices = Devices;
        foreach (ArtemisDevice artemisDevice in devices)
            artemisDevice.Save();
        _deviceRepository.SaveRange(devices.Select(d => d.DeviceEntity));
        UpdateLeds();
    }

    /// <inheritdoc />
    public void SuspendDeviceProviders()
    {
        lock (_suspensionLock)
        {
            _logger.Information("Suspending all device providers");

            bool wasPaused = _renderService.Value.IsPaused;
            try
            {
                _renderService.Value.IsPaused = true;
                foreach (DeviceProvider deviceProvider in _pluginManagementService.GetFeaturesOfType<DeviceProvider>().Where(d => d.SuspendSupported))
                    SuspendDeviceProvider(deviceProvider);
            }
            finally
            {
                _renderService.Value.IsPaused = wasPaused;
            }
        }
    }

    /// <inheritdoc />
    public void ResumeDeviceProviders()
    {
        lock (_suspensionLock)
        {
            _logger.Information("Resuming all device providers");

            bool wasPaused = _renderService.Value.IsPaused;
            try
            {
                _renderService.Value.IsPaused = true;
                foreach (DeviceProvider deviceProvider in _suspendedDeviceProviders.ToList())
                    ResumeDeviceProvider(deviceProvider);
            }
            finally
            {
                _renderService.Value.IsPaused = wasPaused;
            }
        }
    }

    private void SuspendDeviceProvider(DeviceProvider deviceProvider)
    {
        if (_suspendedDeviceProviders.Contains(deviceProvider))
        {
            _logger.Warning("Device provider {DeviceProvider} is already suspended", deviceProvider.Info.Name);
            return;
        }

        try
        {
            _pluginManagementService.DisablePluginFeature(deviceProvider, false);
            deviceProvider.Suspend();
            _suspendedDeviceProviders.Add(deviceProvider);
            _logger.Information("Device provider {DeviceProvider} suspended", deviceProvider.Info.Name);
        }
        catch (Exception e)
        {
            _logger.Error(e, "Device provider {DeviceProvider} failed to suspend", deviceProvider.Info.Name);
        }
    }

    private void ResumeDeviceProvider(DeviceProvider deviceProvider)
    {
        try
        {
            _pluginManagementService.EnablePluginFeature(deviceProvider, false, true);
            _suspendedDeviceProviders.Remove(deviceProvider);
            _logger.Information("Device provider {DeviceProvider} resumed", deviceProvider.Info.Name);
        }
        catch (Exception e)
        {
            _logger.Error(e, "Device provider {DeviceProvider} failed to resume", deviceProvider.Info.Name);
        }
    }

    private ArtemisDevice GetArtemisDevice(IRGBDevice rgbDevice, DeviceProvider deviceProvider)
    {
        string deviceIdentifier = deviceProvider.GetDeviceIdentifier(rgbDevice);
        DeviceEntity? deviceEntity = _deviceRepository.Get(deviceIdentifier);
        string legacyIdentifier = rgbDevice.GetDeviceIdentifier();
        if (deviceEntity == null && legacyIdentifier != deviceIdentifier)
        {
            deviceEntity = _deviceRepository.Rename(legacyIdentifier, deviceIdentifier);
            if (deviceEntity != null)
                _logger.Information("Migrated device identity {LegacyIdentifier} to provider identity {Identifier}", legacyIdentifier, deviceIdentifier);
        }

        ArtemisDevice device;
        if (deviceEntity != null)
        {
            device = new ArtemisDevice(rgbDevice, deviceProvider, deviceEntity);
            // Profiles are stored separately from device configuration and may retain the old
            // identifier for an arbitrary amount of time. Keep accepting it on every startup,
            // not just during the one run in which the device row was migrated.
            if (legacyIdentifier != deviceIdentifier)
                device.AddIdentifierAlias(legacyIdentifier);
        }
        // Fall back on creating a new device
        else
        {
            _logger.Information("No device config found for {DeviceInfo}, device hash: {DeviceHashCode}. Adding a new entry", rgbDevice.DeviceInfo, deviceIdentifier);
            device = new ArtemisDevice(rgbDevice, deviceProvider);
            _deviceRepository.Add(device.DeviceEntity);
        }

        LoadDeviceLayout(device);
        return device;
    }

    private void SubscribeToDevicesChanged(DeviceProvider deviceProvider, IRGBDeviceProvider rgbDeviceProvider)
    {
        void Handler(object? sender, DevicesChangedEventArgs e)
        {
            lock (_deviceChangeLock)
            {
                try
                {
                    if (!deviceProvider.IsEnabled)
                        return;

                    if (e.Action == DevicesChangedEventArgs.DevicesChangedAction.Added)
                        HandleRuntimeDeviceAdded(deviceProvider, e.Device);
                    else
                        HandleRuntimeDeviceRemoved(deviceProvider, e.Device);
                }
                catch (Exception exception)
                {
                    _logger.Error(exception, "Failed to process runtime device change from {DeviceProvider} ({Action} {Device})",
                        deviceProvider.GetType().Name, e.Action, e.Device.DeviceInfo.DeviceName);
                }
            }
        }

        UnsubscribeFromDevicesChanged(deviceProvider);
        rgbDeviceProvider.DevicesChanged += Handler;
        _devicesChangedHandlers[deviceProvider] = (rgbDeviceProvider, Handler);
    }

    private void UnsubscribeFromDevicesChanged(DeviceProvider deviceProvider)
    {
        if (!_devicesChangedHandlers.Remove(deviceProvider, out (IRGBDeviceProvider Provider, EventHandler<DevicesChangedEventArgs> Handler) subscription))
            return;

        subscription.Provider.DevicesChanged -= subscription.Handler;
    }

    private void HandleRuntimeDeviceRemoved(DeviceProvider deviceProvider, IRGBDevice rgbDevice)
    {
        ArtemisDevice? device;
        lock (_devicesLock)
            device = _devices.FirstOrDefault(d => ReferenceEquals(d.DeviceProvider, deviceProvider) && ReferenceEquals(d.RgbDevice, rgbDevice));
        if (device == null || !device.IsConnected)
            return;

        // Detach the dead RGB.NET object from the rendering surface, but retain the logical
        // Artemis device and LEDs used by profiles. A matching add event can rebind them later.
        OnDeviceDisconnected(new DeviceEventArgs(device));
        device.Disconnect();
        _logger.Information("Device provider {DeviceProvider} disconnected {Device}; retaining Artemis identity {Identifier}",
            deviceProvider.GetType().Name, rgbDevice.DeviceInfo.DeviceName, device.Identifier);
    }

    private void HandleRuntimeDeviceAdded(DeviceProvider deviceProvider, IRGBDevice rgbDevice)
    {
        string identifier = deviceProvider.GetDeviceIdentifier(rgbDevice);
        ArtemisDevice? existing;
        lock (_devicesLock)
            existing = _devices.FirstOrDefault(d => ReferenceEquals(d.DeviceProvider, deviceProvider) && d.Identifier == identifier);
        if (existing != null)
        {
            if (ReferenceEquals(existing.RgbDevice, rgbDevice))
                return;

            if (existing.IsConnected)
            {
                OnDeviceDisconnected(new DeviceEventArgs(existing));
                existing.Disconnect();
                _logger.Warning("Device provider {DeviceProvider} added replacement {Device} before removing the previous RGB.NET object for Artemis identity {Identifier}; rebinding the existing logical device",
                    deviceProvider.GetType().Name, rgbDevice.DeviceInfo.DeviceName, identifier);
            }

            bool topologyPreserved = existing.Rebind(rgbDevice);
            OnDeviceReconnected(new DeviceEventArgs(existing));
            if (!topologyPreserved)
                UpdateLeds();

            _logger.Information("Device provider {DeviceProvider} reconnected {Device} to retained Artemis identity {Identifier} (LED topology preserved: {TopologyPreserved})",
                deviceProvider.GetType().Name, rgbDevice.DeviceInfo.DeviceName, identifier, topologyPreserved);
            return;
        }

        ArtemisDevice addedDevice = GetArtemisDevice(rgbDevice, deviceProvider);
        lock (_devicesLock)
        {
            _devices.Add(addedDevice);
            if (addedDevice.IsEnabled)
                _enabledDevices.Add(addedDevice);
            _devices.Sort((a, b) => a.ZIndex - b.ZIndex);
            _enabledDevices.Sort((a, b) => a.ZIndex - b.ZIndex);
            UpdateDeviceSnapshots();
        }

        OnDeviceAdded(new DeviceEventArgs(addedDevice));
        UpdateLeds();
        _logger.Information("Device provider {DeviceProvider} added runtime device {Device} with Artemis identity {Identifier}",
            deviceProvider.GetType().Name, rgbDevice.DeviceInfo.DeviceName, identifier);
    }

    private void UpdateDeviceSnapshots()
    {
        _devicesSnapshot = _devices.ToList().AsReadOnly();
        _enabledDevicesSnapshot = _enabledDevices.ToList().AsReadOnly();
    }

    private static bool IsSameProvider(DeviceProvider first, DeviceProvider second)
    {
        if (first.Plugin != null && second.Plugin != null)
            return first.Plugin.Guid == second.Plugin.Guid;

        // Unit-test and development providers may not have an owning Plugin assigned.
        return first.GetType().FullName == second.GetType().FullName;
    }

    private void BlinkDevice(ArtemisDevice device, int blinkCount)
    {
        if (!device.IsConnected)
            return;

        RGBSurface surface = _renderService.Value.Surface;

        // Create a LED group way at the top
        ListLedGroup ledGroup = new(surface, device.Leds.Select(l => l.RgbLed))
        {
            Brush = new SolidColorBrush(new Color(255, 255, 255)),
            ZIndex = 999
        };

        // After 200ms, detach the LED group
        Task.Run(async () =>
        {
            await Task.Delay(200);
            ledGroup.Detach();

            if (blinkCount < 5)
            {
                // After another 200ms, start over, repeat six times
                await Task.Delay(200);
                BlinkDevice(device, blinkCount + 1);
            }
        });
    }

    private void CalculateRenderProperties()
    {
        foreach (ArtemisDevice artemisDevice in Devices)
            artemisDevice.CalculateRenderProperties();
        UpdateLeds();
    }

    private void UpdateLeds()
    {
        OnLedsChanged();
    }

    private void RenderScaleOnRenderScaleMultiplierChanged(object? sender, EventArgs e)
    {
        CalculateRenderProperties();
    }

    #region Events

    /// <inheritdoc />
    public event EventHandler<DeviceEventArgs>? DeviceAdded;

    /// <inheritdoc />
    public event EventHandler<DeviceEventArgs>? DeviceRemoved;

    /// <inheritdoc />
    public event EventHandler<DeviceEventArgs>? DeviceDisconnected;

    /// <inheritdoc />
    public event EventHandler<DeviceEventArgs>? DeviceReconnected;

    /// <inheritdoc />
    public event EventHandler<DeviceEventArgs>? DeviceEnabled;

    /// <inheritdoc />
    public event EventHandler<DeviceEventArgs>? DeviceDisabled;

    /// <inheritdoc />
    public event EventHandler<DeviceProviderEventArgs>? DeviceProviderAdded;

    /// <inheritdoc />
    public event EventHandler<DeviceProviderEventArgs>? DeviceProviderRemoved;

    /// <inheritdoc />
    public event EventHandler? LedsChanged;

    protected virtual void OnDeviceAdded(DeviceEventArgs e)
    {
        DeviceAdded?.Invoke(this, e);
    }

    protected virtual void OnDeviceRemoved(DeviceEventArgs e)
    {
        DeviceRemoved?.Invoke(this, e);
    }

    protected virtual void OnDeviceDisconnected(DeviceEventArgs e)
    {
        DeviceDisconnected?.Invoke(this, e);
    }

    protected virtual void OnDeviceReconnected(DeviceEventArgs e)
    {
        DeviceReconnected?.Invoke(this, e);
    }

    protected virtual void OnDeviceEnabled(DeviceEventArgs e)
    {
        DeviceEnabled?.Invoke(this, e);
    }

    protected virtual void OnDeviceDisabled(DeviceEventArgs e)
    {
        DeviceDisabled?.Invoke(this, e);
    }

    protected virtual void OnDeviceProviderAdded(DeviceProviderEventArgs e)
    {
        DeviceProviderAdded?.Invoke(this, e);
    }

    protected virtual void OnDeviceProviderRemoved(DeviceProviderEventArgs e)
    {
        DeviceProviderRemoved?.Invoke(this, e);
    }

    protected virtual void OnLedsChanged()
    {
        LedsChanged?.Invoke(this, EventArgs.Empty);
    }

    #endregion
}
