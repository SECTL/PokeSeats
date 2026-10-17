using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using ImGuiNET;
using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace PokeSeats.UI.Rendering;

/// <summary>Owns the Direct3D 11 device, swap chain and the ImGui draw pipeline.</summary>
internal unsafe sealed class Direct3D11Graphics : IDisposable
{
    private const uint DxgiUsageRenderTargetOutput = 0x20;
    private const int VertexBufferSlack = 5000;
    private const int IndexBufferSlack = 10000;

    private static readonly IntPtr ResetRenderStateCallback = new(-8);
    private static readonly byte[] ShaderFileName = Encoding.ASCII.GetBytes("PokeSeats.UI.Rendering.Direct3D11Graphics\0");
    private static readonly byte[] PositionSemantic = Encoding.ASCII.GetBytes("POSITION\0");
    private static readonly byte[] TexCoordSemantic = Encoding.ASCII.GetBytes("TEXCOORD\0");
    private static readonly byte[] ColorSemantic = Encoding.ASCII.GetBytes("COLOR\0");

    private const string ShaderSource = @"
cbuffer VertexBuffer : register(b0)
{
    float4x4 ProjectionMatrix;
};

struct VS_INPUT
{
    float2 Position : POSITION;
    float2 Uv : TEXCOORD0;
    float4 Color : COLOR0;
};

struct PS_INPUT
{
    float4 Position : SV_POSITION;
    float4 Color : COLOR0;
    float2 Uv : TEXCOORD0;
};

Texture2D FontTexture : register(t0);
SamplerState FontSampler : register(s0);

PS_INPUT VSMain(VS_INPUT input)
{
    PS_INPUT output;
    output.Position = mul(ProjectionMatrix, float4(input.Position, 0.0f, 1.0f));
    output.Color = input.Color;
    output.Uv = input.Uv;
    return output;
}

float4 PSMain(PS_INPUT input) : SV_Target
{
    return input.Color * FontTexture.Sample(FontSampler, input.Uv);
}
";

    private readonly D3D11 _d3d11;

    private ComPtr<ID3D11Device> _device;
    private ComPtr<ID3D11DeviceContext> _context;
    private ComPtr<IDXGISwapChain> _swapChain;

    private ID3D11RenderTargetView* _renderTargetView;
    private ID3D11Buffer* _vertexBuffer;
    private ID3D11Buffer* _indexBuffer;
    private ID3D11Buffer* _constantBuffer;
    private ID3D11VertexShader* _vertexShader;
    private ID3D11PixelShader* _pixelShader;
    private ID3D11InputLayout* _inputLayout;
    private ID3D11SamplerState* _samplerState;
    private ID3D11RasterizerState* _rasterizerState;
    private ID3D11BlendState* _blendState;
    private ID3D11DepthStencilState* _depthStencilState;
    private ID3D11Texture2D* _fontTexture;
    private ID3D11ShaderResourceView* _fontShaderResourceView;

    private int _framebufferWidth;
    private int _framebufferHeight;
    private int _vertexCapacity;
    private int _indexCapacity;
    private bool _disposed;

