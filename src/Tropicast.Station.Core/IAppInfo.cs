namespace Tropicast.Station.Core;

/// <summary>Product identity shown in the UI and diagnostics.</summary>
public interface IAppInfo
{
    string ProductName { get; }

    string Version { get; }
}
