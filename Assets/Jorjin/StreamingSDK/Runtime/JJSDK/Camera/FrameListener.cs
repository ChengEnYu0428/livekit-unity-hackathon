using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Scripting;

namespace Jorjin.Streaming.JJSDK
{
    /// <summary>
    /// Java callback proxy used by JJSDK. The byte-buffer callback is the path
    /// used by the verified Jorjin sample application.
    /// </summary>
    [Preserve]
    internal sealed class FrameListener : AndroidJavaProxy
    {
        public delegate void OnIncomingFrame(in Color32[] data, int width, int height, int format);
        public delegate void OnIncomingBytes(in byte[] bytes, int width, int height, int format);

        private Color32[] data;
        private readonly OnIncomingFrame incomingFrame;
        private readonly OnIncomingBytes incomingBytes;

        public bool IsNativeListener { get; }

        public FrameListener(OnIncomingFrame callback)
            : base("com.jorjin.jjsdk.camera.NativeFrameListener")
        {
            IsNativeListener = true;
            incomingFrame = callback;
        }

        public FrameListener(OnIncomingBytes callback)
            : base("com.jorjin.jjsdk.camera.FrameListener")
        {
            IsNativeListener = false;
            incomingBytes = callback;
        }

        [Preserve]
        public void onIncomingFrame(
            long pointer,
            int width,
            int height,
            int format,
            long timestampUs)
        {
            IntPtr source = new(pointer);
            int pixelCount = checked(width * height);
            if (data == null || data.Length != pixelCount)
                data = new Color32[pixelCount];

            GCHandle handle = default;
            try
            {
                handle = GCHandle.Alloc(data, GCHandleType.Pinned);
                IntPtr destination = handle.AddrOfPinnedObject();
                unsafe
                {
                    Buffer.MemoryCopy(
                        source.ToPointer(),
                        destination.ToPointer(),
                        pixelCount * 4L,
                        pixelCount * 4L);
                }
            }
            finally
            {
                if (handle.IsAllocated) handle.Free();
            }

            incomingFrame?.Invoke(data, width, height, format);
        }

        [Preserve]
        public void onIncomingFrame(
            AndroidJavaObject byteBuffer,
            int width,
            int height,
            int format)
        {
            // JJSDK's Java byte buffer contains four dummy bytes before the
            // actual RGBA pixels and three dummy bytes after them.
            byteBuffer.Call<int>("remaining");
            byte[] bytes = (byte[])(Array)byteBuffer.Call<sbyte[]>("array");
            incomingBytes?.Invoke(bytes, width, height, format);
        }
    }
}
