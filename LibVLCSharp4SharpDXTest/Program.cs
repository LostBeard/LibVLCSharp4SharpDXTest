using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using LibVLCSharp.WinForms;
using static LibVLCSharp.MediaPlayer;
using Device = SharpDX.Direct3D11.Device;
using Device1 = SharpDX.Direct3D11.Device1;
using Format = SharpDX.DXGI.Format;

namespace LibVLCSharp.CustomRendering.Direct3D11
{
    // https://code.videolan.org/videolan/vlc/-/blob/e19dc449339e740a579d44e81ceed72ba56914e5/doc/libvlc/d3d11_player.cpp
    // https://code.videolan.org/videolan/vlc/-/blob/e19dc449339e740a579d44e81ceed72ba56914e5/src/misc/es_format.c
    static class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [SuppressUnmanagedCodeSecurity]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseHandle(IntPtr hObject);

        
        static Format _renderFormat = Format.R8G8B8A8_UNorm;
        static RawColor4 _colorBlack = new RawColor4(0.0f, 0.0f, 0.0f, 1.0f);
        static bool _verticalSync = false;

        static Form _form;

        static SwapChainDescription _swapDesc;
        static SwapChain _swapChain;
        static RenderTargetView _swapchainRenderTarget;

        static Device _d3dDevice;
        static DeviceContext _d3dctx;

        static int _outWidth = 640;
        static int _outHeight = 480;

        static int _textureWidth = 0;
        static int _textureHeight = 0;

        static Device _d3deviceVLC;
        static DeviceContext _d3dctxVLC;

        static Texture2D _textureVLC;
        static RenderTargetView _textureVLCRenderTarget;
        static nint _textureSharedHandle;
        static Texture2D _texture;
        static ShaderResourceView _textureShaderResource;

        static SamplerState _samplerState;

        static VertexShader _basicVS;
        static PixelShader _basicPS;

        static LibVLC _libvlc;
        static MediaPlayer _mediaplayer;

        static OutputResize _reportSize;
        static MouseMove _mouseMove;
        static MousePress _mousePress;
        static MouseRelease _mouseRelease;

        static IntPtr _reportOpaque;

        /// <summary>
        /// Entry point
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        static async Task Main(string[] args)
        {
            CreateWindow();
            InitializeDirect3D();
            InitializeLibVLC();
            //
            using var media = new Media(@"V:\Video\Big Buck Bunny - FULL HD 60FPS.mp4");
            _mediaplayer.Media = media;
            _mediaplayer.Play();
            //
            Application.Run();
        }
        /// <summary>
        /// Creates the app's window
        /// </summary>
        static void CreateWindow()
        {
            _form = new Form() { Width = _outWidth, Height = _outHeight, Text = typeof(Program).Namespace };
            _form.Show();
            _form.Resize += Form_Resize;
            _form.FormClosing += Form_FormClosing;
        }
        /// <summary>
        /// Initializes DX11
        /// </summary>
        static void InitializeDirect3D()
        {
            var flags = DeviceCreationFlags.None;
#if DEBUG
            flags |= DeviceCreationFlags.Debug;
#endif
            // VLC device and context
            _d3deviceVLC = new Device(DriverType.Hardware, flags | DeviceCreationFlags.VideoSupport, FeatureLevel.Level_11_1);
            _d3dctxVLC = _d3deviceVLC.ImmediateContext;

            // Primary device, context, and swapchain
            _swapDesc = new SwapChainDescription()
            {
                BufferCount = 1,
                ModeDescription = new ModeDescription(_outWidth, _outHeight, new Rational(60, 1), Format.R8G8B8A8_UNorm),
                IsWindowed = true,
                OutputHandle = _form.Handle,
                SampleDescription = new SampleDescription(1, 0),
                Usage = Usage.RenderTargetOutput,
                Flags = SwapChainFlags.AllowModeSwitch,
                SwapEffect = SwapEffect.Discard,
            };
            Device.CreateWithSwapChain(DriverType.Hardware, flags, new[] { FeatureLevel.Level_11_1 }, _swapDesc, out _d3dDevice, out _swapChain);
            _d3dctx = _d3dDevice.ImmediateContext;

            using var _factory = _swapChain.GetParent<Factory>();
            _factory.MakeWindowAssociation(_form.Handle, WindowAssociationFlags.IgnoreAll);

            using var multithread = _d3dDevice.QueryInterface<Multithread>();
            multithread.SetMultithreadProtected(true);

            using (var bytecode = ShaderBytecode.Compile(GetShaderString("basic"), "VSMain", "vs_4_0", ShaderFlags.None, EffectFlags.None, null, null))
                _basicVS = new VertexShader(_d3dDevice, bytecode);

            using (var bytecode = ShaderBytecode.Compile(GetShaderString("basic"), "PSMain", "ps_4_0", ShaderFlags.None, EffectFlags.None, new[] { new ShaderMacro("COPYMODE", "1") }, null))
                _basicPS = new PixelShader(_d3dDevice, bytecode);

            using var _backBuffer = _swapChain.GetBackBuffer<Texture2D>(0);
            _swapchainRenderTarget = new RenderTargetView(_d3dDevice, _backBuffer);

            _samplerState = new SamplerState(_d3dDevice, new SamplerStateDescription()
            {
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                Filter = Filter.MinMagMipLinear,
            });
        }
        /// <summary>
        /// Initializes LibVLC
        /// </summary>
        unsafe static void InitializeLibVLC()
        {
            _libvlc = new LibVLC(enableDebugLogs: true);
            _libvlc.Log += (s, e) => Debug.WriteLine(e.FormattedLog);
            _mediaplayer = new MediaPlayer(_libvlc);
            _mediaplayer.SetOutputCallbacks(VideoEngine.D3D11, OutputSetup, OutputCleanup, SetWindow, UpdateOutput, Swap, StartRendering, null, FrameMetadata, SelectPlane);
        }

