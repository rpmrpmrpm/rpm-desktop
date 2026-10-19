#if RPM_WINUI
// Adapted from Uno Platform's GLCanvasElement and WinUINativeOpenGLWrapper (Uno.WinUI.Graphics3DGL 6.7.65,
// WinUI build), Copyright (c) Uno Platform Inc., licensed under the Apache License, Version 2.0
// (http://www.apache.org/licenses/LICENSE-2.0). Changed from the original: the colour buffer is RGBA8
// instead of RGB, the readback goes through two pixel-pack buffers so a frame is collected one frame
// later instead of waiting for the GPU, the copy into the bitmap reuses one stream wrapper per buffer,
// and Invalidate does nothing while the window is minimised (see WindowSight).

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Silk.NET.Core.Contexts;
using Silk.NET.OpenGL;

namespace RPM.Desktop.Presentation.Common;

/// <summary>A XAML element drawn with OpenGL on the WinUI head. It is the base of the Pegasus stage
/// and of the visualizer stages there. See <c>GlCanvas</c> in GlCanvasAlias.cs. The Skia head uses
/// Uno's GLCanvasElement instead.
///
/// <para>WinUI has no surface that OpenGL can draw into. So each frame is drawn into an offscreen
/// framebuffer, read back to memory, and copied into a <see cref="WriteableBitmap"/> that the element
/// paints with. Uno's version read the frame back with a synchronous <c>glReadPixels</c>. That call
/// waits on the UI thread until the GPU has finished the frame. With Pegasus open at 1920x1080 this
/// took 32 percent of the UI thread, plus 11 percent for the copy. Here the read is queued into one of
/// two pixel-pack buffers. The other buffer, filled on the frame before, is copied out. The GPU
/// finishes in the meantime, so the map does not wait. The cost is one frame of latency, about 16 ms.
/// The first frame after a resize is read synchronously, so the element never shows an empty
/// frame.</para>
///
/// <para>Everything here runs on the UI thread. Render is queued on the dispatcher at low priority, as
/// in Uno's version.</para>
/// </summary>
public abstract partial class WinGlCanvas : Grid, INativeContext
{
    private const uint PixelPackBuffer = 0x88EB;   // GL_PIXEL_PACK_BUFFER
    private const uint StreamRead = 0x88E1;        // GL_STREAM_READ
    private const uint MapReadBit = 0x0001;        // GL_MAP_READ_BIT
    private const uint Bgra = 0x80E1;              // GL_BGRA
    private const uint UnsignedByte = 0x1401;      // GL_UNSIGNED_BYTE
    private const uint FramebufferTarget = 0x8D40; // GL_FRAMEBUFFER
    private const uint ColorAttachment0 = 0x8CE0;  // GL_COLOR_ATTACHMENT0

    /// <summary>One OpenGL context per window, shared by every element in it, as in Uno's
    /// version.</summary>
    private static readonly Dictionary<XamlRoot, WglContext?> Contexts = [];

    private readonly Func<Window> _getWindow;
    private WglContext? _context;
    private GL? _gl;
    private Offscreen? _offscreen;
    private WriteableBitmap? _backBuffer;
    private bool _initialised;
    private bool _failed;

    /// <summary>Whether GL is up for this element: null before it is loaded, false if it failed.</summary>
    public bool? IsGLInitialized => _failed ? false : _initialised ? true : null;

    protected abstract void Init(GL gl);

    protected abstract void OnDestroy(GL gl);

    protected abstract void RenderOverride(GL gl);

    protected WinGlCanvas(Func<Window> getWindow)
    {
        _getWindow = getWindow;

        // GL's rows run bottom to top; the brush flips them.
        Background = new ImageBrush
        {
            RelativeTransform = new ScaleTransform { ScaleX = 1, ScaleY = -1, CenterX = 0.5, CenterY = 0.5 },
        };

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => UpdateFramebuffer();
    }

