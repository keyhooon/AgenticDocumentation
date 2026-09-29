using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Sample;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class LocalAttribute : Attribute;
public class MarkerAttribute : Attribute;
public interface IService { void Run(); }
public interface IExtended : IService;
[Marker, Local]
public abstract class Base : IService
{
    private int secret;
    public int Counter;
    public string Label { get; private set; } = "";
    public abstract void Run();
    public int Calculate(int value = 7) => value + Counter;
    public int Calculate(string value) { return value.Length; }
    public void Use() { Counter = 1; Counter++; var current = Counter; Run(); Calculate(2); }
}
public class Service : Base, IExtended { public override void Run() { Counter += 2; } }
public class Grandchild : Service;
public partial class Split { public void First() { } }
public partial class Split { public void Second() { } }
public record Record(int Value);
public struct Value;
public enum Choice { One }
public delegate int Callback(int value);
public class Outer<T> { public class Inner { } }
public static class Startup
{
    public static void Configure(IServiceCollection services)
    {
        services.AddSingleton<IService, Service>();
        services.AddScoped(typeof(IService), typeof(Service));
        services.AddTransient<IService>(provider => new Service());
        services.AddSingleton<IService>(new Service());
        services.TryAddTransient<IService, Service>();
        services.AddKeyedSingleton<IService, Service>("key");
        new Pretender().AddSingleton<IService, Service>();
    }
}
public class Pretender { public void AddSingleton<TService, TImplementation>() { } }
