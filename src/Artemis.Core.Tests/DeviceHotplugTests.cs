using Artemis.Core.DeviceProviders;
using Artemis.Core.Providers;
using Artemis.Core.Services;
using Artemis.Storage.Entities.Surface;
using Artemis.Storage.Repositories.Interfaces;
using NSubstitute;
using RGB.NET.Core;
using Serilog;
using Xunit;

namespace Artemis.Core.Tests;

public class DeviceHotplugTests
{
    [Fact]
    public void ReplugRebindsExistingDeviceAndLedObjects()
    {
        TestRgbDevice original = new("mouse-a");
        TestRgbProvider rgbProvider = new(original);
        TestArtemisProvider provider = new(rgbProvider) {IsEnabled = true};
        DeviceService service = CreateDeviceService();

        int disconnected = 0;
        int reconnected = 0;
        service.DeviceDisconnected += (_, _) => disconnected++;
        service.DeviceReconnected += (_, _) => reconnected++;
        service.AddDeviceProvider(provider);

        ArtemisDevice logicalDevice = Assert.Single(service.Devices);
        ArtemisLed logicalLed = Assert.Single(logicalDevice.Leds);

        rgbProvider.Disconnect(original);

        Assert.False(logicalDevice.IsConnected);
        Assert.Same(logicalDevice, Assert.Single(service.Devices));
        Assert.Equal(1, disconnected);

        TestRgbDevice replacement = new("mouse-a");
        rgbProvider.Connect(replacement);

        Assert.True(logicalDevice.IsConnected);
        Assert.Same(logicalDevice, Assert.Single(service.Devices));
        Assert.Same(logicalLed, Assert.Single(logicalDevice.Leds));
        Assert.Same(replacement, logicalDevice.RgbDevice);
        Assert.Same(replacement.Single(), logicalLed.RgbLed);
        Assert.Equal(1, reconnected);
    }

    [Fact]
    public void ReplacementAddBeforeRemoveRebindsInsteadOfDuplicatingDatabaseEntity()
    {
        TestRgbDevice original = new("mouse-a");
        TestRgbProvider rgbProvider = new(original);
        TestArtemisProvider provider = new(rgbProvider) {IsEnabled = true};
        DeviceService service = CreateDeviceService();
        service.AddDeviceProvider(provider);

        ArtemisDevice logicalDevice = Assert.Single(service.Devices);
        DeviceEntity entity = logicalDevice.DeviceEntity;
        TestRgbDevice replacement = new("mouse-a");

        rgbProvider.Connect(replacement);

        Assert.Same(logicalDevice, Assert.Single(service.Devices));
        Assert.Same(entity, logicalDevice.DeviceEntity);
        Assert.Same(replacement, logicalDevice.RgbDevice);
        Assert.True(logicalDevice.IsConnected);
        Assert.Single(service.Devices.Select(device => device.DeviceEntity.Id).Distinct());
    }

    [Fact]
    public void IdenticalDevicesReturningInReverseOrderKeepTheirBindings()
    {
        TestRgbDevice first = new("bulb-a");
        TestRgbDevice second = new("bulb-b");
        TestRgbProvider rgbProvider = new(first, second);
        TestArtemisProvider provider = new(rgbProvider) {IsEnabled = true};
        DeviceService service = CreateDeviceService();
        service.AddDeviceProvider(provider);

        ArtemisDevice firstLogical = service.Devices.Single(d => d.Identifier == "test:bulb-a");
        ArtemisDevice secondLogical = service.Devices.Single(d => d.Identifier == "test:bulb-b");
        ArtemisLed firstLed = Assert.Single(firstLogical.Leds);
        ArtemisLed secondLed = Assert.Single(secondLogical.Leds);

        rgbProvider.Disconnect(first);
        rgbProvider.Disconnect(second);

        TestRgbDevice secondReplacement = new("bulb-b");
        TestRgbDevice firstReplacement = new("bulb-a");
        rgbProvider.Connect(secondReplacement);
        rgbProvider.Connect(firstReplacement);

        Assert.Same(firstLogical, service.Devices.Single(d => d.Identifier == "test:bulb-a"));
        Assert.Same(secondLogical, service.Devices.Single(d => d.Identifier == "test:bulb-b"));
        Assert.Same(firstLed, Assert.Single(firstLogical.Leds));
        Assert.Same(secondLed, Assert.Single(secondLogical.Leds));
        Assert.Same(firstReplacement, firstLogical.RgbDevice);
        Assert.Same(secondReplacement, secondLogical.RgbDevice);
    }

    [Fact]
    public void LegacyIdentifierRemainsAnAliasAfterDeviceRowWasAlreadyMigrated()
    {
        TestRgbDevice rgbDevice = new("keyboard-a");
        TestRgbProvider rgbProvider = new(rgbDevice);
        TestArtemisProvider provider = new(rgbProvider) {IsEnabled = true};
        DeviceService service = CreateDeviceService();

        service.AddDeviceProvider(provider);

        ArtemisDevice device = Assert.Single(service.Devices);
        Assert.Equal("test:keyboard-a", device.Identifier);
        Assert.True(device.MatchesIdentifier(rgbDevice.GetDeviceIdentifier()));
    }