    /// <summary>Requests a frame. Ignored while the window is minimised, because nobody can see the
    /// frame, and the readback is the most expensive work the UI thread does while Pegasus is up. The
    /// first frame after the window is restored is requested by <see cref="OnSightChanged"/>.</summary>
    public void Invalidate()
    {
        if (_failed || WindowSight.Minimised)
            return;

        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, Render);
    }

    private void OnSightChanged()
    {
        if (!WindowSight.Minimised && _initialised)
            Invalidate();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _context = ContextFor(XamlRoot, _getWindow);
        if (_context is null)
        {
            _failed = true;
            return;
        }

        _gl = GL.GetApi(this);
        using (_context.MakeCurrent())
        {
            UpdateFramebufferCurrent();
            if (_failed || _offscreen is null)
                return;

            try
            {
                _gl.BindFramebuffer((GLEnum)FramebufferTarget, _offscreen.Framebuffer);
                Init(_gl);
            }
            catch (Exception ex)
            {
                RPM.Core.Diagnostics.Said.Line("gl", $"initialisation failed: {ex.GetType().Name}: {ex.Message}");
                _failed = true;
                return;
            }
        }

        _getWindow().Closed += OnWindowClosed;

        // Subscribed only while loaded. The event is static. A subscription made in the constructor
        // would keep every stage ever created alive.
        WindowSight.Changed += OnSightChanged;
        _initialised = true;
        Invalidate();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _initialised = false;
        WindowSight.Changed -= OnSightChanged;
        if (_context is null || _gl is null)
            return;

        using (_context.MakeCurrent())
        {
            // If the window is closing, it has already taken the context with it. Nothing is left to
            // delete.
            if (WglContext.wglGetCurrentContext() != 0)
            {
                try
                {
                    OnDestroy(_gl);
                }
                catch (Exception ex)
                {
                    RPM.Core.Diagnostics.Said.Line("gl", $"OnDestroy failed: {ex.GetType().Name}: {ex.Message}");
                }

                _offscreen?.Dispose();
                _gl.Dispose();
            }
        }

        _gl = null;
        _offscreen = null;
        _getWindow().Closed -= OnWindowClosed;
    }

    private void OnWindowClosed(object sender, WindowEventArgs args) =>
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (XamlRoot is { } root && Contexts.Remove(root, out var context) && context is not null)
            {
                using (context.MakeCurrent())
                    context.Dispose();
            }
        });

    private void UpdateFramebuffer()
    {
        if (!_initialised || _context is null)
            return;

        using (_context.MakeCurrent())
            UpdateFramebufferCurrent();

        Invalidate();
    }

    /// <summary>Rebuilds the offscreen buffers at the element's size. The context must be
    /// current.</summary>
    private void UpdateFramebufferCurrent()
    {
        if (_gl is null)
            return;

        var width = Math.Max(1, (int)RenderSize.Width);
        var height = Math.Max(1, (int)RenderSize.Height);

        try
        {
            _offscreen?.Dispose();
            _offscreen = new Offscreen(_gl, width, height);
        }
        catch (Exception ex)
        {
            RPM.Core.Diagnostics.Said.Line("gl", $"framebuffer update failed: {ex.GetType().Name}: {ex.Message}");
            _offscreen = null;
            _failed = true;
            return;
        }

        _backBuffer = new WriteableBitmap(width, height);
        ((ImageBrush)Background).ImageSource = _backBuffer;
    }

    private unsafe void Render()
    {
        if (!_initialised || _failed || !IsLoaded || _context is null || _gl is null
            || _offscreen is not { } off || _backBuffer is null)
            return;

        using (_context.MakeCurrent())
        {
            try
            {
                _gl.BindFramebuffer((GLEnum)FramebufferTarget, off.Framebuffer);
                _gl.Viewport(0, 0, (uint)off.Width, (uint)off.Height);
                RenderOverride(_gl);

                // Errors left by RenderOverride belong to the stage. Clear them, so that the check
                // after the readback reads only its own errors.
                for (var i = 0; i < 8 && _gl.GetError() != GLEnum.NoError; i++) { }

                _gl.ReadBuffer((GLEnum)ColorAttachment0);

                // Queue this frame into one buffer...
                var write = off.Next;
                _gl.BindBuffer((GLEnum)PixelPackBuffer, off.Pack[write]);
                _gl.ReadPixels(0, 0, (uint)off.Width, (uint)off.Height, (GLEnum)Bgra, (GLEnum)UnsignedByte, (void*)0);

                // ...and copy out the other one, which was filled a frame ago. The first frame has no
                // earlier frame to copy, so it copies itself and waits. Uno's version waited like this
                // for every frame.
                var read = off.HasPrevious ? 1 - write : write;
                _gl.BindBuffer((GLEnum)PixelPackBuffer, off.Pack[read]);
                var pixels = _gl.MapBufferRange((GLEnum)PixelPackBuffer, 0, (nuint)off.Bytes, (uint)MapReadBit);
                if (pixels != null)
                {
                    using (var stream = _backBuffer.PixelBuffer.AsStream())
                        stream.Write(new ReadOnlySpan<byte>(pixels, off.Bytes));
                    _gl.UnmapBuffer((GLEnum)PixelPackBuffer);
                }

                _gl.BindBuffer((GLEnum)PixelPackBuffer, 0);
                off.Next = 1 - write;
                off.HasPrevious = true;

                var error = _gl.GetError();
                if (error != GLEnum.NoError)
                    throw new InvalidOperationException($"the framebuffer readback failed with {error}");

                _backBuffer.Invalidate();
            }
            catch (Exception ex)
            {
                RPM.Core.Diagnostics.Said.Line("gl", $"rendering failed, the element stops drawing: {ex.GetType().Name}: {ex.Message}");
                _failed = true;
            }
        }
    }

    nint INativeContext.GetProcAddress(string proc, int? slot) =>
        _context?.GetProcAddress(proc) ?? throw new InvalidOperationException("no GL context");

    bool INativeContext.TryGetProcAddress(string proc, out nint addr, int? slot)
    {
        if (_context is { } context)
            return context.TryGetProcAddress(proc, out addr);
        addr = 0;
        return false;
    }

    void IDisposable.Dispose() { }

    /// <summary>The window's context, made on first use and shared by every element in that
    /// window.</summary>
    private static unsafe WglContext? ContextFor(XamlRoot root, Func<Window> getWindow)
    {
        if (Contexts.TryGetValue(root, out var known))
            return known;

        WglContext? context = null;
        try
        {
            context = new WglContext(getWindow());
            using (context.MakeCurrent())
            {
                var getString = (delegate* unmanaged[Cdecl]<uint, byte*>)context.GetProcAddress("glGetString");
                var version = Marshal.PtrToStringUTF8((nint)getString(0x1F02)) ?? "";   // GL_VERSION
                RPM.Core.Diagnostics.Said.Line("gl", $"context created, version {version}");

                var getInteger = (delegate* unmanaged[Cdecl]<uint, int*, void>)context.GetProcAddress("glGetIntegerv");
                int major = 0, minor = 0;
                getInteger(0x821B, &major);   // GL_MAJOR_VERSION
                getInteger(0x821C, &minor);   // GL_MINOR_VERSION

                // Pixel-pack buffers and MapBufferRange are core in OpenGL 3.0. Uno's version also
                // requires 3.0.
                if (major < 3)
                {
                    RPM.Core.Diagnostics.Said.Line("gl", $"OpenGL {major}.{minor} is below 3.0; GL elements will not draw");
                    context.Dispose();
                    context = null;
                }
            }
        }
        catch (Exception ex)
        {
            RPM.Core.Diagnostics.Said.Line("gl", $"context creation failed: {ex.GetType().Name}: {ex.Message}");
            context?.Dispose();
            context = null;
        }

        Contexts[root] = context;
        return context;
    }

    /// <summary>The offscreen framebuffer (RGBA8 colour, 24/8 depth-stencil) and the two pixel-pack
    /// buffers the frames are read back through.</summary>
    private sealed partial class Offscreen : IDisposable
    {
        private readonly GL _gl;
        private readonly uint _colour;
        private readonly uint _depthStencil;

        public uint Framebuffer { get; }

        public uint[] Pack { get; } = new uint[2];

        public int Width { get; }

        public int Height { get; }

        public int Bytes => Width * Height * 4;

        /// <summary>Which pack buffer the next frame is read into.</summary>
        public int Next { get; set; }

        /// <summary>Whether the other pack buffer holds a frame.</summary>
        public bool HasPrevious { get; set; }

        public unsafe Offscreen(GL gl, int width, int height)
        {
            _gl = gl;
            Width = width;
            Height = height;

            Framebuffer = gl.GenFramebuffer();
            gl.BindFramebuffer((GLEnum)FramebufferTarget, Framebuffer);

            _colour = gl.GenTexture();
            gl.BindTexture(GLEnum.Texture2D, _colour);
            gl.TexImage2D(GLEnum.Texture2D, 0, (int)GLEnum.Rgba8, (uint)width, (uint)height, 0,
                GLEnum.Rgba, GLEnum.UnsignedByte, null);
            gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMinFilter, (int)GLEnum.Linear);
            gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMagFilter, (int)GLEnum.Linear);
            gl.FramebufferTexture2D((GLEnum)FramebufferTarget, (GLEnum)ColorAttachment0, GLEnum.Texture2D, _colour, 0);
            gl.BindTexture(GLEnum.Texture2D, 0);

            _depthStencil = gl.GenRenderbuffer();
            gl.BindRenderbuffer(GLEnum.Renderbuffer, _depthStencil);
            gl.RenderbufferStorage(GLEnum.Renderbuffer, GLEnum.Depth24Stencil8, (uint)width, (uint)height);
            gl.FramebufferRenderbuffer((GLEnum)FramebufferTarget, GLEnum.DepthStencilAttachment, GLEnum.Renderbuffer, _depthStencil);
            gl.BindRenderbuffer(GLEnum.Renderbuffer, 0);

            if (gl.CheckFramebufferStatus((GLEnum)FramebufferTarget) != GLEnum.FramebufferComplete)
                throw new InvalidOperationException("the offscreen framebuffer is not complete");

            gl.BindFramebuffer((GLEnum)FramebufferTarget, 0);

            for (var i = 0; i < Pack.Length; i++)
            {
                Pack[i] = gl.GenBuffer();
                gl.BindBuffer((GLEnum)PixelPackBuffer, Pack[i]);
                gl.BufferData((GLEnum)PixelPackBuffer, (nuint)Bytes, null, (GLEnum)StreamRead);
            }

            gl.BindBuffer((GLEnum)PixelPackBuffer, 0);
        }

        public void Dispose()
        {
            _gl.DeleteBuffers(Pack);
            _gl.DeleteFramebuffer(Framebuffer);
            _gl.DeleteTexture(_colour);
            _gl.DeleteRenderbuffer(_depthStencil);
        }
    }

    /// <summary>A WGL context on the window's device context. It is made the same way as in Uno's
    /// WinUINativeOpenGLWrapper.</summary>
    private sealed partial class WglContext : IDisposable
    {
        private static readonly Lazy<nint> OpenGl32 = new(() =>
            NativeLibrary.TryLoad("opengl32.dll", typeof(WglContext).Assembly, DllImportSearchPath.System32, out var handle)
                ? handle
                : 0);

        private nint _hdc;
        private nint _context;

        public WglContext(Window window)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            _hdc = GetDC(hwnd);

            var format = new PixelFormatDescriptor
            {
                nSize = (ushort)Marshal.SizeOf<PixelFormatDescriptor>(),
                nVersion = 1,
                dwFlags = 0x25,   // PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER
                cColorBits = 32,
                cRedBits = 8,
                cGreenBits = 8,
                cBlueBits = 8,
                cAlphaBits = 8,
                cDepthBits = 16,
                cStencilBits = 1,
            };

            var chosen = ChoosePixelFormat(_hdc, ref format);
            if (chosen == 0)
                throw new InvalidOperationException("ChoosePixelFormat failed");
            if (SetPixelFormat(_hdc, chosen, ref format) == 0)
                throw new InvalidOperationException("SetPixelFormat failed");

            _context = wglCreateContext(_hdc);
            if (_context == 0)
                throw new InvalidOperationException("wglCreateContext failed");
        }

        public bool TryGetProcAddress(string proc, out nint addr)
        {
            if (OpenGl32.Value != 0 && NativeLibrary.TryGetExport(OpenGl32.Value, proc, out addr))
                return true;
            addr = wglGetProcAddress(proc);
            return addr != 0;
        }

        public nint GetProcAddress(string proc) =>
            TryGetProcAddress(proc, out var addr) ? addr : throw new InvalidOperationException($"no GL function {proc}");

        /// <summary>Makes this context current and puts the previous one back when disposed.</summary>
        public Restore MakeCurrent()
        {
            var previousContext = wglGetCurrentContext();
            var previousDc = wglGetCurrentDC();
            if (wglMakeCurrent(_hdc, _context) == 0)
                RPM.Core.Diagnostics.Said.Line("gl", "wglMakeCurrent failed");
            return new Restore(previousDc, previousContext);
        }

        public void Dispose()
        {
            if (_context != 0 && wglDeleteContext(_context) == 0)
                RPM.Core.Diagnostics.Said.Line("gl", "wglDeleteContext failed");
            _context = 0;
            _hdc = 0;
        }

        public readonly struct Restore(nint dc, nint context) : IDisposable
        {
            public void Dispose() => wglMakeCurrent(dc, context);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PixelFormatDescriptor
        {
            public ushort nSize;
            public ushort nVersion;
            public uint dwFlags;
            public byte iPixelType;
            public byte cColorBits;
            public byte cRedBits;
            public byte cRedShift;
            public byte cGreenBits;
            public byte cGreenShift;
            public byte cBlueBits;
            public byte cBlueShift;
            public byte cAlphaBits;
            public byte cAlphaShift;
            public byte cAccumBits;
            public byte cAccumRedBits;
            public byte cAccumGreenBits;
            public byte cAccumBlueBits;
            public byte cAccumAlphaBits;
            public byte cDepthBits;
            public byte cStencilBits;
            public byte cAuxBuffers;
            public byte iLayerType;
            public byte bReserved;
            public uint dwLayerMask;
            public uint dwVisibleMask;
            public uint dwDamageMask;
        }

        [LibraryImport("user32.dll")]
        private static partial nint GetDC(nint hWnd);

        [LibraryImport("gdi32.dll")]
        private static partial int ChoosePixelFormat(nint hdc, ref PixelFormatDescriptor ppfd);

        [LibraryImport("gdi32.dll")]
        private static partial int SetPixelFormat(nint hdc, int iPixelFormat, ref PixelFormatDescriptor ppfd);

        [LibraryImport("opengl32.dll", StringMarshalling = StringMarshalling.Utf8)]
        private static partial nint wglGetProcAddress(string functionName);

        [LibraryImport("opengl32.dll")]
        private static partial nint wglCreateContext(nint hdc);

        [LibraryImport("opengl32.dll")]
        private static partial nint wglGetCurrentDC();

        [LibraryImport("opengl32.dll")]
        internal static partial nint wglGetCurrentContext();

        [LibraryImport("opengl32.dll")]
        private static partial int wglDeleteContext(nint hglrc);

        [LibraryImport("opengl32.dll")]
        private static partial int wglMakeCurrent(nint hdc, nint hglrc);
    }
}
#endif
