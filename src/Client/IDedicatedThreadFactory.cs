namespace OpenCortex.CortexUSB.Client
{
    /// <summary>
    /// Optional capability an <see cref="ITransport"/> can implement when plain
    /// <c>new Thread(...).Start()</c> isn't safe to call from wherever
    /// <see cref="ProtocolClient"/> happens to be running (e.g. a background
    /// WebAssembly worker — see WebHidTransport in the CortexWasm project).
    /// When the active transport implements this, ProtocolClient asks it to
    /// start the dedicated reader thread instead of creating one directly.
    /// </summary>
    public interface IDedicatedThreadFactory
    {
        /// <summary>
        /// Starts <paramref name="body"/> as a new dedicated, long-running
        /// background thread (not a thread-pool task) and returns once it has
        /// been started. <paramref name="body"/> itself runs forever until the
        /// caller's cancellation token fires.
        /// </summary>
        void StartDedicatedThread(Action body);
    }
}
