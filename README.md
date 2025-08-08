# LibVLCSharp4SharpDXTest

Test app using LibVLCSharp 4 pre-release and SharpDX to test the new DirectX 11 rendering callbacks in LibVLCSharp 4. 
Based primarily on the first reference linked below, but this project uses the DirectX interop library [SharpDX](https://github.com/sharpdx/SharpDX) instead of [TerraFX](https://github.com/terrafx/terrafx.interop.windows).

### References 
- [LibVLCSharp.CustomRendering.Direct3D11](https://code.videolan.org/videolan/LibVLCSharp/-/blob/master/samples/LibVLCSharp.CustomRendering.Direct3D11/Program.cs)
- [D3D11 Player C++](https://code.videolan.org/videolan/vlc/-/blob/e19dc449339e740a579d44e81ceed72ba56914e5/doc/libvlc/d3d11_player.cpp)


### Current issues 
Currently trying to figure out 2 issues. See the inline comments at the end of the method for more information.
```cs
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
            Format = renderFormat,
            Width = _textureWidth,
            Height = _textureHeight,
            OptionFlags = ResourceOptionFlags.Shared | ResourceOptionFlags.SharedNthandle,
        });

        // get texture shaderResourceView so we can use it in our shaders
        _textureShaderResource = new ShaderResourceView(_d3dDevice, _texture, new ShaderResourceViewDescription
        {
            Dimension = ShaderResourceViewDimension.Texture2D,
            Format = renderFormat,
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
            Format = renderFormat,
            Dimension = RenderTargetViewDimension.Texture2D,
        });
        _d3dctxVLC.OutputMerger.SetRenderTargets(null, _textureVLCRenderTarget);
        _d3dctxVLC.Rasterizer.SetViewport(0, 0, _textureWidth, _textureHeight);
    }

    // set the output config for VLC knows what to do
    output.Union.DxgiFormat = (int)renderFormat;
    output.FullRange = true;
    output.ColorSpace = ColorSpace.BT709;
    output.ColorPrimaries = ColorPrimaries.BT709;
    output.TransferFunction = TransferFunction.SRGB;


    // ISSUE #1 (likely related to issue #2)
    // output.Orientation >> DOES NOT SEEM TO WORK AS EXPECTED >> Does not reliably affect output orientation, 
    // and causes unexpected results
    // Setting to TopLeft (used in linked example code) = blank output
    // Setting to Anything else = output shows... but sometimes horizontally flipped content, sometimes yellowish, 
    //  and resizing the form can cause a horizontal flip
    output.Orientation = VideoOrientation.TopLeft;


    // ISSUE #2
    // No matter the settings used here, an exception is thrown after this method returns (usually a couple times, 
    // and again if resized)
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
```