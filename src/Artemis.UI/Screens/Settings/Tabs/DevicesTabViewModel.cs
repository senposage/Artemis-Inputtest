using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Artemis.Core;
using Artemis.Core.Services;
using Artemis.UI.DryIoc.Factories;
using Artemis.UI.Screens.Device;
using Artemis.UI.Shared.Routing;
using Artemis.UI.Shared.Services;
using Avalonia.Threading;
using DynamicData;
using ReactiveUI;

namespace Artemis.UI.Screens.Settings;

public class DevicesTabViewModel : RoutableScreen
{
    private readonly IDeviceVmFactory _deviceVmFactory;
    private readonly IDeviceService _deviceService;
    private readonly IWindowService _windowService;
    private bool _confirmedDisable;

    public DevicesTabViewModel(IDeviceService deviceService, IWindowService windowService, IDeviceVmFactory deviceVmFactory)
    {
        DisplayName = "Devices";

        _deviceService = deviceService;
        _windowService = windowService;
        _deviceVmFactory = deviceVmFactory;

        Devices = [];
        MissingDevices = [];
        MissingStoredDevices = [];
        this.WhenActivated(disposables =>
        {
            GetDevices();

            Observable.FromEventPattern<DeviceEventArgs>(x => _deviceService.DeviceAdded += x, x => _deviceService.DeviceAdded -= x)
                .Subscribe(d => AddDevice(d.EventArgs.Device))
                .DisposeWith(disposables);
            Observable.FromEventPattern<DeviceEventArgs>(x => _deviceService.DeviceRemoved += x, x => _deviceService.DeviceRemoved -= x)
                .Subscribe(d => RemoveDevice(d.EventArgs.Device))
                .DisposeWith(disposables);
            Observable.FromEventPattern<DeviceEventArgs>(x => _deviceService.DeviceForgotten += x, x => _deviceService.DeviceForgotten -= x)
                .Subscribe(d => Dispatcher.UIThread.Post(() => RemoveMissingDevice(d.EventArgs.Device)))
                .DisposeWith(disposables);
            Observable.FromEventPattern(x => _deviceService.MissingStoredDevicesChanged += x, x => _deviceService.MissingStoredDevicesChanged -= x)
                .Subscribe(_ => RefreshMissingStoredDevices())
                .DisposeWith(disposables);
        });
    }

    public ObservableCollection<DeviceSettingsViewModel> Devices { get; }
    public ObservableCollection<DeviceSettingsViewModel> MissingDevices { get; }
    public ObservableCollection<StoredDeviceSettingsViewModel> MissingStoredDevices { get; }

    public async Task<bool> ShowDeviceDisableDialog()
    {
        if (_confirmedDisable)
            return true;

        bool confirmed = await _windowService.ShowConfirmContentDialog(
            "Disabling device",
            "Disabling a device will cause it to stop updating. " +
            "\r\nSome SDKs will even go back to using manufacturer lighting (Artemis restart may be required)."
        );
        if (confirmed)
            _confirmedDisable = true;

        return confirmed;
    }

    private void GetDevices()
    {
        Devices.Clear();
        MissingDevices.Clear();
        MissingStoredDevices.Clear();
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            Devices.AddRange(_deviceService.Devices.Select(d => _deviceVmFactory.DeviceSettingsViewModel(d, this)));
            MissingDevices.AddRange(_deviceService.MissingDevices.Select(d => _deviceVmFactory.DeviceSettingsViewModel(d, this)));
            MissingStoredDevices.AddRange(_deviceService.MissingStoredDevices.Select(d => new StoredDeviceSettingsViewModel(d, _deviceService, _windowService)));
        }, DispatcherPriority.Background);
    }

    private void AddDevice(ArtemisDevice device)
    {
        Dispatcher.UIThread.Post(() =>
        {
            RemoveMissingDevice(device);
            // If the device was only enabled, don't add it
            if (Devices.Any(d => d.Device == device))
                return;

            Devices.Add(_deviceVmFactory.DeviceSettingsViewModel(device, this));
        });
    }

    private void RemoveDevice(ArtemisDevice device)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // If the device was only disabled don't remove it
            if (_deviceService.Devices.Contains(device))
                return;

            DeviceSettingsViewModel? viewModel = Devices.FirstOrDefault(i => i.Device == device);
            if (viewModel != null)
                Devices.Remove(viewModel);
            if (_deviceService.MissingDevices.Contains(device) && MissingDevices.All(i => i.Device != device))
                MissingDevices.Add(_deviceVmFactory.DeviceSettingsViewModel(device, this));
        });
    }

    private void RemoveMissingDevice(ArtemisDevice device)
    {
        DeviceSettingsViewModel? viewModel = MissingDevices.FirstOrDefault(i => i.Device == device);
        if (viewModel != null)
            MissingDevices.Remove(viewModel);
    }

    private void RefreshMissingStoredDevices()
    {
        Dispatcher.UIThread.Post(() =>
        {
            MissingStoredDevices.Clear();
            MissingStoredDevices.AddRange(_deviceService.MissingStoredDevices.Select(d => new StoredDeviceSettingsViewModel(d, _deviceService, _windowService)));
        });
    }
}
