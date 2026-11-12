using Silk.NET.Vulkan;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// VITASHARP_PRESENT_THREAD=1: vkQueuePresentKHR runs on a thread of its own instead of the GPU worker (on the Mali-G57
/// it took 2.6-3.6 ms of the worker's frame). One present at most is in flight: the worker hands the image over and goes
/// on with the next frame; the next acquire, a swapchain change, a device wait or the renderer's end first wait for it.
/// The queue is shared by both threads, so every vkQueueSubmit/vkQueuePresentKHR holds <see cref="_queueLock"/>; the
/// swapchain is never used by both at once (the acquire waits for the present). Off by default.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    private static readonly bool s_presentThreadOn = Environment.GetEnvironmentVariable(EnvVars.PresentThread) == "1";

    /// <summary>The queue's external synchronization (vkQueueSubmit on the worker, vkQueuePresentKHR on the present thread).</summary>
    private readonly object _queueLock = new();
    private readonly object _presentSlot = new();
    private Thread? _presentThread;
    private bool _presentPending, _presentQuit;
    private SwapchainKHR _pendingSwapchain;
    private uint _pendingIndex;
    private Silk.NET.Vulkan.Semaphore _pendingWait;

    /// <summary>Hands the present to the present thread (starting it the first time); the caller holds _presentLock.</summary>
    private void QueuePresentAsync(uint index, Silk.NET.Vulkan.Semaphore wait)
    {
        if (_presentThread == null)
        {
            _presentThread = new Thread(PresentLoop) { IsBackground = true, Name = "VitaSharp present" };
            _presentThread.Start();
            Log.Info($"{EnvVars.PresentThread}: vkQueuePresentKHR runs on its own thread");
        }
        lock (_presentSlot)
        {
            while (_presentPending)
            {
                Monitor.Wait(_presentSlot);
            }

            _pendingSwapchain = _swapchain;
            _pendingIndex = index;
            _pendingWait = wait;
            _presentPending = true;
            Monitor.PulseAll(_presentSlot);
        }
    }

    /// <summary>Waits until no present is in flight (before an acquire, a swapchain change, vkDeviceWaitIdle, the end).</summary>
    private void DrainPresent()
    {
        if (_presentThread == null)
        {
            return;
        }

        lock (_presentSlot)
        {
            while (_presentPending)
            {
                Monitor.Wait(_presentSlot);
            }
        }
    }

    private void PresentLoop()
    {
        while (true)
        {
            SwapchainKHR swapchainKHR;
            uint index;
            Silk.NET.Vulkan.Semaphore wait;
            lock (_presentSlot)
            {
                while (!_presentPending && !_presentQuit)
                {
                    Monitor.Wait(_presentSlot);
                }

                if (_presentQuit && !_presentPending)
                {
                    return;
                }

                swapchainKHR = _pendingSwapchain;
                index = _pendingIndex;
                wait = _pendingWait;
            }
            try
            {
                PresentNow(swapchainKHR, index, wait);
            }
            catch (Exception e)
            {
                Log.Warn($"present thread: {e.Message}");
            }
            lock (_presentSlot)
            {
                _presentPending = false;
                Monitor.PulseAll(_presentSlot);
            }
        }
    }

    private void StopPresentThread()
    {
        if (_presentThread == null)
        {
            return;
        }

        lock (_presentSlot)
        {
            _presentQuit = true;
            Monitor.PulseAll(_presentSlot);
        }
        _presentThread.Join();
        _presentThread = null;
    }
}
