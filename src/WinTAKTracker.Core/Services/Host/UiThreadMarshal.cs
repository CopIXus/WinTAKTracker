namespace WinTAKTracker.Services.Host;

/// <summary>
/// Optional UI-thread marshal for WinRT location consent. Set from the WPF app; null in the service.
/// </summary>
public static class UiThreadMarshal
{
    public static Func<Func<Task>, Task>? InvokeAsync { get; set; }

    public static Task InvokeAsyncOrDirect(Func<Task> action)
    {
        var marshal = InvokeAsync;
        return marshal is null ? action() : marshal(action);
    }
}
