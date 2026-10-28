using Altinn.Authorization.TestUtils;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry.Trace;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;

namespace Altinn.Authorization.ServiceDefaults.Tests;

public class ServiceDefaultsTests
{
#if NET10_0_OR_GREATER
    private static readonly IPNetwork TestNetwork
        = new IPNetwork(new IPAddress([10, 50, 0, 15]), 16);
#else
    private static readonly Microsoft.AspNetCore.HttpOverrides.IPNetwork TestNetwork
        = new Microsoft.AspNetCore.HttpOverrides.IPNetwork(new IPAddress([10, 50, 0, 15]), 16);

    private static readonly IPNetwork TestNetworkConverted
        = Altinn.Authorization.ServiceDefaults.Utils.IPNetworkUtils.From(TestNetwork);
#endif



    [Fact]
    public async Task NoClusterInfo()
    {
        await using var app = await CreateApp([]);

        var altinnClusterInfo = app.GetRequiredService<IOptionsMonitor<AltinnClusterInfo>>().CurrentValue;
        var forwardedHeadersOptions = app.GetRequiredService<IOptionsMonitor<ForwardedHeadersOptions>>().CurrentValue;
        Assert.NotNull(altinnClusterInfo);

#pragma warning disable CS0618 // Type or member is obsolete
        altinnClusterInfo.ClusterNetwork.ShouldBeNull();
#pragma warning restore CS0618 // Type or member is obsolete
        altinnClusterInfo.TrustedProxies.ShouldBeEmpty();

#if NET10_0_OR_GREATER
        forwardedHeadersOptions.KnownIPNetworks.ShouldNotContain(TestNetwork);
#else
        forwardedHeadersOptions.KnownNetworks.ShouldNotContain(TestNetwork);
#endif
    }

    [Fact]
    public async Task ValidCidrInClusterInfo()
    {
        await using var app = await CreateApp([
#if NET10_0_OR_GREATER
            KeyValuePair.Create("Altinn:ClusterInfo:ClusterNetwork", $"{TestNetwork.BaseAddress}/{TestNetwork.PrefixLength}"),
#else
            KeyValuePair.Create("Altinn:ClusterInfo:ClusterNetwork", $"{TestNetwork.Prefix}/{TestNetwork.PrefixLength}"),
#endif
        ]);

        var altinnClusterInfo = app.GetRequiredService<IOptionsMonitor<AltinnClusterInfo>>().CurrentValue;
        var forwardedHeadersOptions = app.GetRequiredService<IOptionsMonitor<ForwardedHeadersOptions>>().CurrentValue;
        Assert.NotNull(altinnClusterInfo);

        altinnClusterInfo.TrustedProxies.ShouldNotBeEmpty();

#if NET10_0_OR_GREATER
#pragma warning disable CS0618 // Type or member is obsolete
        altinnClusterInfo.ClusterNetwork.ShouldBe(TestNetwork);
        altinnClusterInfo.TrustedProxies.ShouldContain(TestNetwork);
#pragma warning restore CS0618 // Type or member is obsolete
        forwardedHeadersOptions.KnownIPNetworks.ShouldContain(TestNetwork);
#else
#pragma warning disable CS0618 // Type or member is obsolete
        altinnClusterInfo.ClusterNetwork.ShouldBe(TestNetworkConverted);
        altinnClusterInfo.TrustedProxies.ShouldContain(TestNetworkConverted);
#pragma warning restore CS0618 // Type or member is obsolete
        forwardedHeadersOptions.KnownNetworks.ShouldContain(static x => x.Prefix.Equals(TestNetworkConverted.BaseAddress) && x.PrefixLength == TestNetworkConverted.PrefixLength);
#endif
    }

    [Fact]
    public async Task ValidCidrListTrustedProxies()
    {
        await using var app = await CreateApp([
            KeyValuePair.Create("Altinn:ClusterInfo:TrustedProxies", "10.11.12.13/24,, 10.100.0.0/16 ,"),
        ]);

        var altinnClusterInfo = app.GetRequiredService<IOptionsMonitor<AltinnClusterInfo>>().CurrentValue;
        var forwardedHeadersOptions = app.GetRequiredService<IOptionsMonitor<ForwardedHeadersOptions>>().CurrentValue;
        Assert.NotNull(altinnClusterInfo);

        altinnClusterInfo.TrustedProxies.Count.ShouldBe(2);
        altinnClusterInfo.TrustedProxies.ShouldContain(new IPNetwork(new IPAddress([10, 11, 12, 0]), 24));
        altinnClusterInfo.TrustedProxies.ShouldContain(new IPNetwork(new IPAddress([10, 100, 0, 0]), 16));

#if NET10_0_OR_GREATER
        forwardedHeadersOptions.KnownIPNetworks.ShouldContain(new IPNetwork(new IPAddress([10, 11, 12, 0]), 24));
        forwardedHeadersOptions.KnownIPNetworks.ShouldContain(new IPNetwork(new IPAddress([10, 100, 0, 0]), 16));
#else
        var ip1 = new IPAddress([10, 11, 12, 0]);
        var ip2 = new IPAddress([10, 100, 0, 0]);
        forwardedHeadersOptions.KnownNetworks.ShouldContain(x => x.Prefix.Equals(ip1) && x.PrefixLength == 24);
        forwardedHeadersOptions.KnownNetworks.ShouldContain(x => x.Prefix.Equals(ip2) && x.PrefixLength == 16);
#endif
    }

