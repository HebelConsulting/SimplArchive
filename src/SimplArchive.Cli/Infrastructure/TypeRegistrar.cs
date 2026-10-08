using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace SimplArchive.Cli.Infrastructure;

/// <summary>Spectre's command construction over a service collection, so a command can take an <c>ILogger&lt;T&gt;</c> (#1661).</summary>
public sealed class TypeRegistrar(IServiceCollection services) : ITypeRegistrar
{
    public ITypeResolver Build() => new TypeResolver(services.BuildServiceProvider());

    public void Register(Type service, Type implementation) => services.AddSingleton(service, implementation);

    public void RegisterInstance(Type service, object implementation) => services.AddSingleton(service, implementation);

    public void RegisterLazy(Type service, Func<object> factory) => services.AddSingleton(service, _ => factory());

    private sealed class TypeResolver(ServiceProvider provider) : ITypeResolver, IDisposable
    {
        public object? Resolve(Type? type) => type is null ? null : provider.GetService(type);

        public void Dispose() => provider.Dispose();
    }
}