    [Fact]
    public void ProviderReloadRebindsRetainedLogicalDeviceAndLedObjects()
    {
        TestRgbDevice original = new("mouse-a");
        TestArtemisProvider originalProvider = new(new TestRgbProvider(original)) {IsEnabled = true};
        DeviceService service = CreateDeviceService();
        service.AddDeviceProvider(originalProvider);
        ArtemisDevice logicalDevice = Assert.Single(service.Devices);
        ArtemisLed logicalLed = Assert.Single(logicalDevice.Leds);

        service.RemoveDeviceProvider(originalProvider);
        TestRgbDevice replacement = new("mouse-a");
        TestArtemisProvider replacementProvider = new(new TestRgbProvider(replacement)) {IsEnabled = true};
        service.AddDeviceProvider(replacementProvider);

        Assert.Same(logicalDevice, Assert.Single(service.Devices));
        Assert.Same(logicalLed, Assert.Single(logicalDevice.Leds));
        Assert.Same(replacementProvider, logicalDevice.DeviceProvider);
        Assert.Same(replacement, logicalDevice.RgbDevice);
        Assert.True(logicalDevice.IsConnected);
    }

    [Fact]
    public void TopologyChangeRetainsSurvivingLedObjects()
    {
        TestRgbDevice original = new("strip-a", LedId.LedStripe1, LedId.LedStripe2);
        TestRgbProvider rgbProvider = new(original);
        TestArtemisProvider provider = new(rgbProvider) {IsEnabled = true};
        DeviceService service = CreateDeviceService();
        service.AddDeviceProvider(provider);

        ArtemisDevice logicalDevice = Assert.Single(service.Devices);
        ArtemisLed retainedLed = logicalDevice.LedIds[LedId.LedStripe1];
        ArtemisLed removedLed = logicalDevice.LedIds[LedId.LedStripe2];

        rgbProvider.Disconnect(original);
        TestRgbDevice replacement = new("strip-a", LedId.LedStripe1, LedId.LedStripe3);
        rgbProvider.Connect(replacement);

        Assert.Same(logicalDevice, Assert.Single(service.Devices));
        Assert.Same(retainedLed, logicalDevice.LedIds[LedId.LedStripe1]);
        Assert.Same(replacement.Single(l => l.Id == LedId.LedStripe1), retainedLed.RgbLed);
        Assert.DoesNotContain(removedLed, logicalDevice.Leds);
        Assert.Contains(LedId.LedStripe3, logicalDevice.LedIds.Keys);
    }

    private static DeviceService CreateDeviceService()
    {
        IPluginManagementService pluginManagementService = Substitute.For<IPluginManagementService>();
        IDeviceRepository repository = Substitute.For<IDeviceRepository>();
        repository.Get(Arg.Any<string>()).Returns(call => CreateEntity(call.Arg<string>()));
        IRenderService renderService = Substitute.For<IRenderService>();
        ILogger logger = new LoggerConfiguration().CreateLogger();

        return new DeviceService(logger, pluginManagementService, repository, new Lazy<IRenderService>(() => renderService),
            () => [new NoneLayoutProvider()]);
    }

    private static DeviceEntity CreateEntity(string id)
    {
        return new DeviceEntity
        {
            Id = id,
            DeviceProvider = "test",
            IsEnabled = true,
            Scale = 1,
            RedScale = 1,
            GreenScale = 1,
            BlueScale = 1,
            LayoutType = NoneLayoutProvider.LAYOUT_TYPE
        };
    }

    private sealed class TestArtemisProvider(TestRgbProvider rgbProvider) : DeviceProvider
    {
        public override IRGBDeviceProvider RgbDeviceProvider => rgbProvider;
        public override string GetDeviceIdentifier(IRGBDevice device) => $"test:{((TestDeviceInfo) device.DeviceInfo).StableId}";
        public override void Enable() { }
        public override void Disable() { }
    }

    private sealed class TestRgbProvider(params TestRgbDevice[] devices) : AbstractRGBDeviceProvider
    {
        private readonly List<TestRgbDevice> _initialDevices = [..devices];

        protected override void InitializeSDK() { }
        protected override IEnumerable<IRGBDevice> LoadDevices() => _initialDevices;

        public void Disconnect(TestRgbDevice device) => RemoveDevice(device);
        public void Connect(TestRgbDevice device) => AddDevice(device);
    }

    private sealed class TestRgbDevice : AbstractRGBDevice<TestDeviceInfo>, IUnknownDevice
    {
        public TestRgbDevice(string stableId, params LedId[] ledIds)
            : base(new TestDeviceInfo(stableId), new TestUpdateQueue())
        {
            if (ledIds.Length == 0)
                ledIds = [LedId.LedStripe1];

            for (int index = 0; index < ledIds.Length; index++)
                AddLed(ledIds[index], new Point(index * 10, 0), new Size(10, 10));
        }
    }

    private sealed class TestDeviceInfo(string stableId) : IRGBDeviceInfo
    {
        public string StableId { get; } = stableId;
        public RGBDeviceType DeviceType => RGBDeviceType.LedStripe;
        public string DeviceName => "Identical test device";
        public string Manufacturer => "Test";
        public string Model => "Same model";
        public object? LayoutMetadata { get; set; }
    }

    private sealed class TestUpdateQueue() : UpdateQueue(new DeviceUpdateTrigger())
    {
        protected override bool Update(ReadOnlySpan<(object key, Color color)> dataSet) => true;
    }
}