    [Fact]
    public async Task InvalidCidrInClusterInfo()
    {
        await Should.ThrowAsync<FormatException>(async () =>
        {
            await using var app = await CreateApp([
                KeyValuePair.Create("Altinn:ClusterInfo:ClusterNetwork", "text"),
            ]);

            var _altinnClusterInfo = app.GetRequiredService<IOptionsMonitor<AltinnClusterInfo>>().CurrentValue;
        });
    }

    [Fact]
    public async Task OpenTelemetry_Sampling_RootSampler_IsConfigurable_Enabled()
    {
        await using var app = await CreateApp([
            new("Altinn:Telemetry:Sampling:Root:SamplingRatio", "1.0"),
        ]);

        await app.Poke(parent: default /* root activity */);

        app.Activities.Select(static a => a.DisplayName).ShouldBe(["outer poke", "inner poke"], ignoreOrder: true);
    }

    [Fact]
    public async Task OpenTelemetry_Sampling_RootSampler_IsConfigurable_Disabled()
    {
        await using var app = await CreateApp([
            new("Altinn:Telemetry:Sampling:Root:SamplingRatio", "0.0"),
        ]);

        await app.Poke(parent: default /* root activity */);

        //if (samplingRate == 0)
        app.Activities.ShouldBeEmpty();
    }

    [Fact]
    public async Task OpenTelemetry_Sampling_RemoteParentSampledSampler_IsConfigurable_Enabled()
    {
        await using var app = await CreateApp([
            new("Altinn:Telemetry:Sampling:Root:SamplingRatio", "0.0"), // disable root sampler, so we're sure that the remote parent sampled sampler is doing the work
            new("Altinn:Telemetry:Sampling:RemoteParentSampled:SamplingRatio", "1.0"),
        ]);

        await app.Poke(parent: CreateActivityContext(remote: true, sampled: true));

        app.Activities.Select(static a => a.DisplayName).ShouldBe(["outer poke", "inner poke"], ignoreOrder: true);
    }

    [Fact]
    public async Task OpenTelemetry_Sampling_RemoteParentSampledSampler_IsConfigurable_Disabled()
    {
        await using var app = await CreateApp([
            new("Altinn:Telemetry:Sampling:Root:SamplingRatio", "1.0"), // enable root sampler, so we're sure that the remote parent sampled sampler is doing the work
            new("Altinn:Telemetry:Sampling:RemoteParentSampled:SamplingRatio", "0.0"),
        ]);

        await app.Poke(parent: CreateActivityContext(remote: true, sampled: true));

        app.Activities.ShouldBeEmpty();
    }

    [Fact]
    public async Task OpenTelemetry_Sampling_RemoteParentNotSampledSampler_IsConfigurable_Enabled()
    {
        await using var app = await CreateApp([
            new("Altinn:Telemetry:Sampling:Root:SamplingRatio", "0.0"), // disable root sampler, so we're sure that the remote parent sampled sampler is doing the work
            new("Altinn:Telemetry:Sampling:RemoteParentNotSampled:SamplingRatio", "1.0"),
        ]);

        await app.Poke(parent: CreateActivityContext(remote: true, sampled: false));

        app.Activities.Select(static a => a.DisplayName).ShouldBe(["outer poke", "inner poke"], ignoreOrder: true);
    }

    [Fact]
    public async Task OpenTelemetry_Sampling_RemoteParentNotSampledSampler_IsConfigurable_Disabled()
    {
        await using var app = await CreateApp([
            new("Altinn:Telemetry:Sampling:Root:SamplingRatio", "1.0"), // enable root sampler, so we're sure that the remote parent sampled sampler is doing the work
            new("Altinn:Telemetry:Sampling:RemoteParentNotSampled:SamplingRatio", "0.0"),
        ]);

        await app.Poke(parent: CreateActivityContext(remote: true, sampled: false));

        app.Activities.ShouldBeEmpty();
    }

