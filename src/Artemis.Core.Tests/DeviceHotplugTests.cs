using Artemis.Core.DeviceProviders;
using Artemis.Core.Providers;
using Artemis.Core.Services;
using Artemis.Storage.Entities.Surface;
using Artemis.Storage.Entities.Plugins;
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
        IDeviceRepository repository = Substitute.For<IDeviceRepository>();
        repository.Get(Arg.Any<string>()).Returns(call => CreateEntity(call.Arg<string>()));
        DeviceService service = CreateDeviceService(repository);

        int disconnected = 0;
        int reconnected = 0;
        service.DeviceDisconnected += (_, _) => disconnected++;
        service.DeviceReconnected += (_, _) => reconnected++;
        service.AddDeviceProvider(provider);

        ArtemisDevice logicalDevice = Assert.Single(service.Devices);
        ArtemisLed logicalLed = Assert.Single(logicalDevice.Leds);
        int added = 0;
        int removed = 0;
        int ledsChanged = 0;
        service.DeviceAdded += (_, _) => added++;
        service.DeviceRemoved += (_, _) => removed++;
        service.LedsChanged += (_, _) => ledsChanged++;

        rgbProvider.Disconnect(original);

        Assert.False(logicalDevice.IsConnected);
        Assert.Empty(service.Devices);
        Assert.Same(logicalDevice, Assert.Single(service.MissingDevices));
        Assert.Equal(1, disconnected);
        Assert.Equal(1, removed);
        Assert.Equal(1, ledsChanged);

        service.SaveDevices();
        repository.Received(1).SaveRange(Arg.Is<IEnumerable<DeviceEntity>>(entities =>
            entities.Count() == 1 && ReferenceEquals(entities.Single(), logicalDevice.DeviceEntity)));
        int ledsChangedBeforeReconnect = ledsChanged;

        TestRgbDevice replacement = new("mouse-a");
        rgbProvider.Connect(replacement);

        Assert.True(logicalDevice.IsConnected);
        Assert.Same(logicalDevice, Assert.Single(service.Devices));
        Assert.Empty(service.MissingDevices);
        Assert.Same(logicalLed, Assert.Single(logicalDevice.Leds));
        Assert.Same(replacement, logicalDevice.RgbDevice);
        Assert.Same(logicalDevice.DeviceEntity, service.Devices.Single().DeviceEntity);
        Assert.Same(replacement.Single(), logicalLed.RgbLed);
        Assert.Equal(1, reconnected);
        Assert.Equal(1, added);
        // Reconnecting from Missing must repopulate deferred profile LED bindings even
        // when every ArtemisLed object and LED ID was preserved.
        Assert.Equal(ledsChangedBeforeReconnect + 1, ledsChanged);
        repository.DidNotReceiveWithAnyArgs().Remove(default!);
        repository.Received(1).Get("test:mouse-a");
    }

    [Fact]
    public void ReplugWithReplacementRuntimeIdentityRebindsRetainedLogicalDevice()
    {
        TestRgbDevice original = new("runtime-path-a", "same-physical-device");
        TestRgbProvider rgbProvider = new(original);
        IDeviceRepository repository = Substitute.For<IDeviceRepository>();
        repository.Get(Arg.Any<string>()).Returns(call => CreateEntity(call.Arg<string>()));
        DeviceService service = CreateDeviceService(repository);
        service.AddDeviceProvider(new TestArtemisProvider(rgbProvider) {IsEnabled = true});

        ArtemisDevice logicalDevice = Assert.Single(service.Devices);
        ArtemisLed logicalLed = Assert.Single(logicalDevice.Leds);
        rgbProvider.Disconnect(original);

        TestRgbDevice replacement = new("runtime-path-b", "same-physical-device");
        rgbProvider.Connect(replacement);

        Assert.Same(logicalDevice, Assert.Single(service.Devices));
        Assert.Empty(service.MissingDevices);
        Assert.Equal("test:runtime-path-a", logicalDevice.Identifier);
        Assert.Same(logicalLed, Assert.Single(logicalDevice.Leds));
        Assert.Same(replacement, logicalDevice.RgbDevice);
        repository.Received(1).Get("test:runtime-path-a");
        repository.DidNotReceive().Get("test:runtime-path-b");
    }

    [Fact]
    public async Task ReplacementRuntimeIdentityDuringRemovalGraceRebindsDisconnectedLogicalDevice()
    {
        TestRgbDevice original = new("runtime-path-a", "same-physical-device");
        TestRgbProvider rgbProvider = new(original);
        IDeviceRepository repository = Substitute.For<IDeviceRepository>();
        repository.Get(Arg.Any<string>()).Returns(call => CreateEntity(call.Arg<string>()));
        DeviceService service = CreateDeviceService(repository);
        service.DeviceRemovalGracePeriod = TimeSpan.FromMilliseconds(150);
        service.AddDeviceProvider(new TestArtemisProvider(rgbProvider) {IsEnabled = true});

        ArtemisDevice logicalDevice = Assert.Single(service.Devices);
        ArtemisLed logicalLed = Assert.Single(logicalDevice.Leds);
        int removed = 0;
        service.DeviceRemoved += (_, _) => removed++;

        rgbProvider.Disconnect(original);
        Assert.False(logicalDevice.IsConnected);
        Assert.Empty(service.MissingDevices);

        TestRgbDevice replacement = new("runtime-path-b", "same-physical-device");
        rgbProvider.Connect(replacement);
        await Task.Delay(250);

        Assert.Same(logicalDevice, Assert.Single(service.Devices));
        Assert.Empty(service.MissingDevices);
        Assert.Same(logicalLed, Assert.Single(logicalDevice.Leds));
        Assert.Same(replacement, logicalDevice.RgbDevice);
        Assert.True(logicalDevice.IsConnected);
        Assert.Equal(0, removed);
        Assert.Contains("test:runtime-path-b", logicalDevice.DeviceEntity.IdentifierAliases);
        repository.DidNotReceive().Get("test:runtime-path-b");
    }

    [Fact]
    public async Task TransientRemovalDoesNotEnterMissingOrRaiseRemoved()
    {
        TestRgbDevice original = new("mouse-a");
        TestRgbProvider rgbProvider = new(original);
        DeviceService service = CreateDeviceService();
        service.DeviceRemovalGracePeriod = TimeSpan.FromMilliseconds(150);
        service.AddDeviceProvider(new TestArtemisProvider(rgbProvider) {IsEnabled = true});
        ArtemisDevice logicalDevice = Assert.Single(service.Devices);
        int removed = 0;
        service.DeviceRemoved += (_, _) => removed++;

        rgbProvider.Disconnect(original);
        Assert.Empty(service.MissingDevices);

        TestRgbDevice replacement = new("mouse-a");
        rgbProvider.Connect(replacement);
        await Task.Delay(250);

        Assert.Same(logicalDevice, Assert.Single(service.Devices));
        Assert.Empty(service.MissingDevices);
        Assert.True(logicalDevice.IsConnected);
        Assert.Same(replacement, logicalDevice.RgbDevice);
        Assert.Equal(0, removed);
    }

    [Fact]
    public void SavedMissingDeviceIsClaimedImmediatelyWhenItReconnects()
    {
        DeviceEntity stored = CreateEntity("test:mouse-a");
        IDeviceRepository repository = Substitute.For<IDeviceRepository>();
        repository.GetAll().Returns([stored]);
        repository.Get("test:mouse-a").Returns(stored);
        DeviceService service = CreateDeviceService(repository);

        Assert.Same(stored, Assert.Single(service.MissingStoredDevices));

        TestRgbDevice rgbDevice = new("mouse-a");
        service.AddDeviceProvider(new TestArtemisProvider(new TestRgbProvider(rgbDevice)) {IsEnabled = true});

        Assert.Empty(service.MissingStoredDevices);
        Assert.Same(rgbDevice, Assert.Single(service.Devices).RgbDevice);
    }

    [Fact]
    public void StoredMissingDeviceWithReplacementRuntimeIdentityIsClaimedBySignature()
    {
        DeviceEntity stored = CreateEntity("test:runtime-path-a");
        stored.DeviceProvider = TestArtemisProvider.PluginId;
        stored.ReconnectionSignature = "same-physical-device";
        IDeviceRepository repository = Substitute.For<IDeviceRepository>();
        repository.GetAll().Returns([stored]);
        DeviceService service = CreateDeviceService(repository);
        repository.Get(Arg.Any<string>()).Returns((DeviceEntity?) null);

        TestRgbDevice replacement = new("runtime-path-b", "same-physical-device");
        service.AddDeviceProvider(new TestArtemisProvider(new TestRgbProvider(replacement)) {IsEnabled = true});

        ArtemisDevice device = Assert.Single(service.Devices);
        Assert.Same(stored, device.DeviceEntity);
        Assert.Equal("test:runtime-path-b", device.Identifier);
        Assert.True(device.MatchesIdentifier("test:runtime-path-a"));
        Assert.Contains("test:runtime-path-b", stored.IdentifierAliases);
        repository.DidNotReceive().Add(Arg.Any<DeviceEntity>());
    }

    [Fact]
    public void ObsoleteSplitChildIsNotLabeledMissingWhileParentIsPresent()
    {
        DeviceEntity obsoleteZone = CreateEntity("test:mainboard|zone:7");
        obsoleteZone.DeviceProvider = TestArtemisProvider.PluginId;
        IDeviceRepository repository = Substitute.For<IDeviceRepository>();
        repository.GetAll().Returns([obsoleteZone]);
        DeviceService service = CreateDeviceService(repository);
        repository.Get("test:mainboard|zone:7").Returns(obsoleteZone);

        Assert.Same(obsoleteZone, Assert.Single(service.MissingStoredDevices));

        TestRgbProvider rgbProvider = new(new TestRgbDevice("mainboard|zone:2"));
        service.AddDeviceProvider(new TestArtemisProvider(rgbProvider) {IsEnabled = true});

        Assert.Empty(service.MissingStoredDevices);
        repository.DidNotReceive().Remove(obsoleteZone);

        TestRgbDevice returningZone = new("mainboard|zone:7");
        rgbProvider.Connect(returningZone);

        ArtemisDevice restored = service.Devices.Single(device => device.Identifier == "test:mainboard|zone:7");
        Assert.Same(obsoleteZone, restored.DeviceEntity);
        repository.DidNotReceive().Remove(obsoleteZone);
    }

    [Fact]
    public void FirstRuntimeSplitChildRefreshesMissingStoredClassification()
    {
        DeviceEntity obsoleteZone = CreateEntity("test:mainboard|zone:7");
        obsoleteZone.DeviceProvider = TestArtemisProvider.PluginId;
        IDeviceRepository repository = Substitute.For<IDeviceRepository>();
        repository.GetAll().Returns([obsoleteZone]);
        DeviceService service = CreateDeviceService(repository);
        TestRgbProvider rgbProvider = new();

        service.AddDeviceProvider(new TestArtemisProvider(rgbProvider) {IsEnabled = true});
        Assert.Same(obsoleteZone, Assert.Single(service.MissingStoredDevices));

        rgbProvider.Connect(new TestRgbDevice("mainboard|zone:2"));

        Assert.Empty(service.MissingStoredDevices);
        repository.DidNotReceive().Remove(obsoleteZone);
    }

    [Fact]
    public void MissingDevicesCanBePermanentlyForgotten()
    {
        DeviceEntity stored = CreateEntity("test:lost-device");
        IDeviceRepository repository = Substitute.For<IDeviceRepository>();
        repository.GetAll().Returns([stored]);
        DeviceService service = CreateDeviceService(repository);
        int forgotten = 0;
        service.StoredDeviceForgotten += (_, args) =>
        {
            Assert.Same(stored, args.DeviceEntity);
            forgotten++;
        };

        service.ForgetDevice(stored);

        Assert.Empty(service.MissingStoredDevices);
        repository.Received(1).Remove(stored);
        Assert.Equal(1, forgotten);
    }

    [Fact]
    public void DeviceMissingAfterHotplugCanBePermanentlyForgotten()
    {
        TestRgbDevice rgbDevice = new("lost-mouse");
        TestRgbProvider rgbProvider = new(rgbDevice);
        IDeviceRepository repository = Substitute.For<IDeviceRepository>();
        repository.Get(Arg.Any<string>()).Returns(call => CreateEntity(call.Arg<string>()));
        DeviceService service = CreateDeviceService(repository);
        service.AddDeviceProvider(new TestArtemisProvider(rgbProvider) {IsEnabled = true});
        ArtemisDevice device = Assert.Single(service.Devices);

        rgbProvider.Disconnect(rgbDevice);
        service.ForgetDevice(device);

        Assert.Empty(service.MissingDevices);
        repository.Received(1).Remove(device.DeviceEntity);
    }

    [Fact]
    public void RendererAttachesGenuinelyNewRuntimeDevice()
    {
        var previousStartupArguments = Constants.StartupArguments;
        try
        {
            Constants.StartupArguments = new List<string>().AsReadOnly();
            IDeviceService deviceService = Substitute.For<IDeviceService>();
            deviceService.EnabledDevices.Returns([]);
            ISettingsService settingsService = Substitute.For<ISettingsService>();
            settingsService.GetSetting("Core.TargetFrameRate", 30).Returns(CreateSetting("Core.TargetFrameRate", 30));
            settingsService.GetSetting("Core.RenderScale", 0.5).Returns(CreateSetting("Core.RenderScale", 0.5));
            settingsService.GetSetting("Core.PreferredGraphicsContext", "Software").Returns(CreateSetting("Core.PreferredGraphicsContext", "Software"));
            CoreRenderer coreRenderer = new(Substitute.For<IModuleService>(), Substitute.For<IProfileService>());
            using RenderService renderService = new(new LoggerConfiguration().CreateLogger(), settingsService, deviceService, coreRenderer,
                new global::DryIoc.LazyEnumerable<IGraphicsContextProvider>([]));
            renderService.Initialize();

            TestRgbDevice rgbDevice = new("new-mouse");
            ArtemisDevice device = new(rgbDevice, new TestArtemisProvider(new TestRgbProvider(rgbDevice)) {IsEnabled = true});
            deviceService.DeviceAdded += Raise.Event<EventHandler<DeviceEventArgs>>(deviceService, new DeviceEventArgs(device));

            Assert.Contains(rgbDevice, renderService.Surface.Devices);
        }
        finally
        {
            Constants.StartupArguments = previousStartupArguments;
        }
    }

    [Fact]
    public void GenuinelyNewRuntimeDeviceMovesToMissingImmediatelyWhenUnplugged()
    {
        TestRgbDevice keyboard = new("keyboard-a");
        TestRgbProvider rgbProvider = new(keyboard);
        DeviceService service = CreateDeviceService();
        service.AddDeviceProvider(new TestArtemisProvider(rgbProvider) {IsEnabled = true});

        TestRgbDevice mouse = new("g502-hero");
        rgbProvider.Connect(mouse);
        ArtemisDevice mouseDevice = service.Devices.Single(device => device.Identifier == "test:g502-hero");

        rgbProvider.Disconnect(mouse);

        Assert.Equal("test:keyboard-a", Assert.Single(service.Devices).Identifier);
        Assert.Same(mouseDevice, Assert.Single(service.MissingDevices));
        Assert.False(mouseDevice.IsConnected);
    }

    [Fact]
    public void DuplicateProviderSnapshotCreatesOneLogicalDeviceAndOneDatabaseIdentity()
    {
        TestRgbDevice stale = new("mainboard-a");
        TestRgbDevice replacement = new("mainboard-a");
        TestRgbProvider rgbProvider = new(stale, replacement);
        TestArtemisProvider provider = new(rgbProvider) {IsEnabled = true};
        IDeviceRepository repository = Substitute.For<IDeviceRepository>();
        repository.Get(Arg.Any<string>()).Returns(call => CreateEntity(call.Arg<string>()));
        DeviceService service = CreateDeviceService(repository);

        service.AddDeviceProvider(provider);

        ArtemisDevice device = Assert.Single(service.Devices);
        Assert.Same(replacement, device.RgbDevice);
        Assert.Single(service.Devices.Select(d => d.DeviceEntity.Id).Distinct());
        repository.Received(1).Get("test:mainboard-a");
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
        int added = 0;
        service.DeviceAdded += (_, _) => added++;

        rgbProvider.Connect(replacement);

        Assert.Same(logicalDevice, Assert.Single(service.Devices));
        Assert.Same(entity, logicalDevice.DeviceEntity);
        Assert.Same(replacement, logicalDevice.RgbDevice);
        Assert.True(logicalDevice.IsConnected);
        Assert.Single(service.Devices.Select(device => device.DeviceEntity.Id).Distinct());
        Assert.Equal(0, added);
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

        Assert.Empty(service.Devices);

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
        Assert.Empty(service.Devices);
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

    private static DeviceService CreateDeviceService(IDeviceRepository? repository = null)
    {
        IPluginManagementService pluginManagementService = Substitute.For<IPluginManagementService>();
        repository ??= Substitute.For<IDeviceRepository>();
        repository.Get(Arg.Any<string>()).Returns(call => CreateEntity(call.Arg<string>()));
        IRenderService renderService = Substitute.For<IRenderService>();
        ILogger logger = new LoggerConfiguration().CreateLogger();

        return new DeviceService(logger, pluginManagementService, repository, new Lazy<IRenderService>(() => renderService),
            () => [new NoneLayoutProvider()])
        {
            DeviceRemovalGracePeriod = TimeSpan.Zero
        };
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

    private static PluginSetting<T> CreateSetting<T>(string name, T value)
    {
        IPluginRepository repository = Substitute.For<IPluginRepository>();
        return new PluginSetting<T>(repository, new PluginSettingEntity {Name = name, Value = CoreJson.Serialize(value)});
    }

    private sealed class TestArtemisProvider : DeviceProvider
    {
        public const string PluginId = "5d56f8da-ffb2-4d2d-bb5f-a175ab53a260";
        private static readonly Plugin TestPlugin = new(
            new PluginInfo {Guid = Guid.Parse(PluginId), Name = "Test", Version = "1.0.0", Main = "Test.dll"},
            new DirectoryInfo(AppContext.BaseDirectory), new PluginEntity(), false);
        private readonly TestRgbProvider _rgbProvider;

        public TestArtemisProvider(TestRgbProvider rgbProvider)
        {
            _rgbProvider = rgbProvider;
            Plugin = TestPlugin;
        }

        public override IRGBDeviceProvider RgbDeviceProvider => _rgbProvider;
        public override string GetDeviceIdentifier(IRGBDevice device) => $"test:{((TestDeviceInfo) device.DeviceInfo).StableId}";
        public override string? GetReconnectionSignature(IRGBDevice device) => ((TestDeviceInfo) device.DeviceInfo).ReconnectionSignature;
        public override string? GetParentDeviceIdentifier(string deviceIdentifier)
        {
            int separatorIndex = deviceIdentifier.LastIndexOf('|');
            return separatorIndex >= 0 && deviceIdentifier[(separatorIndex + 1)..].StartsWith("zone:", StringComparison.Ordinal)
                ? deviceIdentifier[..separatorIndex]
                : null;
        }
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

        public TestRgbDevice(string stableId, string reconnectionSignature)
            : base(new TestDeviceInfo(stableId, reconnectionSignature), new TestUpdateQueue())
        {
            AddLed(LedId.LedStripe1, new Point(0, 0), new Size(10, 10));
        }
    }

    private sealed class TestDeviceInfo(string stableId, string? reconnectionSignature = null) : IRGBDeviceInfo
    {
        public string StableId { get; } = stableId;
        public string ReconnectionSignature { get; } = reconnectionSignature ?? stableId;
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