    internal Direct3D11Graphics(IWindow window)
    {
        nint windowHandle = GetWindowHandle(window);
        _d3d11 = D3D11.GetApi(window);

        try
        {
            CreateDeviceAndSwapChain(window, windowHandle);
            CreatePipeline();
            UpdateFontTexture();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void Render(ImDrawDataPtr drawData)
    {
        ThrowIfDisposed();
        if (drawData.NativePtr == null || _renderTargetView == null)
            return;

        Vector2 displaySize = drawData.DisplaySize;
        if (displaySize.X <= 0f || displaySize.Y <= 0f)
            return;

        Vector2 framebufferScale = drawData.FramebufferScale;
        float scaleX = framebufferScale.X > 0f ? framebufferScale.X : 1f;
        float scaleY = framebufferScale.Y > 0f ? framebufferScale.Y : 1f;
        float viewportWidth = displaySize.X * scaleX;
        float viewportHeight = displaySize.Y * scaleY;

        _context.OMSetRenderTargets(1, ref _renderTargetView, (ID3D11DepthStencilView*)null);
        Span<float> clearColor = stackalloc float[4] { 0.08f, 0.08f, 0.08f, 1f };
        _context.ClearRenderTargetView(_renderTargetView, clearColor);

        if (drawData.TotalVtxCount > 0 && drawData.TotalIdxCount > 0)
        {
            EnsureBuffers(drawData.TotalVtxCount, drawData.TotalIdxCount);
            UploadDrawData(drawData);
        }

        UpdateProjection(drawData);
        SetupRenderState(viewportWidth, viewportHeight);

        Vector2 displayPosition = drawData.DisplayPos;
        ImVector<ImDrawListPtr> commandLists = drawData.CmdLists;
        uint globalIndexOffset = 0;
        uint globalVertexOffset = 0;

        for (int listIndex = 0; listIndex < commandLists.Size; listIndex++)
        {
            ImDrawListPtr drawList = commandLists[listIndex];
            ImPtrVector<ImDrawCmdPtr> commands = drawList.CmdBuffer;
            int listIndexCount = drawList.IdxBuffer.Size;
            int listVertexCount = drawList.VtxBuffer.Size;

            for (int commandIndex = 0; commandIndex < commands.Size; commandIndex++)
            {
                ImDrawCmdPtr command = commands[commandIndex];
                IntPtr userCallback = command.UserCallback;
                if (userCallback != IntPtr.Zero)
                {
                    if (userCallback == ResetRenderStateCallback)
                        SetupRenderState(viewportWidth, viewportHeight);
                    else
                        InvokeUserCallback(userCallback, drawList.NativePtr, command.NativePtr);
                    continue;
                }

                if (command.ElemCount == 0)
                    continue;

                float clipMinX = (command.ClipRect.X - displayPosition.X) * scaleX;
                float clipMinY = (command.ClipRect.Y - displayPosition.Y) * scaleY;
                float clipMaxX = (command.ClipRect.Z - displayPosition.X) * scaleX;
                float clipMaxY = (command.ClipRect.W - displayPosition.Y) * scaleY;
                if (clipMaxX <= clipMinX || clipMaxY <= clipMinY)
                    continue;

                int left = Math.Clamp((int)clipMinX, 0, _framebufferWidth);
                int top = Math.Clamp((int)clipMinY, 0, _framebufferHeight);
                int right = Math.Clamp((int)clipMaxX, 0, _framebufferWidth);
                int bottom = Math.Clamp((int)clipMaxY, 0, _framebufferHeight);
                if (right <= left || bottom <= top)
                    continue;

                Box2D<int> scissor = new(left, top, right, bottom);
                _context.RSSetScissorRects(1, new ReadOnlySpan<Box2D<int>>(&scissor, 1));

                ID3D11ShaderResourceView* texture = (ID3D11ShaderResourceView*)(void*)command.TextureId;
                if (texture == null)
                    texture = _fontShaderResourceView;
                _context.PSSetShaderResources(0, 1, ref texture);

                _context.DrawIndexed(
                    command.ElemCount,
                    command.IdxOffset + globalIndexOffset,
                    (int)(command.VtxOffset + globalVertexOffset));
            }

            globalIndexOffset += (uint)listIndexCount;
            globalVertexOffset += (uint)listVertexCount;
        }

        ThrowIfFailed(_swapChain.Present(1, 0), "IDXGISwapChain.Present");
    }

    internal void Resize(Vector2D<int> size)
    {
        ThrowIfDisposed();
        if (size.X <= 0 || size.Y <= 0)
            return;
        if (_renderTargetView != null && size.X == _framebufferWidth && size.Y == _framebufferHeight)
            return;

        _context.OMSetRenderTargets(0, (ID3D11RenderTargetView**)null, (ID3D11DepthStencilView*)null);
        Release(ref _renderTargetView);
        _context.Flush();
        ThrowIfFailed(
            _swapChain.ResizeBuffers(0, (uint)size.X, (uint)size.Y, Format.FormatUnknown, 0),
            "IDXGISwapChain.ResizeBuffers");
        CreateRenderTarget(size.X, size.Y);
    }

    internal void UpdateFontTexture()
    {
        ThrowIfDisposed();

        ImFontAtlasPtr atlas = ImGui.GetIO().Fonts;
        atlas.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height);
        if (pixels == IntPtr.Zero || width <= 0 || height <= 0)
            throw new InvalidOperationException("The ImGui font atlas did not produce RGBA32 pixel data.");

        Texture2DDesc textureDesc = new()
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.FormatR8G8B8A8Unorm,
            SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
            Usage = Usage.Default,
            BindFlags = (uint)BindFlag.ShaderResource,
            CPUAccessFlags = 0,
            MiscFlags = 0
        };
        SubresourceData initialData = new()
        {
            PSysMem = (void*)pixels,
            SysMemPitch = (uint)width * 4u,
            SysMemSlicePitch = 0
        };

        ID3D11Texture2D* texture = null;
        ID3D11ShaderResourceView* shaderResourceView = null;
        try
        {
            ThrowIfFailed(_device.CreateTexture2D(&textureDesc, &initialData, ref texture), "CreateTexture2D(font atlas)");
            ThrowIfFailed(
                _device.CreateShaderResourceView(
                    (ID3D11Resource*)texture,
                    (ShaderResourceViewDesc*)null,
                    ref shaderResourceView),
                "CreateShaderResourceView(font atlas)");
        }
        catch
        {
            Release(ref shaderResourceView);
            Release(ref texture);
            throw;
        }

        Release(ref _fontShaderResourceView);
        Release(ref _fontTexture);
        _fontTexture = texture;
        _fontShaderResourceView = shaderResourceView;

        atlas.SetTexID((IntPtr)shaderResourceView);
        atlas.ClearTexData();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        Release(ref _fontShaderResourceView);
        Release(ref _fontTexture);
        Release(ref _constantBuffer);
        Release(ref _vertexBuffer);
        Release(ref _indexBuffer);
        Release(ref _inputLayout);
        Release(ref _vertexShader);
        Release(ref _pixelShader);
        Release(ref _samplerState);
        Release(ref _rasterizerState);
        Release(ref _blendState);
        Release(ref _depthStencilState);
        Release(ref _renderTargetView);
        Release(ref _context);
        Release(ref _swapChain);
        Release(ref _device);
        _d3d11.Dispose();
    }

