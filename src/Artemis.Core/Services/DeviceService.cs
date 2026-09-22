using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
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
    private readonly List<ArtemisDevice> _retainedDevices = [];
    private readonly List<DeviceEntity> _missingStoredDevices;
    private readonly object _devicesLock = new();
    private readonly Dictionary<DeviceProvider, (IRGBDeviceProvider Provider, EventHandler<DevicesChangedEventArgs> Handler)> _devicesChangedHandlers = [];
    private readonly Dictionary<ArtemisDevice, CancellationTokenSource> _pendingDeviceRemovals = [];
    private readonly object _deviceChangeLock = new();
    private readonly List<DeviceProvider> _suspendedDeviceProviders = [];
    private readonly object _suspensionLock = new();
    private volatile IReadOnlyCollection<ArtemisDevice> _enabledDevicesSnapshot = [];
    private volatile IReadOnlyCollection<ArtemisDevice> _devicesSnapshot = [];
    private volatile IReadOnlyCollection<ArtemisDevice> _missingDevicesSnapshot = [];
    private volatile IReadOnlyCollection<DeviceEntity> _missingStoredDevicesSnapshot = [];

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
        _missingStoredDevices = _deviceRepository.GetAll() ?? [];
        UpdateMissingStoredDevicesSnapshot();

        SuspendedDeviceProviders = new ReadOnlyCollection<DeviceProvider>(_suspendedDeviceProviders);
        RenderScale.RenderScaleMultiplierChanged += RenderScaleOnRenderScaleMultiplierChanged;
    }

    public IReadOnlyCollection<DeviceProvider> SuspendedDeviceProviders { get; }
    public IReadOnlyCollection<ArtemisDevice> EnabledDevices => _enabledDevicesSnapshot;
    public IReadOnlyCollection<ArtemisDevice> Devices => _devicesSnapshot;
    public IReadOnlyCollection<ArtemisDevice> MissingDevices => _missingDevicesSnapshot;
    public IReadOnlyCollection<DeviceEntity> MissingStoredDevices => _missingStoredDevicesSnapshot;

    // Providers such as OpenRGB can briefly withdraw their entire device list while
    // rebuilding it after one USB device changes. Keep those transient removals out of
    // the public Missing collection while still detaching their dead RGB.NET objects
    // from the renderer immediately.
    internal TimeSpan DeviceRemovalGracePeriod { get; set; } = TimeSpan.FromSeconds(2);

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

            List<ArtemisDevice> addedDevices = [];
            List<(ArtemisDevice Device, bool TopologyPreserved, bool WasHidden)> reconnectedDevices = [];
            lock (_deviceChangeLock)
            {
                SubscribeToDevicesChanged(deviceProvider, rgbDeviceProvider);

                List<IRGBDevice> providerDevices = rgbDeviceProvider.Devices.ToList();
                if (!providerDevices.Any())
                {
                    _logger.Warning("Device provider {deviceProvider} has no devices", deviceProvider.GetType().Name);
                    return;
                }

                // A provider rescan may briefly expose both the old and replacement RGB.NET objects.
                // Treat the provider identity as authoritative and only attach the newest object for it.
                List<IGrouping<string, IRGBDevice>> deviceGroups = providerDevices
                    .GroupBy(deviceProvider.GetDeviceIdentifier)
                    .ToList();
                foreach (IGrouping<string, IRGBDevice> duplicate in deviceGroups.Where(g => g.Count() > 1))
                    _logger.Warning("Device provider {DeviceProvider} exposed {Count} objects for Artemis identity {Identifier}; attaching one logical device",
                        deviceProvider.GetType().Name, duplicate.Count(), duplicate.Key);

                foreach (IRGBDevice rgbDevice in deviceGroups.Select(g => g.Last()))
                {
                    string identifier = deviceProvider.GetDeviceIdentifier(rgbDevice);
                    ArtemisDevice? retained;
                    bool wasHidden;
                    lock (_devicesLock)
                    {
                        retained = _devices.Concat(_retainedDevices).FirstOrDefault(d => IsSameProvider(d.DeviceProvider, deviceProvider) &&
                                                                                        d.Identifier == identifier);
                        wasHidden = retained != null && _retainedDevices.Contains(retained);
                    }

                    if (retained != null)
                    {
                        if (retained.IsConnected && !ReferenceEquals(retained.RgbDevice, rgbDevice))
                        {
                            OnDeviceDisconnected(new DeviceEventArgs(retained));
                            retained.Disconnect();
                        }
                        bool topologyPreserved = retained.Rebind(rgbDevice, deviceProvider);
                        ActivateRetainedDevice(retained);
                        reconnectedDevices.Add((retained, topologyPreserved, wasHidden));
                    }
                    else
                        addedDevices.Add(GetArtemisDevice(rgbDevice, deviceProvider));
                    _logger.Debug("Device provider {deviceProvider} added {deviceName}", deviceProvider.GetType().Name, rgbDevice.DeviceInfo.DeviceName);
                }

                lock (_devicesLock)
                {
                    _devices.AddRange(addedDevices);
                    _enabledDevices.AddRange(addedDevices.Where(d => d.IsEnabled));
                    SortDevicesAndUpdateSnapshots();
                }
            }

            OnDeviceProviderAdded(new DeviceProviderEventArgs(deviceProvider, addedDevices));
            foreach (ArtemisDevice artemisDevice in addedDevices)
                OnDeviceAdded(new DeviceEventArgs(artemisDevice));
            foreach ((ArtemisDevice device, bool topologyPreserved, bool wasHidden) in reconnectedDevices)
            {
                if (wasHidden)
                    OnDeviceAdded(new DeviceEventArgs(device));
                OnDeviceReconnected(new DeviceEventArgs(device));
                _logger.Information("Device provider {DeviceProvider} rebound {Device} to retained Artemis identity {Identifier} after provider reload (LED topology preserved: {TopologyPreserved})",
                    deviceProvider.GetType().Name, device.RgbDevice.DeviceInfo.DeviceName, device.Identifier, topologyPreserved);
            }

            // A hidden/missing device has had its profile LEDs moved into each layer's
            // deferred binding list. Repopulate those bindings even when the physical LED
            // topology is identical to the device that disappeared.
            if (addedDevices.Count > 0 || reconnectedDevices.Any(d => d.WasHidden || !d.TopologyPreserved))
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
                RetainDisconnectedDevice(artemisDevice);
                OnDeviceRemoved(new DeviceEventArgs(artemisDevice));
            }

            // The devices disappear from public/UI collections, while their logical objects
            // and profile bindings remain retained for a future provider reload.
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
        List<ArtemisDevice> devices;
        lock (_devicesLock)
            devices = _devices.Concat(_retainedDevices).Distinct().ToList();

        foreach (ArtemisDevice artemisDevice in devices)
            artemisDevice.Save();
        // Device IDs are a hard identity boundary. Keep persistence safe even if a buggy
        // third-party provider manages to report the same identity more than once.
        _deviceRepository.SaveRange(devices.Select(d => d.DeviceEntity).DistinctBy(e => e.Id));
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
            bool claimedStoredDevice;
            lock (_devicesLock)
            {
                claimedStoredDevice = _missingStoredDevices.RemoveAll(entity => entity.Id == deviceEntity.Id || entity.Id == legacyIdentifier) > 0;
                if (claimedStoredDevice)
                    UpdateMissingStoredDevicesSnapshot();
            }
            if (claimedStoredDevice)
                OnMissingStoredDevicesChanged();

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

    /// <inheritdoc />
    public void ForgetDevice(ArtemisDevice artemisDevice)
    {
        lock (_deviceChangeLock)
        {
            lock (_devicesLock)
            {
                if (!_retainedDevices.Contains(artemisDevice))
                    throw new InvalidOperationException("Only missing devices can be permanently forgotten");
            }

            _deviceRepository.Remove(artemisDevice.DeviceEntity);
            lock (_devicesLock)
            {
                _retainedDevices.Remove(artemisDevice);
                UpdateDeviceSnapshots();
            }
        }

        OnDeviceForgotten(new DeviceEventArgs(artemisDevice));
        _logger.Information("Permanently forgot missing device {Device} with Artemis identity {Identifier}",
            artemisDevice.RgbDevice.DeviceInfo.DeviceName, artemisDevice.Identifier);
    }

    /// <inheritdoc />
    public void ForgetDevice(DeviceEntity deviceEntity)
    {
        lock (_deviceChangeLock)
        {
            DeviceEntity storedEntity;
            lock (_devicesLock)
                storedEntity = _missingStoredDevices.FirstOrDefault(entity => entity.Id == deviceEntity.Id)
                               ?? throw new InvalidOperationException("Only missing saved devices can be permanently forgotten");

            _deviceRepository.Remove(storedEntity);
            lock (_devicesLock)
            {
                _missingStoredDevices.Remove(storedEntity);
                UpdateMissingStoredDevicesSnapshot();
            }
        }

        OnStoredDeviceForgotten(new StoredDeviceEventArgs(deviceEntity));
        OnMissingStoredDevicesChanged();
        _logger.Information("Permanently forgot unclaimed saved device with Artemis identity {Identifier}", deviceEntity.Id);
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
        string identifier = deviceProvider.GetDeviceIdentifier(rgbDevice);
        ArtemisDevice? device;
        lock (_devicesLock)
        {
            device = _devices.FirstOrDefault(d => ReferenceEquals(d.DeviceProvider, deviceProvider) && ReferenceEquals(d.RgbDevice, rgbDevice));
            if (device == null)
            {
                // Some providers reconstruct the object used in their remove notification.
                // Fall back to stable identity, but only when no live object with that identity
                // remains. This preserves the add-before-remove rescan case.
                ArtemisDevice? identityMatch = _devices.FirstOrDefault(d => IsSameProvider(d.DeviceProvider, deviceProvider) && d.Identifier == identifier);
                bool replacementStillPresent = identityMatch != null && deviceProvider.RgbDeviceProvider.Devices.Any(candidate =>
                    ReferenceEquals(candidate, identityMatch.RgbDevice) ||
                    (!ReferenceEquals(candidate, rgbDevice) && deviceProvider.GetDeviceIdentifier(candidate) == identifier));
                if (!replacementStillPresent)
                    device = identityMatch;
            }
        }
        if (device == null || !device.IsConnected)
            return;

        // Stop rendering to the dead RGB.NET object immediately, but wait before publishing
        // the device as missing. Some providers emit a remove/add storm around a rescan.
        OnDeviceDisconnected(new DeviceEventArgs(device));
        device.Disconnect();

        if (_pendingDeviceRemovals.Remove(device, out CancellationTokenSource? previousRemoval))
        {
            previousRemoval.Cancel();
            previousRemoval.Dispose();
        }

        var cancellation = new CancellationTokenSource();
        _pendingDeviceRemovals[device] = cancellation;
        _ = FinalizeRuntimeDeviceRemovalAfterGracePeriod(deviceProvider, rgbDevice, device, cancellation);
        _logger.Information("Device provider {DeviceProvider} temporarily disconnected {Device}; waiting {GracePeriodMs} ms before marking Artemis identity {Identifier} missing",
            deviceProvider.GetType().Name, rgbDevice.DeviceInfo.DeviceName, DeviceRemovalGracePeriod.TotalMilliseconds, device.Identifier);
    }

    private async Task FinalizeRuntimeDeviceRemovalAfterGracePeriod(DeviceProvider deviceProvider, IRGBDevice rgbDevice,
        ArtemisDevice device, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(DeviceRemovalGracePeriod, cancellation.Token);
            lock (_deviceChangeLock)
            {
                if (cancellation.IsCancellationRequested ||
                    !_pendingDeviceRemovals.TryGetValue(device, out CancellationTokenSource? current) ||
                    !ReferenceEquals(current, cancellation) || device.IsConnected)
                    return;

                _pendingDeviceRemovals.Remove(device);
                RetainDisconnectedDevice(device);
                OnDeviceRemoved(new DeviceEventArgs(device));
                UpdateLeds();
                _logger.Information("Device provider {DeviceProvider} disconnected {Device}; retaining Artemis identity {Identifier} as missing",
                    deviceProvider.GetType().Name, rgbDevice.DeviceInfo.DeviceName, device.Identifier);
            }
        }
        catch (OperationCanceledException)
        {
            // The same logical device returned during the grace period.
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Failed to finalize removal of {Device} from {DeviceProvider}",
                rgbDevice.DeviceInfo.DeviceName, deviceProvider.GetType().Name);
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private void HandleRuntimeDeviceAdded(DeviceProvider deviceProvider, IRGBDevice rgbDevice)
    {
        string identifier = deviceProvider.GetDeviceIdentifier(rgbDevice);
        ArtemisDevice? existing;
        bool wasHidden;
        lock (_devicesLock)
        {
            existing = _devices.Concat(_retainedDevices).FirstOrDefault(d => IsSameProvider(d.DeviceProvider, deviceProvider) && d.Identifier == identifier);
            wasHidden = existing != null && _retainedDevices.Contains(existing);
        }
        if (existing != null)
        {
            if (_pendingDeviceRemovals.Remove(existing, out CancellationTokenSource? pendingRemoval))
                pendingRemoval.Cancel();

            if (ReferenceEquals(existing.RgbDevice, rgbDevice) && existing.IsConnected)
                return;

            if (existing.IsConnected)
            {
                OnDeviceDisconnected(new DeviceEventArgs(existing));
                existing.Disconnect();
                _logger.Warning("Device provider {DeviceProvider} added replacement {Device} before removing the previous RGB.NET object for Artemis identity {Identifier}; rebinding the existing logical device",
                    deviceProvider.GetType().Name, rgbDevice.DeviceInfo.DeviceName, identifier);
            }

            bool topologyPreserved = existing.Rebind(rgbDevice);
            ActivateRetainedDevice(existing);
            if (wasHidden)
                OnDeviceAdded(new DeviceEventArgs(existing));
            OnDeviceReconnected(new DeviceEventArgs(existing));
            // Once a device reached the Missing collection, active profile layers moved
            // its LEDs into deferred bindings. A topology-preserving rebind must still
            // notify ProfileService so those LEDs become active again.
            if (wasHidden || !topologyPreserved)
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
            SortDevicesAndUpdateSnapshots();
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
        _missingDevicesSnapshot = _retainedDevices.ToList().AsReadOnly();
    }

    private void UpdateMissingStoredDevicesSnapshot()
    {
        _missingStoredDevicesSnapshot = _missingStoredDevices.ToList().AsReadOnly();
    }

    private void RetainDisconnectedDevice(ArtemisDevice device)
    {
        lock (_devicesLock)
        {
            _devices.Remove(device);
            _enabledDevices.Remove(device);
            if (!_retainedDevices.Contains(device))
                _retainedDevices.Add(device);
            UpdateDeviceSnapshots();
        }
    }

    private void ActivateRetainedDevice(ArtemisDevice device)
    {
        lock (_devicesLock)
        {
            _retainedDevices.Remove(device);
            if (!_devices.Contains(device))
                _devices.Add(device);
            if (device.IsEnabled && !_enabledDevices.Contains(device))
                _enabledDevices.Add(device);
            SortDevicesAndUpdateSnapshots();
        }
    }

    private void SortDevicesAndUpdateSnapshots()
    {
        _devices.Sort((a, b) => a.ZIndex - b.ZIndex);
        _enabledDevices.Sort((a, b) => a.ZIndex - b.ZIndex);
        UpdateDeviceSnapshots();
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
    public event EventHandler<DeviceEventArgs>? DeviceForgotten;

    /// <inheritdoc />
    public event EventHandler<StoredDeviceEventArgs>? StoredDeviceForgotten;

    /// <inheritdoc />
    public event EventHandler? MissingStoredDevicesChanged;

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

    protected virtual void OnDeviceForgotten(DeviceEventArgs e)
    {
        DeviceForgotten?.Invoke(this, e);
    }

    protected virtual void OnStoredDeviceForgotten(StoredDeviceEventArgs e)
    {
        StoredDeviceForgotten?.Invoke(this, e);
    }

    protected virtual void OnMissingStoredDevicesChanged()
    {
        MissingStoredDevicesChanged?.Invoke(this, EventArgs.Empty);
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