        static unsafe void FrameMetadata(nint opaque, FrameMetadataType type, void* metadata)
        {
            var nmt = true;
        }
        /// <summary>
        /// Reads embedded resource shader strings from the project's Shaders folder
        /// </summary>
        /// <param name="shader"></param>
        /// <param name="useDefaultExtension"></param>
        /// <returns></returns>
        static string? GetShaderString(string shader, bool useDefaultExtension = true)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceNames = assembly.GetManifestResourceNames();
            var resourceName = $".Shaders.{shader}{(useDefaultExtension ? ".hlsl" : "")}";
            resourceName = resourceNames.Where(o => o.EndsWith(resourceName)).FirstOrDefault();
            if (string.IsNullOrEmpty(resourceName)) return null;
            using var reader = new StreamReader(assembly.GetManifestResourceStream(resourceName)!);
            return reader.ReadToEnd();
        }
        /// <summary>
        /// Called when the form is closing
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        static void Form_FormClosing(object sender, FormClosingEventArgs e)
        {
            _mediaplayer?.Stop();
            _mediaplayer?.Dispose();
            _mediaplayer = null;
            _libvlc?.Dispose();
            _libvlc = null;
            Cleanup();
            Environment.Exit(0);
        }
        /// <summary>
        /// Called when the form is resizing
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        static void Form_Resize(object sender, EventArgs e)
        {
            _outWidth = _form.ClientRectangle.Width;
            _outHeight = _form.ClientRectangle.Height;

            // resize the backbuffer and then recreate the backbuffer render target
            _swapchainRenderTarget.Dispose();
            _d3dctx.ClearState();
            _swapChain.ResizeBuffers(_swapDesc.BufferCount, _outWidth, _outHeight, Format.Unknown, SwapChainFlags.None);
            using var _backBuffer = _swapChain.GetBackBuffer<Texture2D>(0);
            _swapchainRenderTarget = new RenderTargetView(_d3dDevice, _backBuffer);

            _reportSize?.Invoke(_reportOpaque, (uint)_outWidth, (uint)_outHeight);
        }
        /// <summary>
        /// Cleanup all resources
        /// </summary>
        static void Cleanup()
        {
            ReleaseTextures();
            if (_swapChain != null)
            {
                _swapChain.Dispose();
                _swapChain = null;
            }
            if (_swapchainRenderTarget != null)
            {
                _swapchainRenderTarget.Dispose();
                _swapchainRenderTarget = null;
            }
            // Release VLC context and device
            if (_d3dctxVLC != null)
            {
                _d3dctxVLC.Dispose(); // Extra release for AddRef in OutputSetup
                _d3dctxVLC.Dispose();
                _d3dctxVLC = null;
            }
            if (_d3deviceVLC != null)
            {
                _d3deviceVLC.Dispose();
                _d3deviceVLC = null;
            }
            // Release main context and device
            if (_d3dctx != null)
            {
                _d3dctx.Dispose();
                _d3dctx = null;
            }
            if (_d3dDevice != null)
            {
                _d3dDevice.Dispose();
                _d3dDevice = null;
            }
            // Release other resources
            if (_samplerState != null)
            {
                _samplerState.Dispose();
                _samplerState = null;
            }
            if (_basicVS != null)
            {
                _basicVS.Dispose();
                _basicVS = null;
            }
            if (_basicPS != null)
            {
                _basicPS.Dispose();
                _basicPS = null;
            }
        }
        /// <summary>
        /// Called when VLC needs output context input
        /// </summary>
        /// <param name="opaque"></param>
        /// <param name="config"></param>
        /// <param name="setup"></param>
        /// <returns></returns>
        unsafe static bool OutputSetup(ref IntPtr opaque, SetupDeviceConfig* config, ref SetupDeviceInfo setup)
        {
            setup.D3D11.DeviceContext = _d3dctxVLC.NativePointer.ToPointer();
            return true;
        }
        /// <summary>
        /// Called by VLC when the output resources should be cleaned up
        /// </summary>
        /// <param name="opaque"></param>
        static void OutputCleanup(IntPtr opaque)
        {
            ReleaseTextures();
        }
        /// <summary>
        /// Called by VLC when it needs setup the window
        /// </summary>
        /// <param name="opaque"></param>
        /// <param name="reportSizeChange"></param>
        /// <param name="mousemove"></param>
        /// <param name="mousepress"></param>
        /// <param name="mouserelease"></param>
        /// <param name="reportopaque"></param>
        static void SetWindow(IntPtr opaque, OutputResize reportSizeChange, MouseMove mousemove, MousePress mousepress, MouseRelease mouserelease, IntPtr reportopaque)
        {
            _reportOpaque = reportopaque;
            _reportSize = reportSizeChange;
            _mouseMove = mousemove;
            _mousePress = mousepress;
            _mouseRelease = mouserelease;
            _reportSize?.Invoke(_reportOpaque, (uint)_outWidth, (uint)_outHeight);
        }
        /// <summary>
        /// VLC calls this when the output size or format changes
        /// </summary>
        /// <param name="opaque"></param>
        /// <param name="config"></param>
        /// <param name="output"></param>
        /// <returns></returns>
        unsafe static bool UpdateOutput(IntPtr opaque, RenderConfig* config, ref OutputConfig output)
        {
            // get frame size
            var width = (int)config->Width;
            var height = (int)config->Height;
            if (width == 0) width = 8;
            if (height == 0) height = 8;
            // (re)create the video frame texture if it does not exist or is a different size than needed
            if (_textureWidth != width || _textureHeight != height || _texture == null)
            {
                ReleaseTextures();


                _textureWidth = width;
                _textureHeight = height;

                //  ************* Create a shareable texture *************
                _texture = new Texture2D(_d3dDevice, new Texture2DDescription()
                {
                    MipLevels = 1,
                    SampleDescription = new SampleDescription(1, 0),
                    BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                    Usage = ResourceUsage.Default,
                    CpuAccessFlags = CpuAccessFlags.None,
                    ArraySize = 1,
                    Format = _renderFormat,
                    Width = _textureWidth,
                    Height = _textureHeight,
                    OptionFlags = ResourceOptionFlags.Shared | ResourceOptionFlags.SharedNthandle,
                });

                // get texture shaderResourceView so we can use it in our shaders
                _textureShaderResource = new ShaderResourceView(_d3dDevice, _texture, new ShaderResourceViewDescription
                {
                    Dimension = ShaderResourceViewDimension.Texture2D,
                    Format = _renderFormat,
                    Texture2D = { MipLevels = 1, MostDetailedMip = 0 }
                });

                // get texture shared handle
                using var sharedResource = _texture.QueryInterface<Resource1>();
                _textureSharedHandle = sharedResource.CreateSharedHandle(null, SharedResourceFlags.Read | SharedResourceFlags.Write);

                // get the shared texture using the device created for VLC
                using var d3d11VLC1 = _d3deviceVLC.QueryInterface<Device1>();
                _textureVLC = d3d11VLC1.OpenSharedResource1<Texture2D>(_textureSharedHandle);

                // set VLC's copy of the shared texture to the current render target on the vlc device context
                _textureVLCRenderTarget = new RenderTargetView(_d3deviceVLC, _textureVLC, new RenderTargetViewDescription
                {
                    Format = _renderFormat,
                    Dimension = RenderTargetViewDimension.Texture2D,
                });
                _d3dctxVLC.OutputMerger.SetRenderTargets(null, _textureVLCRenderTarget);
                _d3dctxVLC.Rasterizer.SetViewport(0, 0, _textureWidth, _textureHeight);
            }

            // set the output config for VLC knows what to do
            output.Union.DxgiFormat = (int)_renderFormat;
            output.FullRange = true;
            output.ColorSpace = ColorSpace.BT709;
            output.ColorPrimaries = ColorPrimaries.BT709;
            output.TransferFunction = TransferFunction.SRGB;


            // ISSUE #1 (likely related to issue #2)
            // output.Orientation >> DOES NOT SEEM TO WORK AS EXPECTED >> Does not reliably affect output orientation, and causes unexpected results
            // Setting to TopLeft (used in linked example code) = blank output
            // Setting to Anything else = output shows... but sometimes horizontally flipped content, sometimes yellowish, and resizing the form can cause a horizontal flip
            output.Orientation = VideoOrientation.BottomRight;


            // ISSUE #2
            // No matter the settings used here,
            // An exception is thrown after this method returns (usually a couple times, and again if resized)
            // "Microsoft Visual C++ Runtime Library" 
            // Assertion failed!
            // Program: ...
            // File: /builds/videolan/vlc/extras/package/w.../es_format.c
            // Line: 105
            // Expression: !"unreachable"
            // https://code.videolan.org/videolan/vlc/-/blob/e19dc449339e740a579d44e81ceed72ba56914e5/src/misc/es_format.c

            // the code in es_format.c is using the orientation information (indirectly) from here when it throws the exception
            return true;
        }
        /// <summary>
        /// Disposes resources used for the VLC video frame
        /// </summary>
        static void ReleaseTextures()
        {
            if (_textureSharedHandle != IntPtr.Zero)
            {
                CloseHandle(_textureSharedHandle);
                _textureSharedHandle = IntPtr.Zero;
            }
            if (_textureVLC != null)
            {
                _textureVLC.Dispose();
                _textureVLC = null;
            }
            if (_textureShaderResource != null)
            {
                _textureShaderResource.Dispose();
                _textureShaderResource = null;
            }
            if (_textureVLCRenderTarget != null)
            {
                _textureVLCRenderTarget.Dispose();
                _textureVLCRenderTarget = null;
            }
            if (_texture != null)
            {
                _texture.Dispose();
                _texture = null;
            }
        }
        /// <summary>
        /// VLC calls this when the frame should be shown
        /// </summary>
        /// <param name="opaque"></param>
        static void Swap(IntPtr opaque)
        {
            _swapChain.Present(0, PresentFlags.None);
        }
        /// <summary>
        /// VLC calls this before and after it renders a frame to the shared texture.
        /// </summary>
        /// <param name="opaque"></param>
        /// <param name="enter">
        /// If true, VLC is about to start drawing the frame. Good time to clear the frame target.<br/>
        /// If false, VLC has finished drawing the frame and it is ready to use.
        /// </param>
        /// <returns></returns>
        static bool StartRendering(IntPtr opaque, bool enter)
        {
            if (enter)
            {
                _d3dctxVLC.ClearRenderTargetView(_textureVLCRenderTarget, _colorBlack);
            }
            else
            {
                _d3dctx.ClearRenderTargetView(_swapchainRenderTarget, _colorBlack);
                _d3dctx.OutputMerger.SetRenderTargets(null, _swapchainRenderTarget);
                _d3dctx.Rasterizer.SetViewport(0, 0, _outWidth, _outHeight);
                _d3dctx.InputAssembler.InputLayout = null;
                _d3dctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
                _d3dctx.VertexShader.Set(_basicVS);
                _d3dctx.PixelShader.Set(_basicPS);
                _d3dctx.PixelShader.SetShaderResource(0, _textureShaderResource);
                _d3dctx.PixelShader.SetSampler(0, _samplerState);
                _d3dctx.Draw(4, 0);
            }
            return true;
        }
        /// <summary>
        /// Called by VLC to set a specific render target
        /// </summary>
        /// <param name="opaque"></param>
        /// <param name="plane"></param>
        /// <param name="output"></param>
        /// <returns></returns>
        unsafe static bool SelectPlane(IntPtr opaque, UIntPtr plane, void* output)
        {
            return (ulong)plane == 0;
        }
    }
}