    [Fact]
    public async Task OpenTelemetry_Sampling_LocalParentSampledSampler_IsConfigurable_Enabled()
    {
        await using var app = await CreateApp([
            new("Altinn:Telemetry:Sampling:Root:SamplingRatio", "0.0"), // disable root sampler, so we're sure that the remote parent sampled sampler is doing the work
            new("Altinn:Telemetry:Sampling:LocalParentSampled:SamplingRatio", "1.0"),
        ]);

        await app.Poke(parent: CreateActivityContext(remote: false, sampled: true));

        app.Activities.Select(static a => a.DisplayName).ShouldBe(["outer poke", "inner poke"], ignoreOrder: true);
    }

    [Fact]
    public async Task OpenTelemetry_Sampling_LocalParentSampledSampler_IsConfigurable_Disabled()
    {
        await using var app = await CreateApp([
            new("Altinn:Telemetry:Sampling:Root:SamplingRatio", "1.0"), // enable root sampler, so we're sure that the remote parent sampled sampler is doing the work
            new("Altinn:Telemetry:Sampling:LocalParentSampled:SamplingRatio", "0.0"),
        ]);

        await app.Poke(parent: CreateActivityContext(remote: false, sampled: true));

        app.Activities.ShouldBeEmpty();
    }

    [Fact]
    public async Task OpenTelemetry_Sampling_LocalParentNotSampledSampler_IsConfigurable_Enabled()
    {
        await using var app = await CreateApp([
            new("Altinn:Telemetry:Sampling:Root:SamplingRatio", "0.0"), // disable root sampler, so we're sure that the remote parent sampled sampler is doing the work
            new("Altinn:Telemetry:Sampling:LocalParentNotSampled:SamplingRatio", "1.0"),
        ]);

        await app.Poke(parent: CreateActivityContext(remote: false, sampled: false));

        app.Activities.Select(static a => a.DisplayName).ShouldBe(["outer poke", "inner poke"], ignoreOrder: true);
    }

    [Fact]
    public async Task OpenTelemetry_Sampling_LocalParentNotSampledSampler_IsConfigurable_Disabled()
    {
        await using var app = await CreateApp([
            new("Altinn:Telemetry:Sampling:Root:SamplingRatio", "1.0"), // enable root sampler, so we're sure that the remote parent sampled sampler is doing the work
            new("Altinn:Telemetry:Sampling:LocalParentNotSampled:SamplingRatio", "0.0"),
        ]);

        await app.Poke(parent: CreateActivityContext(remote: false, sampled: false));

        app.Activities.ShouldBeEmpty();
    }

    [Fact]
    public async Task OpenTelemetry_TailSampling_KeepsErrorSpans()
    {
        await using var app = await CreateApp([
            new("Altinn:Telemetry:Sampling:Root:SamplingRatio", "0.0"), // disable root sampler, so spans would normally be dropped
        ]);

        await app.Poke(parent: default /* root activity */, error: true);

        app.Activities.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task OpenTelemetry_Exporters_OtlpOnly_ExportsOverOtlp()
    {
        using var collector = FakeTelemetryCollector.Start();
        await using var app = await CreateApp([
            .. OtlpConfig(collector),
        ]);

        await app.Poke(parent: default /* root activity */);

        await WaitForExports(app, collector, [OtlpTracesPath]);
        collector.Paths.ShouldNotContain(AzureMonitorTrackPath);
    }

    [Fact]
    public async Task OpenTelemetry_Exporters_AzureMonitorOnly_ExportsToAzureMonitor()
    {
        using var collector = FakeTelemetryCollector.Start();
        await using var app = await CreateApp([
            .. AzureMonitorConfig(collector),
        ]);

        await app.Poke(parent: default /* root activity */);

        await WaitForExports(app, collector, [AzureMonitorTrackPath]);
        collector.Paths.ShouldNotContain(OtlpTracesPath);
    }

    [Fact]
    public async Task OpenTelemetry_Exporters_OtlpAndAzureMonitor_ExportToBoth()
    {
        // Also fails if Azure Monitor's own sampler (rate limited by default) replaces the one from
        // Altinn:Telemetry:Sampling, as the rate limiter drops the first trace.
        using var collector = FakeTelemetryCollector.Start();
        await using var app = await CreateApp([
            .. OtlpConfig(collector),
            .. AzureMonitorConfig(collector),
        ]);

        await app.Poke(parent: default /* root activity */);

        await WaitForExports(app, collector, [OtlpTracesPath, AzureMonitorTrackPath]);
    }

    private const string OtlpTracesPath = "/v1/traces";
    private const string AzureMonitorTrackPath = "/v2.1/track";

    private static IEnumerable<KeyValuePair<string, string>> OtlpConfig(FakeTelemetryCollector collector)
    {
        yield return new("OTEL_EXPORTER_OTLP_ENDPOINT", collector.Endpoint);
        yield return new("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf");
    }

    private static IEnumerable<KeyValuePair<string, string>> AzureMonitorConfig(FakeTelemetryCollector collector)
    {
        yield return new(
            "ConnectionStrings:ApplicationInsights",
            $"InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint={collector.Endpoint}/;LiveEndpoint={collector.Endpoint}/");
    }

    private static async Task WaitForExports(IServiceProvider services, FakeTelemetryCollector collector, IReadOnlyList<string> paths)
    {
        var tracerProvider = services.GetRequiredService<TracerProvider>();
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(10))
        {
            tracerProvider.ForceFlush();
            if (paths.All(collector.Paths.Contains))
            {
                return;
            }

            await Task.Delay(100);
        }

        paths.ShouldBeSubsetOf(collector.Paths);
    }

