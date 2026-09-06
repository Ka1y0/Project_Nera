using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ChipsStudio.Nera.Control;

/// <summary>
/// A dedicated Win32 message-thread implementation of RegisterHotKey. Registrations use a null
/// HWND and therefore belong to this thread; all register/unregister calls are marshalled back to it.
/// </summary>
public sealed class WindowsGlobalHotkeyPlatform : IGlobalHotkeyPlatform
{
    private const uint WmHotkey = 0x0312;
    private const uint WmApp = 0x8000;
    private const uint WorkMessage = WmApp + 0x31;
    private const uint StopMessage = WmApp + 0x32;
    private const int PlatformTimeoutMilliseconds = 5_000;

    private readonly ConcurrentQueue<PlatformRequest> requests_ = new();
    private readonly ManualResetEventSlim ready_ = new(false);
    private readonly Thread thread_;
    private readonly Dictionary<int, NeraHotkeyChord> registeredIds_ = [];
    private Exception? startupError_;
    private uint threadId_;
    private int disposed_;

    public WindowsGlobalHotkeyPlatform()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("RegisterHotKey 仅可在 Windows 上使用。");
        }

        thread_ = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "Nera managed global hotkeys"
        };
        thread_.Start();
        if (!ready_.Wait(PlatformTimeoutMilliseconds))
        {
            throw new TimeoutException("RegisterHotKey 消息线程未能在时限内初始化。");
        }
        if (startupError_ is not null)
        {
            throw new InvalidOperationException("RegisterHotKey 消息线程初始化失败。", startupError_);
        }
    }

    public event Action<NeraHotkeyPlatformActivation>? Activated;

    public NeraHotkeyPlatformResult TryRegister(int id, uint modifiers, uint virtualKey)
    {
        if (id is <= 0 or > 0xBFFF || !new NeraHotkeyChord(modifiers, virtualKey).IsValid ||
            (modifiers & NeraHotkeyModifiers.NoRepeat) == 0)
        {
            return NeraHotkeyPlatformResult.Failure(87); // ERROR_INVALID_PARAMETER
        }
        return Send(() =>
        {
            if (registeredIds_.ContainsKey(id)) return NeraHotkeyPlatformResult.Failure(1409);
            var registered = NativeMethods.RegisterHotKey(IntPtr.Zero, id, modifiers, virtualKey);
            var error = registered ? 0 : Marshal.GetLastPInvokeError();
            if (registered)
            {
                registeredIds_[id] = new(NeraHotkeyModifiers.Persisted(modifiers), virtualKey);
            }
            return new NeraHotkeyPlatformResult(registered, error);
        });
    }

    public NeraHotkeyPlatformResult TryUnregister(int id) => Send(() =>
    {
        var removed = NativeMethods.UnregisterHotKey(IntPtr.Zero, id);
        var error = removed ? 0 : Marshal.GetLastPInvokeError();
        if (removed)
        {
            registeredIds_.Remove(id);
        }
        return new NeraHotkeyPlatformResult(removed, error);
    });

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed_, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        var id = Volatile.Read(ref threadId_);
        if (id != 0)
        {
            NativeMethods.PostThreadMessageW(id, StopMessage, UIntPtr.Zero, IntPtr.Zero);
        }
        if (thread_.IsAlive && Thread.CurrentThread != thread_)
        {
            thread_.Join(PlatformTimeoutMilliseconds);
        }
        ready_.Dispose();
        return ValueTask.CompletedTask;
    }

    private NeraHotkeyPlatformResult Send(Func<NeraHotkeyPlatformResult> operation)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed_) != 0, this);
        var request = new PlatformRequest(operation);
        requests_.Enqueue(request);
        if (!NativeMethods.PostThreadMessageW(
                Volatile.Read(ref threadId_), WorkMessage, UIntPtr.Zero, IntPtr.Zero))
        {
            requests_.TryDequeue(out _);
            return NeraHotkeyPlatformResult.Failure(Marshal.GetLastPInvokeError());
        }

        try
        {
            return request.Completion.Task.WaitAsync(TimeSpan.FromMilliseconds(PlatformTimeoutMilliseconds))
                .GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            return NeraHotkeyPlatformResult.Failure(1460); // ERROR_TIMEOUT
        }
    }

    private void ThreadMain()
    {
        try
        {
            // PeekMessage forces creation of this thread's Win32 message queue before it is published.
            NativeMethods.PeekMessageW(out _, IntPtr.Zero, 0, 0, 0);
            Volatile.Write(ref threadId_, NativeMethods.GetCurrentThreadId());
            ready_.Set();

            while (true)
            {
                var result = NativeMethods.GetMessageW(out var message, IntPtr.Zero, 0, 0);
                if (result <= 0)
                {
                    break;
                }
                if (message.Message == WorkMessage)
                {
                    DrainRequests();
                    continue;
                }
                if (message.Message == StopMessage)
                {
                    break;
                }
                if (message.Message == WmHotkey)
                {
                    int activatedId = unchecked((int)message.WParam);
                    if (!registeredIds_.TryGetValue(activatedId, out var registered) ||
                        !NeraHotkeyMessagePolicy.IsActivation(message.Message, message.LParam, registered))
                        continue;
                    try
                    {
                        Activated?.Invoke(new NeraHotkeyPlatformActivation(activatedId, new NeraInputOrigin
                        {
                            InputKind = NeraInputKind.KeyboardHotkey,
                            Message = message.Message,
                            RegistrationId = activatedId,
                            Vk = registered.VirtualKey,
                            Modifiers = registered.PersistedModifiers,
                            SourceHwnd = message.Hwnd.ToInt64(),
                            SourceThreadId = NativeMethods.GetCurrentThreadId(),
                            SourcePid = Environment.ProcessId
                        }));
                    }
                    catch
                    {
                        // An observer cannot terminate the registration/message thread.
                    }
                }
            }
        }
        catch (Exception error)
        {
            startupError_ = error;
            ready_.Set();
        }
        finally
        {
            // Defense in depth: every successful ID is attempted even when a previous removal fails.
            foreach (var id in registeredIds_.Keys.ToArray())
            {
                NativeMethods.UnregisterHotKey(IntPtr.Zero, id);
                registeredIds_.Remove(id);
            }
            FailPendingRequests(new ObjectDisposedException(nameof(WindowsGlobalHotkeyPlatform)));
            Volatile.Write(ref threadId_, 0);
        }
    }

    private void DrainRequests()
    {
        while (requests_.TryDequeue(out var request))
        {
            try
            {
                request.Completion.TrySetResult(request.Operation());
            }
            catch (Exception error)
            {
                request.Completion.TrySetException(error);
            }
        }
    }

    private void FailPendingRequests(Exception error)
    {
        while (requests_.TryDequeue(out var request))
        {
            request.Completion.TrySetException(error);
        }
    }

    private sealed class PlatformRequest(Func<NeraHotkeyPlatformResult> operation)
    {
        internal Func<NeraHotkeyPlatformResult> Operation { get; } = operation;
        internal TaskCompletionSource<NeraHotkeyPlatformResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        internal IntPtr Hwnd;
        internal uint Message;
        internal UIntPtr WParam;
        internal IntPtr LParam;
        internal uint Time;
        internal NativePoint Point;
        internal uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostThreadMessageW(
            uint threadId,
            uint message,
            UIntPtr wParam,
            IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PeekMessageW(
            out NativeMessage message,
            IntPtr hWnd,
            uint minimum,
            uint maximum,
            uint removeMessage);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int GetMessageW(
            out NativeMessage message,
            IntPtr hWnd,
            uint minimum,
            uint maximum);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();
    }
}
