using System.Reflection;
using System.Runtime.ExceptionServices;
using AutoMapper;
using IronyModManager.DI;
using IronyModManager.IO.Common;
using IronyModManager.Localization;
using IronyModManager.Localization.ResourceProviders;
using IronyModManager.Services.Common;
using IronyModManager.Services.Registrations;
using IronyModManager.Shared;
using IronyModManager.Shared.Cache;
using IronyModManager.Shared.Configuration;
using SimpleInjector;
using SimpleInjector.Packaging;

namespace IronyCli;

/// <summary>Composes the official backend without desktop startup or plugin discovery.</summary>
public sealed class BackendHost : IDisposable
{
    private static int initialized;
    private readonly Container container = new();
    private bool disposed;

    public IGameService Games { get; }
    public IModCollectionService Collections { get; }
    public IModService Mods { get; }
    public string DatabaseDirectory { get; }

    /// <param name="storageRoot">Explicit absolute parent of the IronyModManager database directory.</param>
    public BackendHost(string storageRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        if (!Path.IsPathFullyQualified(storageRoot))
            throw new ArgumentException("Storage root must be an absolute path.", nameof(storageRoot));

        storageRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storageRoot));
        if (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyTitleAttribute>()?.Title != "IronyModManager")
            throw new InvalidOperationException("The CLI assembly title must be IronyModManager for database compatibility.");

        // The upstream resolver, JsonStore path and mapper caches are process-global.
        if (Interlocked.Exchange(ref initialized, 1) != 0)
            throw new InvalidOperationException("Only one backend host may be initialized per process.");

        DatabaseDirectory = Path.Combine(storageRoot, "IronyModManager");
        try
        {
            container.Options.AllowOverridingRegistrations = true;
            container.Options.EnableAutoVerification = false;
            container.Options.ResolveUnregisteredConcreteTypes = true;

            // Reuse upstream factory/mapping semantics, but never call Bootstrap.Setup/PostStartup:
            // those discover desktop/plugins and run cleanup, updater and prank hooks.
            InvokeBackend("IronyModManager.DI.DIContainer", "Init", new DIOptions
            {
                Container = container,
                PluginPathAndName = "__disabled_cli_plugins"
            });
            InvokeBackend("IronyModManager.DI.Bootstrap", "ConfigureExtensions", container);

            container.RegisterInstance<IDomainConfiguration>(new HeadlessConfiguration(storageRoot));
            container.RegisterSingleton<ICache, Cache>();
            container.RegisterInstance<ILogger>(new StderrLogger());
            var resources = new ResourceProvider();
            container.Collection.Register<ILocalizationResourceProvider>(new[] { resources });
            container.RegisterInstance<IDefaultLocalizationResourceProvider>(resources);

            var assemblies = new List<Assembly>
            {
                typeof(LocalizationManager).Assembly,
                typeof(IronyModManager.Models.DIPackage).Assembly,
                typeof(IronyModManager.IO.DIPackage).Assembly,
                typeof(IronyModManager.Parser.DIPackage).Assembly,
                typeof(IronyModManager.Storage.DIPackage).Assembly,
                typeof(IronyModManager.Services.DIPackage).Assembly
            };
            container.RegisterPackages(assemblies);
            var configuration = (MapperConfiguration)InvokeBackend(
                "IronyModManager.DI.Mappers.MapperFinder", "Find", assemblies)!;
            container.RegisterInstance(configuration);
            container.Register<IMapper>(() => configuration.CreateMapper(container.GetInstance));
            InvokeBackend("IronyModManager.DI.MessageBus.MessageBusRegistration", "Register", assemblies);

            // Reject paths that upstream would reinterpret as environment-variable segments.
            if (!string.Equals(Path.TrimEndingDirectorySeparator(DiskOperations.ResolveStoragePath()),
                    storageRoot, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Storage root must not contain expandable environment-variable segments.", nameof(storageRoot));

            CurrentLocale.SetCurrent("en");
            new IronyModManager.IO.OrmInitialization().OnPostStartup();
            new GameRegistration().OnPostStartup();
            new ModSearchParserRegistration().OnPostStartup();

            Games = container.GetInstance<IGameService>();
            Collections = container.GetInstance<IModCollectionService>();
            Mods = container.GetInstance<IModService>();
        }
        catch
        {
            container.Dispose();
            throw;
        }
    }

    /// <summary>Upstream Jot persists synchronously on database property changes; no extra save is needed.</summary>
    public void Flush() => ObjectDisposedException.ThrowIf(disposed, this);

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        container.Dispose();
    }

    private static object? InvokeBackend(string typeName, string methodName, params object[] arguments)
    {
        var type = typeof(DIContainer).Assembly.GetType(typeName, throwOnError: true)!;
        var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeName, methodName);
        try
        {
            return method.Invoke(null, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private sealed class HeadlessConfiguration : IDomainConfiguration
    {
        private readonly DomainConfigurationOptions options = new();

        public HeadlessConfiguration(string storageRoot) => options.App.StoragePath = storageRoot;

        public DomainConfigurationOptions GetOptions() => options;
    }

    private sealed class ResourceProvider : BaseLocalizationResourceProvider, IDefaultLocalizationResourceProvider
    {
        public override string RootPath => Path.Combine(AppContext.BaseDirectory, "Localization");

        public IEnumerable<string> GetAvailableLocales() => Directory.EnumerateFiles(RootPath, "*.json")
            .Select(path => Path.GetFileNameWithoutExtension(path));
    }

    private sealed class StderrLogger : ILogger
    {
        private string lastFatal = string.Empty;

        public void Error(Exception ex, string message = "") => Console.Error.WriteLine($"{message} {ex}".Trim());
        public void Fatal(Exception ex, string message = "")
        {
            lastFatal = $"{message} {ex}".Trim();
            Console.Error.WriteLine(lastFatal);
        }
        public string GetLastFatalExceptionMessage() => lastFatal;
        public void Info(string message) => Console.Error.WriteLine(message);
        public void Trace(string message) => Console.Error.WriteLine(message);
    }
}
