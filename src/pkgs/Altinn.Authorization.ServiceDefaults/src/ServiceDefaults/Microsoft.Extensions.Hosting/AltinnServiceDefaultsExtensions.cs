using System.Diagnostics.CodeAnalysis;
using Altinn.Authorization.ServiceDefaults;
using Altinn.Authorization.ServiceDefaults.AppConfiguration;
using Altinn.Authorization.ServiceDefaults.Authorization.Scopes.PlatformAccessToken;
using Altinn.Authorization.ServiceDefaults.HealthChecks;
using Altinn.Authorization.ServiceDefaults.OpenTelemetry;
using Altinn.Authorization.ServiceDefaults.Options;
using Altinn.Authorization.ServiceDefaults.Telemetry;
using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using CommunityToolkit.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.Metrics;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Extension methods for adding default services to an Altinn service.
/// </summary>
[ExcludeFromCodeCoverage]
public static class AltinnServiceDefaultsExtensions
{
    internal readonly static string HealthEndpoint = "/health";
    internal readonly static string AliveEndpoint = "/alive";

    /// <summary>
    /// Adds default services for an Altinn service.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder"/>.</param>
    /// <param name="name">The service name.</param>
    /// <param name="configureOptions">
    /// An optional action to configure the default service options for the Altinn service.
    /// Use this to customize which services (e.g., telemetry, health checks) are added or ignored.
    /// </param>
    /// <returns><paramref name="builder"/>.</returns>
    public static IHostApplicationBuilder AddAltinnServiceDefaults(this IHostApplicationBuilder builder, string name, Action<AltinnServiceDefaultOptions>? configureOptions = null)
    {
        Guard.IsNotNull(builder);
        Guard.IsNotNullOrEmpty(name);

        var options = new AltinnServiceDefaultOptions();
        configureOptions?.Invoke(options);

        if (builder.Services.TryFindAltinnServiceDescription(out var serviceDescription))
        {
            Guard.IsEqualTo(name, serviceDescription.Name);

            // The service description has already been added - meaning that the defaults have already been added
            // Some of the defaults are not idempotent, so we can't add them again
            return builder;
        }

        var logger = AltinnPreStartLogger.Create(builder.Configuration, nameof(AltinnServiceDefaultsExtensions));
        var envName = builder.Configuration.GetValue<string?>("ALTINN_ENVIRONMENT", defaultValue: null);
        if (string.IsNullOrEmpty(envName))
        {
            if (builder.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("Altinn:LocalDev"))
            {
                logger.Log("LocalDev is set to true in configuration - using local environment");
                envName = "LOCAL";
            }
            else
            {
                logger.Log("No ALTINN_ENVIRONMENT found in configuration - using UNKNOWN environment");
                envName = "UNKNOWN";
            }
        }

        var flags = AltinnServiceFlags.None;
        if (builder.Configuration.GetValue<bool>("Altinn:RunInitOnly"))
        {
            flags |= AltinnServiceFlags.RunInitOnly;
        }

        if (builder.Configuration.GetValue<bool>("Altinn:IsTest"))
        {
            flags |= AltinnServiceFlags.IsTest;
        }

        var env = AltinnEnvironment.Create(envName);
        serviceDescription = new AltinnServiceDescriptor(name, env, flags);
        logger.Log($"Service: {serviceDescription.Name}, Environment: {serviceDescription.Environment}");
        builder.Services.AddSingleton(serviceDescription);
        builder.Services.AddSingleton(env);

        if (options.EnabledServices.AppConfiguration)
        {
            builder.AddAltinnConfiguration(serviceDescription, logger);
        }

        builder.Services.AddMetricsProvider();
        builder.Services.AddAltinnProblemDetails();
        builder.Services.AddExceptionHandler<AltinnExceptionHandler>();
        builder.Services.AddSingleton<AltinnServiceResourceDetector>();
        builder.Services.AddSingleton<HttpStandardResilienceTelemetry>();
        builder.Services.AddOptions<AltinnClusterInfo>().BindConfiguration("Altinn:ClusterInfo");
        builder.Services.AddOptions<PlatformAccessTokenSettings>().BindConfiguration("Altinn:PlatformAccessToken");
        builder.Services.AddOptions<PlatformAccessTokenKeyVaultSettings>().BindConfiguration("Altinn:PlatformAccessToken:KeyVault");
        builder.Services.AddSingleton<IConfigureOptions<AltinnClusterInfo>, ConfigureAltinnClusterInfo>();
        builder.Services.AddOptions<ForwardedHeadersOptions>()
            .Configure((ForwardedHeadersOptions options, IOptionsMonitor<AltinnClusterInfo> clusterInfoOptions) =>
            {
                options.ForwardedHeaders = ForwardedHeaders.All;

                var clusterInfo = clusterInfoOptions.CurrentValue;
                if (clusterInfo.TrustedProxies is { Count: > 0 } trustedProxies)
                {
#if NET10_0_OR_GREATER
                    foreach (var network in trustedProxies)
                    {
                        options.KnownIPNetworks.Add(network);
                    }
#else
                    foreach (var network in trustedProxies)
                    {
                        options.KnownNetworks.Add(new AspNetCore.HttpOverrides.IPNetwork(network.BaseAddress, network.PrefixLength));
                    }
#endif
                }
            });

        if (options.EnabledServices.OpenTelemetry)
        {
            builder.ConfigureOpenTelemetry(options, logger);
        }

        if (options.EnabledServices.HealthCheck)
        {
            builder.AddDefaultHealthChecks();
        }

        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            var clientName = http.Name;

            // Turn on resilience by default
            http.AddStandardResilienceHandler();

            // Turn on service discovery by default
            http.AddServiceDiscovery();

            // Configure some logging for the circuit breaker
            http.Services.AddOptions<HttpStandardResilienceOptions>()
                .Configure((HttpStandardResilienceOptions options, HttpStandardResilienceTelemetry telemetry) =>
                {
                    options.CircuitBreaker.OnOpened = (context) =>
                    {
                        telemetry.CircuitBreakerOpened(clientName, in context);
                        return ValueTask.CompletedTask;
                    };

                    options.CircuitBreaker.OnHalfOpened = (context) =>
                    {
                        telemetry.CircuitBreakerHalfOpen(clientName, in context);
                        return ValueTask.CompletedTask;
                    };

                    options.CircuitBreaker.OnClosed = (context) =>
                    {
                        telemetry.CircuitBreakerClosed(clientName, in context);
                        return ValueTask.CompletedTask;
                    };
                });
        });

        builder.Services.AddAltinnScopesAuthorizationHandlers();

        return builder;
    }

    /// <summary>
    /// Adds metrics services and a default metrics provider to the specified service collection. Optionally allows
    /// further configuration of metrics options.
    /// </summary>
    /// <param name="services">The service collection to which the metrics services and provider will be added. Cannot be null.</param>
    /// <param name="configure">An optional delegate to configure metrics options.</param>
    /// <returns><paramref name="services"/>.</returns>
    public static IServiceCollection AddMetricsProvider(
        this IServiceCollection services,
        Action<IMetricsBuilder>? configure = null)
    {
        if (configure is null)
        {
            services.AddMetrics();
        }
        else
        {
            services.AddMetrics(configure);
        }

        services.TryAddSingleton<IMetricsProvider, DefaultMetricsProvider>();
        return services;
    }

    /// <summary>
    /// Adds default Altinn middleware.
    /// </summary>
    /// <remarks>
    /// Requires that <see cref="AddAltinnServiceDefaults(IHostApplicationBuilder, string, Action{AltinnServiceDefaultOptions})"/> has been called.
    /// </remarks>
    /// <param name="app">The <see cref="WebApplication"/>.</param>
    /// <returns><paramref name="app"/>.</returns>
    public static WebApplication AddDefaultAltinnMiddleware(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(AltinnServiceDefaultsExtensions));
        var otel = app.Services.GetService<LoggerProvider>()?.GetResource();
        logger.LogInformation("Startup // Configure");

        if (app.Environment.IsDevelopment() || app.Environment.IsStaging())
        {
            logger.LogInformation("IsDevelopment || IsStaging => Using developer exception page");

            app.UseDeveloperExceptionPage();
        }
        else
        {
            logger.LogInformation("Production => Using exception handler");

            app.UseExceptionHandler();
        }

        if (otel is not null)
        {
            logger.LogInformation("OpenTelemetry logging resource: {ResourceAttributes}", otel.Attributes);
        }

        app.UseForwardedHeaders();

        app.Use(static next => context =>
        {
            var tagsFeature = context.Features.Get<IHttpMetricsTagsFeature>();
            if (tagsFeature is null or { MetricsDisabled: true })
            {
                return next(context);
            }

            var disableMetrics = context.GetEndpoint()?.Metadata.GetMetadata<IDisableHttpMetricsMetadata>();
            if (disableMetrics is not null)
            {
                return next(context);
            }

            return EnrichFromResponse(context, next, tagsFeature);

            static async Task EnrichFromResponse(HttpContext context, RequestDelegate next, IHttpMetricsTagsFeature tagsFeature)
            {
                await next(context);
                TelemetryHelpers.EnrichFromRequest(tagsFeature, context);
            }
        });

        return app;
    }

    /// <summary>
    /// Maps default Altinn endpoints.
    /// </summary>
    /// <remarks>
    /// Requires that <see cref="AddAltinnServiceDefaults(IHostApplicationBuilder, string, Action{AltinnServiceDefaultOptions})"/> has been called.
    /// </remarks>
    /// <param name="app">The <see cref="WebApplication"/>.</param>
    /// <returns><paramref name="app"/>.</returns>
    public static WebApplication MapDefaultAltinnEndpoints(this WebApplication app)
    {
        var writer = app.Services.GetRequiredService<HealthReportWriter>();

        // All health checks must pass for app to be considered ready to accept traffic after starting
        app.MapHealthChecks(
            HealthEndpoint,
            new HealthCheckOptions
            {
                ResponseWriter = writer,
            })
            .DisableHttpMetrics();

        // Only health checks tagged with the "live" tag must pass for app to be considered alive
        app.MapHealthChecks(
            AliveEndpoint,
            new HealthCheckOptions
            {
                Predicate = static r => r.Tags.Contains("live"),
            })
            .DisableHttpMetrics();

        return app;
    }

    /// <summary>
    /// Gets the configured <see cref="AltinnServiceDescriptor"/>.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/>.</param>
    /// <returns>The <see cref="AltinnServiceDescriptor"/>.</returns>
    public static AltinnServiceDescriptor GetAltinnServiceDescriptor(this IServiceCollection services)
    {
        if (services.TryFindAltinnServiceDescription(out var serviceDescription))
        {
            return serviceDescription;
        }

        return ThrowHelper.ThrowInvalidOperationException<AltinnServiceDescriptor>("Service of type AltinnServiceDescription not registered - did you forget to call AddAltinnServiceDefaults?");
    }

    /// <summary>
    /// Gets the configured <see cref="AltinnServiceDescriptor"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder"/>.</param>
    /// <returns>The <see cref="AltinnServiceDescriptor"/>.</returns>
    public static AltinnServiceDescriptor GetAltinnServiceDescriptor(this IHostApplicationBuilder builder)
        => builder.Services.GetAltinnServiceDescriptor();

    /// <summary>
    /// Gets the configured <see cref="AltinnServiceDescriptor"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IHost"/>.</param>
    /// <returns>The <see cref="AltinnServiceDescriptor"/>.</returns>
    public static AltinnServiceDescriptor GetAltinnServiceDescriptor(this IHost builder)
        => builder.Services.GetRequiredService<AltinnServiceDescriptor>();

    /// <summary>
    /// Gets whether the service is running in local development mode.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/>.</param>
    /// <returns><see langword="true"/> if currently running in local-dev mode, otherwise <see langword="false"/>.</returns>
    public static bool IsLocalDevelopment(this IServiceCollection services)
        => services.GetAltinnServiceDescriptor().IsLocalDev;

    /// <summary>
    /// Gets whether the service is running in local development mode.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder"/>.</param>
    /// <returns><see langword="true"/> if currently running in local-dev mode, otherwise <see langword="false"/>.</returns>
    public static bool IsLocalDevelopment(this IHostApplicationBuilder builder)
        => builder.Services.IsLocalDevelopment();

    /// <summary>
    /// Gets whether the service is running in local development mode.
    /// </summary>
    /// <param name="builder">The <see cref="IHost"/>.</param>
    /// <returns><see langword="true"/> if currently running in local-dev mode, otherwise <see langword="false"/>.</returns>
    public static bool IsLocalDevelopment(this IHost builder)
        => builder.GetAltinnServiceDescriptor().IsLocalDev;

    private static bool TryFindAltinnServiceDescription(this IServiceCollection services, [NotNullWhen(true)] out AltinnServiceDescriptor? serviceDescription)
    {
        var descriptor = services.SingleOrDefault(s => s.ServiceType == typeof(AltinnServiceDescriptor));
        if (descriptor is null)
        {
            serviceDescription = null;
            return false;
        }

        if (descriptor.Lifetime != ServiceLifetime.Singleton)
        {
            ThrowHelper.ThrowInvalidOperationException("Service of type AltinnServiceDescription registered as non-singleton");
        }

        if (descriptor.ImplementationInstance is AltinnServiceDescriptor instance)
        {
            serviceDescription = instance;
            return true;
        }

        serviceDescription = null;
        return ThrowHelper.ThrowInvalidOperationException<bool>("Service of type AltinnServiceDescription registered without an instance");
    }

    private static IHostApplicationBuilder ConfigureOpenTelemetry(this IHostApplicationBuilder builder, AltinnServiceDefaultOptions options, AltinnPreStartLogger logger)
    {
        builder.Services.AddOptions<AltinnTelemetryOptions>()
            .BindConfiguration("Altinn:Telemetry");

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource =>
            {
                resource.AddAltinnServiceResource();
            })
            .WithLogging(
                configureBuilder: null,
                configureOptions: logging =>
                {
                    logging.IncludeFormattedMessage = true;
                    logging.IncludeScopes = true;
                }
            )
            .WithMetrics(metrics =>
            {
                metrics.AddRuntimeInstrumentation()
                       .AddBuiltInMeters()
                       .AddAspNetCoreInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation(o =>
                {
                    o.Filter = (httpContext) =>
                    {
                        if (TelemetryHelpers.ShouldExclude(httpContext.Request.Path))
                        {
                            return false;
                        }

                        return true;
                    };

                    o.EnrichWithHttpResponse = (activity, response) =>
                    {
                        if (activity is null || response is null)
                        {
                            return;
                        }

                        TelemetryHelpers.EnrichFromRequest(activity, response.HttpContext);
                    };
                });

                tracing.AddHttpClientInstrumentation();
                tracing.AddProcessor<TailSamplingProcessor>();
                tracing.ConfigureServices(services => services.AddSingleton<ITailSampler, ErrorTailSampler>());
            });

        builder.AddOpenTelemetryExporters(options, logger);
        builder.Services.ConfigureOpenTelemetryTracerProvider(tracing =>
        {
            // Note: this has to go *after* adding azure monitor, becuase it overwrites stuff
            tracing
                .SetSampler(services =>
                {
                    var config = services.GetRequiredService<IOptions<AltinnTelemetryOptions>>();

                    return config.Value.Sampling.ToSampler();
                })
                .ConfigureResource(resource =>
                {
                    resource.AddAltinnServiceResource();
                });
        });

        return builder;
    }

    private static ResourceBuilder AddAltinnServiceResource(this ResourceBuilder resource)
        // The environment variable detector goes last, so that OTEL_SERVICE_NAME and OTEL_RESOURCE_ATTRIBUTES
        // override the attributes set by AltinnServiceResourceDetector (such as service.name).
        => resource
            .AddDetector(services => services.GetRequiredService<AltinnServiceResourceDetector>())
            .AddEnvironmentVariableDetector();

    private static IHostApplicationBuilder AddOpenTelemetryExporters(this IHostApplicationBuilder builder, AltinnServiceDefaultOptions options, AltinnPreStartLogger logger)
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            logger.Log("OTEL_EXPORTER_OTLP_ENDPOINT is set - using OTLP exporter for OpenTelemetry");
            builder.Services.ConfigureOpenTelemetryLoggerProvider(logging => logging.AddOtlpExporter());
            builder.Services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddOtlpExporter());
            builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddOtlpExporter());
        }
        else if (options.EnabledServices.ApplicationInsights)
        {
            builder.AddApplicationInsights(logger);
        }

        return builder;
    }

    private static MeterProviderBuilder AddBuiltInMeters(this MeterProviderBuilder meterProviderBuilder)
        => meterProviderBuilder.AddMeter(
            "Microsoft.AspNetCore.Hosting",
            "Microsoft.AspNetCore.Server.Kestrel",
            "System.Net.Http");

    private static IHostApplicationBuilder AddDefaultHealthChecks(this IHostApplicationBuilder builder)
    {
        builder.Services.AddSingleton<HealthReportWriter>();
        builder.Services.AddOptions<HealthReportWriterSettings>()
            .Configure((HealthReportWriterSettings opts, IHostEnvironment env) =>
            {
                if (!env.IsProduction())
                {
                    opts.Exceptions |= HealthReportWriterSettings.ExceptionHandling.Include;
                }

                if (env.IsDevelopment())
                {
                    opts.Exceptions |= HealthReportWriterSettings.ExceptionHandling.IncludeStackTrace;
                }
            });

        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", static () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    private static IHostApplicationBuilder AddApplicationInsights(this IHostApplicationBuilder builder, AltinnPreStartLogger logger)
    {
        var applicationInsightsInstrumentationKey = builder.Configuration.GetValue<string>("ApplicationInsights:InstrumentationKey");
        var applicationInsightsConnectionString = builder.Configuration.GetValue<string>("ConnectionStrings:ApplicationInsights")
            ?? builder.Configuration.GetValue<string>("ApplicationInsights:ConnectionString");

        if (string.IsNullOrEmpty(applicationInsightsConnectionString) && !string.IsNullOrEmpty(applicationInsightsInstrumentationKey))
        {
            applicationInsightsConnectionString = $"InstrumentationKey={applicationInsightsInstrumentationKey}";
            builder.Configuration.AddInMemoryCollection([
                new("ConnectionStrings:ApplicationInsights", applicationInsightsConnectionString),
            ]);
        }

        if (!string.IsNullOrEmpty(applicationInsightsConnectionString))
        {
            logger.Log($"ApplicationInsightsConnectionString = {applicationInsightsConnectionString}");

            builder.Services.AddOpenTelemetry()
                .UseAzureMonitor(options =>
                {
                    options.ConnectionString = applicationInsightsConnectionString;
                });
        }
        else
        {
            logger.Log("No ApplicationInsights connection string - skipping Application Insights");
        }

        return builder;
    }

    private static IHostApplicationBuilder AddAltinnConfiguration(this IHostApplicationBuilder builder, AltinnServiceDescriptor serviceDescriptor, AltinnPreStartLogger logger)
    {
        builder.Configuration.AddAltinnDbSecretsJson(logger);
        builder.Configuration.AddAltinnKeyVault(logger);
        builder.AddAltinnAppConfiguration(serviceDescriptor, logger);

        return builder;
    }

    private static IConfigurationBuilder AddAltinnDbSecretsJson(this IConfigurationBuilder builder, AltinnPreStartLogger logger)
    {
        var parentDir = Path.GetDirectoryName(Environment.CurrentDirectory);
        if (parentDir is null)
        {
            logger.Log("No parent directory found - skipping altinn-dbsettings-secret.json");
            return builder;
        }

        var altinnDbSecretsConfigFile = Path.Combine(
            parentDir,
            "altinn-appsettings",
            "altinn-dbsettings-secret.json");

        if (!File.Exists(altinnDbSecretsConfigFile))
        {
            logger.Log($"No altinn-dbsettings-secret.json found at \"{altinnDbSecretsConfigFile}\" - skipping altinn-dbsettings-secret.json");
            return builder;
        }

        logger.Log($"Loading configuration from \"{altinnDbSecretsConfigFile}\"");
        builder.AddJsonFile(altinnDbSecretsConfigFile, optional: false, reloadOnChange: true);
        return builder;
    }

    private static IConfigurationBuilder AddAltinnKeyVault(this IConfigurationManager manager, AltinnPreStartLogger logger)
    {
        var clientId = manager.GetValue<string>("kvSetting:ClientId");
        var tenantId = manager.GetValue<string>("kvSetting:TenantId");
        var clientSecret = manager.GetValue<string>("kvSetting:ClientSecret");
        var keyVaultUri = manager.GetValue<string>("kvSetting:SecretUri");
        var enableEnvironmentCredential = manager.GetValue("kvSetting:Credentials:Environment:Enable", defaultValue: false);
        var enableWorkloadIdentityCredential = manager.GetValue("kvSetting:Credentials:WorkloadIdentity:Enable", defaultValue: true);
        var enableManagedIdentityCredential = manager.GetValue("kvSetting:Credentials:ManagedIdentity:Enable", defaultValue: true);

        if (!string.IsNullOrEmpty(keyVaultUri))
        {
            List<TokenCredential> credentialList = [];

            if (!string.IsNullOrEmpty(clientId)
                && !string.IsNullOrEmpty(tenantId)
                && !string.IsNullOrEmpty(clientSecret))
            {
                logger.Log($"adding config from keyvault using client-secret credentials");
                credentialList.Add(new ClientSecretCredential(
                    tenantId: tenantId,
                    clientId: clientId,
                    clientSecret: clientSecret));
            }

            if (enableEnvironmentCredential)
            {
                logger.Log("adding config from keyvault using environment credentials");
                credentialList.Add(new EnvironmentCredential());
            }

            if (enableWorkloadIdentityCredential)
            {
                logger.Log("adding config from keyvault using workload identity credentials");
                credentialList.Add(new WorkloadIdentityCredential());
            }

            if (enableManagedIdentityCredential)
            {
                logger.Log("adding config from keyvault using managed identity credentials");
                credentialList.Add(new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned));
            }

            if (credentialList.Count == 0)
            {
                logger.Log("No credentials found for keyvault - skipping adding keyvault to configuration");
                return manager;
            }

            var credential = new ChainedTokenCredential([.. credentialList]);

            manager.AddAzureKeyVault(new Uri(keyVaultUri), credential);
        }
        else
        {
            logger.Log($"Missing keyvault settings - skipping adding keyvault to configuration");
        }

        return manager;
    }

    private static IConfigurationBuilder AddAltinnAppConfiguration(
        this IHostApplicationBuilder builder,
        AltinnServiceDescriptor serviceDescriptor,
        AltinnPreStartLogger logger)
    {
        var manager = builder.Configuration;

        var appConfigurationEndpoint = manager.GetValue<string>("Altinn:AppConfiguration:Endpoint");
        var appConfigurationLabel = manager.GetValue<string>("Altinn:AppConfiguration:Label");
        var appConfigurationFeatureFlagsEnabled = manager.GetValue<bool>("Altinn:AppConfiguration:FeatureFlags:Enable");
        var enableEnvironmentCredential = manager.GetValue("Altinn:AppConfiguration:Credentials:Environment:Enable", defaultValue: false);
        var enableWorkloadIdentityCredential = manager.GetValue("Altinn:AppConfiguration:Credentials:WorkloadIdentity:Enable", defaultValue: true);
        var enableManagedIdentityCredential = manager.GetValue("Altinn:AppConfiguration:Credentials:ManagedIdentity:Enable", defaultValue: true);

        if (!string.IsNullOrEmpty(appConfigurationEndpoint))
        {
            if (!Uri.TryCreate(appConfigurationEndpoint, UriKind.Absolute, out var appConfigurationEndpointUri))
            {
                logger.Log($"Altinn:AppConfiguration:Endpoint is not a valid URI");
                ThrowHelper.ThrowInvalidOperationException("Altinn:AppConfiguration:Endpoint is not a valid URI");
            }

            if (string.IsNullOrEmpty(appConfigurationLabel))
            {
                logger.Log("Altinn:AppConfiguration:Label is missing but Altinn:AppConfiguration:Endpoint is set");
                ThrowHelper.ThrowInvalidOperationException("Altinn:AppConfiguration:Label is missing but Altinn:AppConfiguration:Endpoint is set");
            }

            List<TokenCredential> credentialList = [];

            if (enableEnvironmentCredential)
            {
                logger.Log("adding config from appconfiguration using environment credentials");
                credentialList.Add(new EnvironmentCredential());
            }

            if (enableWorkloadIdentityCredential)
            {
                logger.Log("adding config from appconfiguration using workload identity credentials");
                credentialList.Add(new WorkloadIdentityCredential());
            }

            if (enableManagedIdentityCredential)
            {
                logger.Log("adding config from appconfiguration using managed identity credentials");
                credentialList.Add(new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned));
            }

            if (credentialList.Count == 0)
            {
                logger.Log("No credentials configured for appconfiguration - skipping adding appconfiguration to configuration");
                return manager;
            }

            var credential = new ChainedTokenCredential([.. credentialList]);

            manager.AddAzureAppConfiguration(options =>
            {
                options.ConfigureRefresh(refresh =>
                {
                    refresh.RegisterAll();
                });

                if (!string.IsNullOrEmpty(appConfigurationLabel))
                {
                    logger.Log($"Adding app configuration label: '{appConfigurationLabel}'");
                    options.Select(KeyFilter.Any, appConfigurationLabel);
                }

                logger.Log($"Adding app configuration key filters for: '{serviceDescriptor.Name}', '{serviceDescriptor.Environment:l}', and '{serviceDescriptor.Environment:l}-{serviceDescriptor.Name}'");
                options.Select(KeyFilter.Any, serviceDescriptor.Name);
                options.Select(KeyFilter.Any, serviceDescriptor.Environment.ToString(format: "l"));
                options.Select(KeyFilter.Any, $"{serviceDescriptor.Environment:l}-{serviceDescriptor.Name}");

                options.ConfigureKeyVault(kvOptions => kvOptions.SetCredential(credential));
                options.Connect(appConfigurationEndpointUri, credential);

                if (appConfigurationFeatureFlagsEnabled)
                {
                    logger.Log("Enabling feature flags from app configuration");
                    options.UseFeatureFlags(fOpts =>
                    {
                        if (!string.IsNullOrEmpty(appConfigurationLabel))
                        {
                            fOpts.Select(KeyFilter.Any, appConfigurationLabel);
                        }

                        fOpts.Select(KeyFilter.Any, serviceDescriptor.Name);
                        fOpts.Select(KeyFilter.Any, serviceDescriptor.Environment.ToString(format: "l"));
                        fOpts.Select(KeyFilter.Any, $"{serviceDescriptor.Environment:l}-{serviceDescriptor.Name}");
                    });
                }
            });

            builder.Services.AddAzureAppConfiguration();
            builder.Services.AddSingleton<IHostedService, RefreshAppConfigurationHostedService>();
        }
        else
        {
            logger.Log($"Missing Altinn:AppConfiguration:Endpoint - skipping adding app configuration to configuration");
        }

        return manager;
    }
}
