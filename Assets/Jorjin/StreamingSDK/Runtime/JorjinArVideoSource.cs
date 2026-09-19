using LiveKit;
using LiveKit.Proto;
using Unity.Collections;
using UnityEngine;

namespace Jorjin.Streaming
{
    /// <summary>
    /// LiveKit texture source for the Jorjin camera. The JJSDK/Unity texture
    /// has the opposite row direction from LiveKit's RGBA frame buffer, so
    /// only the transmitted frame is flipped here. The local preview remains
    /// exactly as produced by JorjinArCameraCapture.
    /// </summary>
    internal sealed class JorjinArVideoSource : ARVideoSource
    {
        private Texture2D texture;

        public JorjinArVideoSource(Texture2D texture)
            : base(texture)
        {
            this.texture = texture;
        }

        protected override bool ReadBuffer()
        {
            if (texture == null) return false;

            _reading = true;
            bool textureChanged = false;
            int currentWidth = texture.width;
            int currentHeight = texture.height;

            if (_previewTexture == null ||
                _previewTexture.width != currentWidth ||
                _previewTexture.height != currentHeight)
            {
                if (_previewTexture != null)
                {
                    Object.Destroy(_previewTexture);
                }
                if (_captureBuffer.IsCreated)
                {
                    _captureBuffer.Dispose();
                }

                _previewTexture = new Texture2D(
                    currentWidth,
                    currentHeight,
                    TextureFormat.RGBA32,
                    false);
                _captureBuffer = new NativeArray<byte>(
                    currentWidth * currentHeight * 4,
                    Allocator.Persistent);
                textureChanged = true;
            }

            Color32[] pixels = texture.GetPixels32();
            var destination = _captureBuffer.AsSpan();
            int rowBytes = currentWidth * 4;

            for (int destinationY = 0; destinationY < currentHeight; destinationY++)
            {
                int sourceY = currentHeight - 1 - destinationY;
                for (int x = 0; x < currentWidth; x++)
                {
                    Color32 color = pixels[sourceY * currentWidth + x];
                    int destinationIndex = destinationY * rowBytes + x * 4;
                    destination[destinationIndex] = color.r;
                    destination[destinationIndex + 1] = color.g;
                    destination[destinationIndex + 2] = color.b;
                    destination[destinationIndex + 3] = color.a;
                }
            }

            _requestPending = true;
            Graphics.CopyTexture(texture, _previewTexture);
            return textureChanged;
        }
    }
}