    private static nint GetWindowHandle(IWindow window)
    {
        INativeWindow native = window.Native
            ?? throw new PlatformNotSupportedException("The window has no native handle yet.");

        nint windowHandle = native.DXHandle.GetValueOrDefault();
        if (windowHandle == 0 && native.Win32 is { } win32)
            windowHandle = win32.Hwnd;
        if (windowHandle == 0)
            throw new PlatformNotSupportedException("Direct3D 11 requires a Win32 window handle.");
        return windowHandle;
    }

    private void CreateDeviceAndSwapChain(IWindow window, nint windowHandle)
    {
        Vector2D<int> framebufferSize = window.FramebufferSize;
        int width = Math.Max(1, framebufferSize.X);
        int height = Math.Max(1, framebufferSize.Y);

        SwapChainDesc swapChainDesc = default;
        swapChainDesc.BufferDesc.Width = (uint)width;
        swapChainDesc.BufferDesc.Height = (uint)height;
        swapChainDesc.BufferDesc.RefreshRate = new Rational { Numerator = 0, Denominator = 1 };
        swapChainDesc.BufferDesc.Format = Format.FormatR8G8B8A8Unorm;
        swapChainDesc.BufferDesc.ScanlineOrdering = ModeScanlineOrder.Unspecified;
        swapChainDesc.BufferDesc.Scaling = ModeScaling.Unspecified;
        swapChainDesc.SampleDesc = new SampleDesc { Count = 1, Quality = 0 };
        swapChainDesc.BufferUsage = DxgiUsageRenderTargetOutput;
        swapChainDesc.BufferCount = 2;
        swapChainDesc.OutputWindow = windowHandle;
        swapChainDesc.Windowed = true;
        swapChainDesc.SwapEffect = SwapEffect.Discard;
        swapChainDesc.Flags = 0;

        int result = _d3d11.CreateDeviceAndSwapChain<IDXGIAdapter, IDXGISwapChain, ID3D11Device, ID3D11DeviceContext>(
            default(ComPtr<IDXGIAdapter>),
            D3DDriverType.Hardware,
            IntPtr.Zero,
            (uint)CreateDeviceFlag.BgraSupport,
            (D3DFeatureLevel*)null,
            0,
            (uint)D3D11.SdkVersion,
            &swapChainDesc,
            ref _swapChain,
            ref _device,
            (D3DFeatureLevel*)null,
            ref _context);
        ThrowIfFailed(result, "D3D11CreateDeviceAndSwapChain");

        CreateRenderTarget(width, height);
    }