    private static ActivityContext CreateActivityContext(bool remote, bool sampled)
        => new(
            traceId: ActivityTraceId.CreateRandom(),
            spanId: ActivitySpanId.CreateRandom(),
            traceFlags: sampled ? ActivityTraceFlags.Recorded : ActivityTraceFlags.None,
            traceState: null,
            isRemote: remote);


    private static async Task<AppContext> CreateApp(
        ImmutableArray<KeyValuePair<string, string>> config,
        Action<AltinnServiceDefaultOptions>? configureOptions = null)
    {
        var id = Guid.NewGuid(); // Do not use v7, v4s are easier to distinguish when debugging.
        var activitySourceName = $"test-{id}";

        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(config);

        var hostAppBuilder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "test",
            EnvironmentName = "Development",
            DisableDefaults = true,
            //ContentRootPath = wwwDir.FullName,
            Configuration = configuration,
        });

        hostAppBuilder.AddAltinnServiceDefaults("test", configureOptions);
        hostAppBuilder.Services.AddKeyedSingleton(serviceKey: activitySourceName, (_, _) => new ActivitySource(activitySourceName));
        hostAppBuilder.Services.AddScoped(s => new PokeService(s.GetRequiredKeyedService<ActivitySource>(serviceKey: activitySourceName)));

        var activities = new ActivityCollector();
        hostAppBuilder.Services.ConfigureOpenTelemetryTracerProvider(opts => opts.AddSource(activitySourceName).AddInMemoryExporter(activities));

        var app = hostAppBuilder.Build();
        try
        {
            var waitTime = TimeSpan.FromSeconds(10);
            if (Debugger.IsAttached)
            {
                waitTime = TimeSpan.FromDays(1);
            }

            await app.StartAsync().WaitAsync(waitTime);
            var ctx = new AppContext(app, activities);
            app = null;
            return ctx;
        }
        finally
        {
            if (app is not null)
            {
                await app.StopAsync();
                app.Dispose();
            }
        }
    }

    private class AppContext
        : IAsyncDisposable
        , IServiceProvider
    {
        private readonly IHost _host;
        private readonly ActivityCollector _activities;

        public AppContext(
            IHost host,
            ActivityCollector activities)
        {
            _host = host;
            _activities = activities;
        }

        public async Task Poke(ActivityContext parent, bool error = false)
        {
            await using var scope = _host.Services.CreateAsyncScope();
            var pokeService = scope.ServiceProvider.GetRequiredService<PokeService>();
            await pokeService.Poke(parent, error);
        }

        public IReadOnlyList<Activity> Activities
            => _activities.Snapshot();

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync();

            if (_host is IAsyncDisposable h)
            {
                await h.DisposeAsync();
            }

            _host.Dispose();
        }

        object? IServiceProvider.GetService(Type serviceType)
            => _host.Services.GetService(serviceType);
    }

    private class PokeService(ActivitySource source)
    {
        public async Task Poke(ActivityContext parent, bool error)
        {
            await Task.Yield();
            Activity.Current = null; // There are some wonkies with parent = default, so clearing away the current activity guarantees that we are in a root activity if parent is default.

            using (var outer = source.StartActivity("outer poke", kind: ActivityKind.Internal, parentContext: parent))
            {
                await Task.Yield();
                using (var inner = source.StartActivity("inner poke", kind: ActivityKind.Internal, parentContext: parent))
                {
                    await Task.Yield();
                    if (error)
                    {
                        inner?.SetStatus(ActivityStatusCode.Error, "I failed");
                    }
                }

                await Task.Yield();
            }

            await Task.Yield();
        }
    }
}
