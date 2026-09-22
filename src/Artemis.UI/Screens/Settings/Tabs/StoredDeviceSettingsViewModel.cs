using System.Threading.Tasks;
using Artemis.Core.Services;
using Artemis.Storage.Entities.Surface;
using Artemis.UI.Shared.Services;

namespace Artemis.UI.Screens.Settings;

public sealed class StoredDeviceSettingsViewModel
{
    private readonly IDeviceService _deviceService;
    private readonly IWindowService _windowService;

    public StoredDeviceSettingsViewModel(DeviceEntity deviceEntity, IDeviceService deviceService, IWindowService windowService)
    {
        DeviceEntity = deviceEntity;
        _deviceService = deviceService;
        _windowService = windowService;
    }

    public DeviceEntity DeviceEntity { get; }
    public string Identifier => DeviceEntity.Id;

    public async Task ForgetDevice()
    {
        bool confirmed = await _windowService.ShowConfirmContentDialog(
            "Remove missing device",
            $"Permanently remove {Identifier}? Its saved settings and all layer bindings will be deleted. This cannot be undone.",
            "Remove",
            "Cancel");
        if (confirmed)
            _deviceService.ForgetDevice(DeviceEntity);
    }
}