    private void CreateRenderTarget(int width, int height)
    {
        Release(ref _renderTargetView);

        Guid textureGuid = typeof(ID3D11Texture2D).GUID;
        void* backBuffer = null;
        ThrowIfFailed(_swapChain.GetBuffer(0, &textureGuid, &backBuffer), "IDXGISwapChain.GetBuffer");
        try
        {
            ThrowIfFailed(
                _device.CreateRenderTargetView(
                    (ID3D11Resource*)backBuffer,
                    (RenderTargetViewDesc*)null,
                    ref _renderTargetView),
                "CreateRenderTargetView");
        }
        finally
        {
            ((ID3D11Texture2D*)backBuffer)->Release();
        }

        _framebufferWidth = width;
        _framebufferHeight = height;
    }

    private void CreatePipeline()
    {
        BufferDesc constantBufferDesc = new()
        {
            ByteWidth = (uint)sizeof(Matrix4x4),
            Usage = Usage.Dynamic,
            BindFlags = (uint)BindFlag.ConstantBuffer,
            CPUAccessFlags = (uint)CpuAccessFlag.Write,
            MiscFlags = 0,
            StructureByteStride = 0
        };
        ThrowIfFailed(
            _device.CreateBuffer(ref constantBufferDesc, (SubresourceData*)null, ref _constantBuffer),
            "CreateBuffer(constant buffer)");

        CreatePipelineStates();

        using D3DCompiler compiler = D3DCompiler.GetApi();
        ID3D10Blob* vertexBlob = null;
        ID3D10Blob* pixelBlob = null;
        try
        {
            vertexBlob = CompileShader(compiler, "VSMain", "vs_4_0");
            pixelBlob = CompileShader(compiler, "PSMain", "ps_4_0");

            ThrowIfFailed(
                _device.CreateVertexShader(
                    vertexBlob->GetBufferPointer(),
                    vertexBlob->GetBufferSize(),
                    (ID3D11ClassLinkage*)null,
                    ref _vertexShader),
                "CreateVertexShader");

            CreateInputLayout(vertexBlob);
            ThrowIfFailed(
                _device.CreatePixelShader(
                    pixelBlob->GetBufferPointer(),
                    pixelBlob->GetBufferSize(),
                    (ID3D11ClassLinkage*)null,
                    ref _pixelShader),
                "CreatePixelShader");
        }
        finally
        {
            Release(ref vertexBlob);
            Release(ref pixelBlob);
        }
    }

    private void CreateInputLayout(ID3D10Blob* vertexBlob)
    {
        // ImDrawVert is pos (float2), uv (float2), col (packed RGBA); the offsets below match that layout.
        if (sizeof(ImDrawVert) != 20)
            throw new InvalidOperationException($"Unexpected ImDrawVert layout (size {sizeof(ImDrawVert)}).");

        fixed (byte* positionName = PositionSemantic)
        fixed (byte* texCoordName = TexCoordSemantic)
        fixed (byte* colorName = ColorSemantic)
        {
            InputElementDesc* elements = stackalloc InputElementDesc[3];
            elements[0] = new InputElementDesc
            {
                SemanticName = positionName,
                SemanticIndex = 0,
                Format = Format.FormatR32G32Float,
                InputSlot = 0,
                AlignedByteOffset = 0,
                InputSlotClass = InputClassification.PerVertexData,
                InstanceDataStepRate = 0
            };
            elements[1] = new InputElementDesc
            {
                SemanticName = texCoordName,
                SemanticIndex = 0,
                Format = Format.FormatR32G32Float,
                InputSlot = 0,
                AlignedByteOffset = 8,
                InputSlotClass = InputClassification.PerVertexData,
                InstanceDataStepRate = 0
            };
            elements[2] = new InputElementDesc
            {
                SemanticName = colorName,
                SemanticIndex = 0,
                Format = Format.FormatR8G8B8A8Unorm,
                InputSlot = 0,
                AlignedByteOffset = 16,
                InputSlotClass = InputClassification.PerVertexData,
                InstanceDataStepRate = 0
            };

            ThrowIfFailed(
                _device.CreateInputLayout(
                    elements,
                    3,
                    vertexBlob->GetBufferPointer(),
                    vertexBlob->GetBufferSize(),
                    ref _inputLayout),
                "CreateInputLayout");
        }
    }

