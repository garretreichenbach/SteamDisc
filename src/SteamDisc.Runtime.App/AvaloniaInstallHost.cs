using Avalonia.Controls;
using Avalonia.Threading;
using SteamDisc.Install;

namespace SteamDisc.Runtime.App;

/// <summary>
/// The skinned front-end's implementation of <see cref="IInstallHost"/>.
/// </summary>
/// <remarks>
/// The install engine runs off the UI thread and calls back into this host to ask the human
/// questions — restart Steam, swap a disc. Each call therefore hops onto the UI thread to show a
/// modal, and a <see cref="TaskCompletionSource{TResult}"/> bridges the dialog's result back to
/// the awaiting engine.
/// </remarks>
public sealed class AvaloniaInstallHost : IInstallHost
{
    private readonly Func<Window?> _window;
    private readonly Action<string> _reportWarning;

    public AvaloniaInstallHost(Func<Window?> window, Action<string> reportWarning)
    {
        _window = window;
        _reportWarning = reportWarning;
    }

    public Task<string?> RequestDiscAsync(DiscRequest request, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<string?>();

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var window = _window();
                var result = window is null ? null : await Dialogs.RequestDiscAsync(window, request);
                completion.TrySetResult(result);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        return completion.Task;
    }

    public void ReportWarning(string message) => _reportWarning(message);

    public Task<bool> ConfirmAsync(string question, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>();

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var window = _window();
                var result = window is not null && await Dialogs.ConfirmAsync(window, question);
                completion.TrySetResult(result);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        return completion.Task;
    }
}