    private void CreatePipelineStates()
    {
        SamplerDesc samplerDesc = new()
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MipLODBias = 0f,
            MaxAnisotropy = 1,
            ComparisonFunc = ComparisonFunc.Always,
            MinLOD = 0f,
            MaxLOD = float.MaxValue
        };
        ThrowIfFailed(_device.CreateSamplerState(ref samplerDesc, ref _samplerState), "CreateSamplerState");

        RasterizerDesc rasterizerDesc = new()
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            FrontCounterClockwise = false,
            DepthBias = 0,
            DepthBiasClamp = 0f,
            SlopeScaledDepthBias = 0f,
            DepthClipEnable = true,
            ScissorEnable = true,
            MultisampleEnable = false,
            AntialiasedLineEnable = false
        };
        ThrowIfFailed(_device.CreateRasterizerState(ref rasterizerDesc, ref _rasterizerState), "CreateRasterizerState");

        BlendDesc blendDesc = default;
        blendDesc.AlphaToCoverageEnable = false;
        blendDesc.IndependentBlendEnable = false;
        ref RenderTargetBlendDesc blendTarget = ref blendDesc.RenderTarget[0];
        blendTarget.BlendEnable = true;
        blendTarget.SrcBlend = Blend.SrcAlpha;
        blendTarget.DestBlend = Blend.InvSrcAlpha;
        blendTarget.BlendOp = BlendOp.Add;
        blendTarget.SrcBlendAlpha = Blend.One;
        blendTarget.DestBlendAlpha = Blend.InvSrcAlpha;
        blendTarget.BlendOpAlpha = BlendOp.Add;
        blendTarget.RenderTargetWriteMask = (byte)ColorWriteEnable.All;
        ThrowIfFailed(_device.CreateBlendState(ref blendDesc, ref _blendState), "CreateBlendState");

        DepthStencilopDesc stencilOperation = new()
        {
            StencilFailOp = StencilOp.Keep,
            StencilDepthFailOp = StencilOp.Keep,
            StencilPassOp = StencilOp.Keep,
            StencilFunc = ComparisonFunc.Always
        };
        DepthStencilDesc depthStencilDesc = new()
        {
            DepthEnable = false,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthFunc = ComparisonFunc.Always,
            StencilEnable = false,
            StencilReadMask = 0,
            StencilWriteMask = 0,
            FrontFace = stencilOperation,
            BackFace = stencilOperation
        };
        ThrowIfFailed(
            _device.CreateDepthStencilState(ref depthStencilDesc, ref _depthStencilState),
            "CreateDepthStencilState");
    }

    private static ID3D10Blob* CompileShader(D3DCompiler compiler, string entryPoint, string target)
    {
        byte[] sourceBytes = Encoding.UTF8.GetBytes(ShaderSource);
        byte[] entryPointBytes = Encoding.ASCII.GetBytes(entryPoint + "\0");

        ID3D10Blob* code = null;
        ID3D10Blob* errors = null;
        fixed (byte* source = sourceBytes)
        fixed (byte* sourceName = ShaderFileName)
        fixed (byte* entry = entryPointBytes)
        {
            int result = compiler.Compile(
                source,
                (nuint)sourceBytes.Length,
                sourceName,
                (D3DShaderMacro*)null,
                (ID3DInclude*)null,
                entry,
                target,
                0,
                0,
                &code,
                &errors);

            if (result < 0)
            {
                string diagnostics = errors == null ? string.Empty : GetBlobText(errors);
                Release(ref errors);
                Release(ref code);
                throw new InvalidOperationException(
                    $"Failed to compile the ImGui shader '{entryPoint}' (HRESULT 0x{unchecked((uint)result):X8}). {diagnostics}".TrimEnd());
            }
        }

        Release(ref errors);
        return code;
    }

    private static string GetBlobText(ID3D10Blob* blob)
    {
        nuint length = blob->GetBufferSize();
        if (length == 0)
            return string.Empty;
        return Marshal.PtrToStringAnsi((IntPtr)blob->GetBufferPointer(), (int)(ulong)length) ?? string.Empty;
    }

    private void EnsureBuffers(int vertexCount, int indexCount)
    {
        if (_vertexBuffer == null || vertexCount > _vertexCapacity)
        {
            Release(ref _vertexBuffer);
            _vertexCapacity = checked(vertexCount + VertexBufferSlack);
            BufferDesc vertexBufferDesc = new()
            {
                ByteWidth = (uint)_vertexCapacity * (uint)sizeof(ImDrawVert),
                Usage = Usage.Dynamic,
                BindFlags = (uint)BindFlag.VertexBuffer,
                CPUAccessFlags = (uint)CpuAccessFlag.Write,
                MiscFlags = 0,
                StructureByteStride = 0
            };
            ThrowIfFailed(
                _device.CreateBuffer(&vertexBufferDesc, (SubresourceData*)null, ref _vertexBuffer),
                "CreateBuffer(vertex buffer)");
        }

        if (_indexBuffer == null || indexCount > _indexCapacity)
        {
            Release(ref _indexBuffer);
            _indexCapacity = checked(indexCount + IndexBufferSlack);
            BufferDesc indexBufferDesc = new()
            {
                ByteWidth = (uint)_indexCapacity * (uint)sizeof(ushort),
                Usage = Usage.Dynamic,
                BindFlags = (uint)BindFlag.IndexBuffer,
                CPUAccessFlags = (uint)CpuAccessFlag.Write,
                MiscFlags = 0,
                StructureByteStride = 0
            };
            ThrowIfFailed(
                _device.CreateBuffer(&indexBufferDesc, (SubresourceData*)null, ref _indexBuffer),
                "CreateBuffer(index buffer)");
        }
    }

    private void UploadDrawData(ImDrawDataPtr drawData)
    {
        ImVector<ImDrawListPtr> commandLists = drawData.CmdLists;

        MappedSubresource mappedVertices = default;
        ThrowIfFailed(
            _context.Map((ID3D11Resource*)_vertexBuffer, 0, Map.WriteDiscard, 0, ref mappedVertices),
            "Map(vertex buffer)");
        try
        {
            byte* destination = (byte*)mappedVertices.PData;
            long remaining = (long)_vertexCapacity * sizeof(ImDrawVert);
            for (int index = 0; index < commandLists.Size; index++)
            {
                ImPtrVector<ImDrawVertPtr> vertices = commandLists[index].VtxBuffer;
                long bytes = (long)vertices.Size * sizeof(ImDrawVert);
                if (bytes == 0)
                    continue;
                if (bytes > remaining)
                    throw new InvalidOperationException("ImGui vertex data exceeded the allocated vertex buffer.");
                System.Buffer.MemoryCopy((void*)vertices.Data, destination, remaining, bytes);
                destination += bytes;
                remaining -= bytes;
            }
        }
        finally
        {
            _context.Unmap((ID3D11Resource*)_vertexBuffer, 0);
        }

        MappedSubresource mappedIndices = default;
        ThrowIfFailed(
            _context.Map((ID3D11Resource*)_indexBuffer, 0, Map.WriteDiscard, 0, ref mappedIndices),
            "Map(index buffer)");
        try
        {
            byte* destination = (byte*)mappedIndices.PData;
            long remaining = (long)_indexCapacity * sizeof(ushort);
            for (int index = 0; index < commandLists.Size; index++)
            {
                ImVector<ushort> indices = commandLists[index].IdxBuffer;
                long bytes = (long)indices.Size * sizeof(ushort);
                if (bytes == 0)
                    continue;
                if (bytes > remaining)
                    throw new InvalidOperationException("ImGui index data exceeded the allocated index buffer.");
                System.Buffer.MemoryCopy((void*)indices.Data, destination, remaining, bytes);
                destination += bytes;
                remaining -= bytes;
            }
        }
        finally
        {
            _context.Unmap((ID3D11Resource*)_indexBuffer, 0);
        }
    }

    private void UpdateProjection(ImDrawDataPtr drawData)
    {
        Vector2 displayPosition = drawData.DisplayPos;
        Vector2 displaySize = drawData.DisplaySize;

        // Vertices stay in logical coordinates; viewport and scissors use framebuffer pixels.
        float left = displayPosition.X;
        float right = displayPosition.X + displaySize.X;
        float top = displayPosition.Y;
        float bottom = displayPosition.Y + displaySize.Y;

        Matrix4x4 projection = new(
            2f / (right - left), 0f, 0f, 0f,
            0f, 2f / (top - bottom), 0f, 0f,
            0f, 0f, 0.5f, 0f,
            (right + left) / (left - right), (top + bottom) / (bottom - top), 0.5f, 1f);

        MappedSubresource mapped = default;
        ThrowIfFailed(
            _context.Map((ID3D11Resource*)_constantBuffer, 0, Map.WriteDiscard, 0, ref mapped),
            "Map(constant buffer)");
        try
        {
            *(Matrix4x4*)mapped.PData = projection;
        }
        finally
        {
            _context.Unmap((ID3D11Resource*)_constantBuffer, 0);
        }
    }

    private void SetupRenderState(float width, float height)
    {
        Viewport viewport = new()
        {
            TopLeftX = 0f,
            TopLeftY = 0f,
            Width = width,
            Height = height,
            MinDepth = 0f,
            MaxDepth = 1f
        };
        _context.RSSetViewports(1, &viewport);

        _context.IASetInputLayout(_inputLayout);
        uint stride = _vertexBuffer != null ? (uint)sizeof(ImDrawVert) : 0u;
        uint offset = 0u;
        _context.IASetVertexBuffers(0, 1, ref _vertexBuffer, ref stride, ref offset);
        _context.Handle->IASetIndexBuffer(_indexBuffer, Format.FormatR16Uint, 0);
        _context.IASetPrimitiveTopology(D3DPrimitiveTopology.D3D11PrimitiveTopologyTrianglelist);
        _context.VSSetShader(_vertexShader, (ID3D11ClassInstance**)null, 0);
        _context.VSSetConstantBuffers(0, 1, ref _constantBuffer);
        _context.PSSetShader(_pixelShader, (ID3D11ClassInstance**)null, 0);
        _context.PSSetSamplers(0, 1, ref _samplerState);
        _context.OMSetBlendState(_blendState, (float*)null, uint.MaxValue);
        _context.OMSetDepthStencilState(_depthStencilState, 0);
        _context.RSSetState(_rasterizerState);

        Box2D<int> fullFrame = new(0, 0, Math.Max(1, (int)width), Math.Max(1, (int)height));
        _context.RSSetScissorRects(1, new ReadOnlySpan<Box2D<int>>(&fullFrame, 1));
    }

    private static void InvokeUserCallback(IntPtr callback, ImDrawList* drawList, ImDrawCmd* command)
    {
        void* address = (void*)callback;
        delegate* unmanaged[Cdecl]<ImDrawList*, ImDrawCmd*, void> function =
            (delegate* unmanaged[Cdecl]<ImDrawList*, ImDrawCmd*, void>)address;
        function(drawList, command);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(Direct3D11Graphics));
    }

    private static void ThrowIfFailed(int result, string operation)
    {
        if (result >= 0)
            return;
        throw new InvalidOperationException(
            $"{operation} failed with HRESULT 0x{unchecked((uint)result):X8}.");
    }

    private static void Release<T>(ref T* resource)
        where T : unmanaged
    {
        if (resource == null)
            return;
        ((IUnknown*)resource)->Release();
        resource = null;
    }

    private static void Release<T>(ref ComPtr<T> resource)
        where T : unmanaged, IComVtbl<T>
    {
        resource.Dispose();
        resource = default;
    }
}
