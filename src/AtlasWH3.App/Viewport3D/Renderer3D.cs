using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace AtlasWH3.App.Viewport3D;

/// <summary>One mesh on the GPU: 32-byte vertices (position, normal, uv), 16-bit indices, optional colour texture.</summary>
public sealed class GpuMesh : IDisposable
{
    public required ID3D11Buffer Vertices { get; init; }
    public required ID3D11Buffer Indices { get; init; }
    public required int IndexCount { get; init; }
    public GpuTexture? Texture { get; init; }
    public bool AlphaTest { get; init; }
    /// <summary>Share of the terrain height the vertex shader adds to each vertex (the LF-offset mountains).</summary>
    public float TerrainOffset { get; init; }
    /// <summary>Water surface (shaders/campaign_water: lake discs and lake meshes): drawn in a flat water colour, since
    /// its only colour texture (sea_colour.dds) is a lookup, not an albedo.</summary>
    public bool Water { get; init; }

    public void Dispose()
    {
        Vertices.Dispose();
        Indices.Dispose();
    }
}

/// <summary>A colour texture with a mip chain (generated on the GPU the first time it is drawn).</summary>
public sealed class GpuTexture : IDisposable
{
    public required ID3D11Texture2D Texture { get; init; }
    public required ID3D11ShaderResourceView View { get; init; }
    public bool MipsGenerated { get; set; }

    public void Dispose()
    {
        View.Dispose();
        Texture.Dispose();
    }
}

/// <summary>
/// Campaign ground for the terrain shader: the texture-group index per lf pixel (R8_UInt) and the groups' base colour
/// textures as one mipped array, with the world size the raster covers and the texture repeat in world units.
/// </summary>
public sealed class TerrainMaterial : IDisposable
{
    public required ID3D11Texture2D Groups { get; init; }
    public required ID3D11ShaderResourceView GroupsView { get; init; }
    public required ID3D11Texture2D Textures { get; init; }
    public required ID3D11ShaderResourceView TexturesView { get; init; }
    public required Vector4 World { get; init; }      // world w, world h, raster w, raster h
    public float RepeatWorld { get; init; } = 4;
    public bool MipsGenerated { get; set; }

    public void Dispose()
    {
        GroupsView.Dispose();
        Groups.Dispose();
        TexturesView.Dispose();
        Textures.Dispose();
    }
}

/// <summary>Per-instance data: world matrix rows (row-vector convention) and a tint (rgb, strength in a).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct InstanceData
{
    public Vector4 Row0, Row1, Row2, Row3;
    public Vector4 Tint;
}

[StructLayout(LayoutKind.Sequential)]
public struct LineVertex(Vector3 position, Vector4 colour)
{
    public Vector3 Position = position;
    public Vector4 Colour = colour;
}

/// <summary>
/// The Direct3D 11 side of the viewport: device, shaders (instanced lit/textured meshes with alpha test and selection
/// tint, height-tinted terrain, coloured lines and translucent fills), states, the swap chain of the host window, and
/// an offscreen target for screenshots. World space is used as is (x east, y up, z north), which is D3D's
/// left-handed frame; matrices are System.Numerics (row vectors), passed as row_major.
/// </summary>
public sealed class Renderer3D : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FrameConstants
    {
        public Matrix4x4 ViewProj;
        public Vector4 LightDir;
        public Vector4 CameraPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TerrainConstants
    {
        public Vector4 World;     // world w, world h, raster w, raster h
        public Vector4 Texture;   // repeat (world units), group count, textured, 0
        public Vector4 Snow;      // x: snow on, y: strength
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TileConstants
    {
        public Int4 Layers;     // ground group per blend channel (R, G, B, A); -1 = the climate (the ground below)
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Int4 { public int X, Y, Z, W; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DrawConstants
    {
        public Vector4 Flags;   // x: has texture, y: alpha test, z: terrain offset (share of the lf height added per vertex)
        public Vector4 Colour;
        public Vector4 Terrain; // world w, world h, height per raw unit (×65535), height offset
    }

    private const string Hlsl = """
        cbuffer Frame : register(b0) { row_major float4x4 viewProj; float4 lightDir; float4 cameraPos; };
        cbuffer Draw : register(b1) { float4 flags; float4 colour; float4 drawTerrain; };
        Texture2D<float> terrainHeight : register(t8);
        cbuffer Ground : register(b2) { float4 groundWorld; float4 groundTex; float4 groundSnow; };
        Texture2D snowMask : register(t4);
        Texture2D snowColour : register(t5);
        Texture2D tileNormal : register(t6);
        Texture2D regionOverlay : register(t7);
        Texture2D tex : register(t0);
        Texture2D<uint> groundGroups : register(t1);
        Texture2DArray groundColours : register(t2);
        cbuffer Tile : register(b3) { int4 tileLayers; };
        Texture2D tileBlend : register(t3);
        SamplerState samp : register(s0);

        struct MeshIn { float3 pos : POSITION; float3 nrm : NORMAL; float2 uv : TEXCOORD0;
                        float4 r0 : WORLD0; float4 r1 : WORLD1; float4 r2 : WORLD2; float4 r3 : WORLD3; float4 tint : TINT; };
        struct MeshOut { float4 pos : SV_POSITION; float3 nrm : NORMAL; float2 uv : TEXCOORD0; float4 tint : TINT; };
        MeshOut MeshVS(MeshIn i)
        {
            float4x4 w = float4x4(i.r0, i.r1, i.r2, i.r3);
            float4 wp = mul(float4(i.pos, 1), w);
            // LF-offset mountains (rigid_detail_map_terrain_blend_with_LF_offset): + adjust × lf height at this vertex.
            if (flags.z != 0 && drawTerrain.x > 0)
                wp.y += flags.z * (terrainHeight.SampleLevel(samp, float2(wp.x / drawTerrain.x, 1 - wp.z / drawTerrain.y), 0) * drawTerrain.z + drawTerrain.w);
            MeshOut o;
            o.pos = mul(wp, viewProj);
            o.nrm = mul(float4(i.nrm, 0), w).xyz;
            o.uv = i.uv;
            o.tint = i.tint;
            return o;
        }
        float4 MeshPS(MeshOut i) : SV_TARGET
        {
            float4 c = flags.w > 0.5 ? float4(0.13, 0.30, 0.43, 1) : flags.x > 0.5 ? tex.Sample(samp, i.uv) : float4(0.68, 0.68, 0.66, 1);
            if (flags.y > 0.5) clip(c.a - 0.5);
            float3 n = normalize(i.nrm + float3(0, 1e-5, 0));
            float d = abs(dot(n, -lightDir.xyz));
            float3 rgb = c.rgb * (0.45 + 0.6 * d);
            return float4(lerp(rgb, i.tint.rgb, i.tint.a), 1);
        }

        struct TerrainIn { float3 pos : POSITION; float3 nrm : NORMAL; };
        struct TerrainOut { float4 pos : SV_POSITION; float3 nrm : NORMAL; float3 wpos : TEXCOORD0; };
        TerrainOut TerrainVS(TerrainIn i)
        {
            TerrainOut o;
            o.pos = mul(float4(i.pos, 1), viewProj);
            o.nrm = i.nrm;
            o.wpos = i.pos;
            return o;
        }
        float3 GroundColour(uint g, float2 uv)
        {
            return groundColours.Sample(samp, float3(uv, min(g, (uint)groundTex.y - 1))).rgb;
        }
        // Blend groups of the four nearest lf pixels (row 0 = north), each a world-space tiled texture.
        float3 ClimateColour(float3 wpos)
        {
            float2 px = float2(wpos.x / groundWorld.x * groundWorld.z, (1 - wpos.z / groundWorld.y) * groundWorld.w) - 0.5;
            int2 p0 = (int2)floor(px);
            float2 f = px - p0;
            int2 hi = int2(groundWorld.zw) - 1;
            uint g00 = groundGroups.Load(int3(clamp(p0, 0, hi), 0));
            uint g10 = groundGroups.Load(int3(clamp(p0 + int2(1, 0), 0, hi), 0));
            uint g01 = groundGroups.Load(int3(clamp(p0 + int2(0, 1), 0, hi), 0));
            uint g11 = groundGroups.Load(int3(clamp(p0 + int2(1, 1), 0, hi), 0));
            float2 uv = wpos.xz / groundTex.x;
            return GroundColour(g00, uv) * (1 - f.x) * (1 - f.y) + GroundColour(g10, uv) * f.x * (1 - f.y)
                 + GroundColour(g01, uv) * (1 - f.x) * f.y + GroundColour(g11, uv) * f.x * f.y;
        }
        // The season's snow: the campaign snow mask (one texel per tile-map cell, row 0 = north) over the snow texture.
        float3 Snowed(float3 c, float3 wpos)
        {
            if (groundSnow.x < 0.5) return c;
            float m = snowMask.SampleLevel(samp, float2(wpos.x / groundWorld.x, 1 - wpos.z / groundWorld.y), 0).r;
            float3 snow = snowColour.Sample(samp, wpos.xz / groundTex.x).rgb;
            return lerp(c, snow, saturate(m * groundSnow.y));
        }
        // The region mask (world image, row 0 = north), alpha-blended over the ground when on.
        float3 Overlaid(float3 c, float3 wpos)
        {
            if (groundSnow.z < 0.5) return c;
            float4 o = regionOverlay.SampleLevel(samp, float2(wpos.x / groundWorld.x, 1 - wpos.z / groundWorld.y), 0);
            return lerp(c, o.rgb, o.a);
        }
        float4 TerrainPS(TerrainOut i) : SV_TARGET
        {
            float d = saturate(dot(normalize(i.nrm), -lightDir.xyz));
            float shade = 0.4 + 0.75 * d;
            if (groundTex.z < 0.5)
            {
                float t = saturate(i.wpos.y / 10);
                return float4(lerp(float3(0.30, 0.37, 0.24), float3(0.66, 0.60, 0.46), t) * shade, 1);
            }
            return float4(Overlaid(Snowed(ClimateColour(i.wpos), i.wpos) * shade, i.wpos), 1);
        }

        // Tile ground: a tile's blend0 weights mix its texture layers; the "climate" share stays the ground below
        // (alpha = the share of the tile's own layers).
        struct TileIn { float3 pos : POSITION; float3 nrm : NORMAL; float2 tuv : TEXCOORD0; float4 axes : TEXCOORD1; };
        struct TileOut { float4 pos : SV_POSITION; float3 nrm : NORMAL; float3 wpos : TEXCOORD0; float2 tuv : TEXCOORD1; float4 axes : TEXCOORD2; };
        TileOut TileVS(TileIn i)
        {
            TileOut o;
            o.pos = mul(float4(i.pos, 1), viewProj);
            o.nrm = i.nrm;
            o.wpos = i.pos;
            o.tuv = i.tuv;
            o.axes = i.axes;
            return o;
        }
        // The tile's own ground: blend0 weights over its layers (climate slots = the global ground), lit with the
        // tile's normal map (DXT5nm: x in alpha along the tile's a axis, y in green along b; axes = their world xz).
        float4 TilePS(TileOut i) : SV_TARGET
        {
            float4 w = tileBlend.Sample(samp, i.tuv);
            float total = w.r + w.g + w.b + w.a;
            if (total < 0.004) discard;
            float2 uv = i.wpos.xz / groundTex.x;
            float3 climate = ClimateColour(i.wpos);
            float3 c = 0;
            [unroll] for (int k = 0; k < 4; k++)
                c += w[k] * (tileLayers[k] >= 0 ? GroundColour((uint)tileLayers[k], uv) : climate);
            c = Snowed(c / total, i.wpos);
            float4 nm = tileNormal.Sample(samp, i.tuv);
            float2 t = nm.ag * 2 - 1;
            float3 n = normalize(normalize(i.nrm) + t.x * float3(i.axes.x, 0, i.axes.y) + t.y * float3(i.axes.z, 0, i.axes.w));
            float d = saturate(dot(n, -lightDir.xyz));
            return float4(Overlaid(c * (0.4 + 0.75 * d), i.wpos), 1);
        }

        // Screen-space sprites (labels): positions already in clip space, texture t0.
        struct SpriteIn { float2 pos : POSITION; float2 uv : TEXCOORD0; };
        struct SpriteOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
        SpriteOut SpriteVS(SpriteIn i) { SpriteOut o; o.pos = float4(i.pos, 0, 1); o.uv = i.uv; return o; }
        float4 SpritePS(SpriteOut i) : SV_TARGET { return tex.SampleLevel(samp, i.uv, 0); }

        struct LineIn { float3 pos : POSITION; float4 col : COLOR; };
        struct LineOut { float4 pos : SV_POSITION; float4 col : COLOR; };
        LineOut LineVS(LineIn i) { LineOut o; o.pos = mul(float4(i.pos, 1), viewProj); o.col = i.col; return o; }
        float4 LinePS(LineOut i) : SV_TARGET { return i.col; }
        """;

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public string AdapterName { get; }

    private readonly ID3D11VertexShader _meshVs, _terrainVs, _lineVs, _tileVs, _spriteVs;
    private readonly ID3D11PixelShader _meshPs, _terrainPs, _linePs, _tilePs, _spritePs;
    private readonly ID3D11InputLayout _meshLayout, _terrainLayout, _lineLayout, _tileLayout, _spriteLayout;
    private ID3D11Buffer? _spriteBuffer;
    private int _spriteCapacity;
    private readonly ID3D11Buffer _frameCb, _drawCb, _groundCb, _tileCb;
    private readonly ID3D11RasterizerState _solid;
    private readonly ID3D11DepthStencilState _depthOn, _depthOff, _depthReadOnly;
    private readonly ID3D11BlendState _opaque, _alpha;
    private readonly ID3D11SamplerState _sampler;
    private ID3D11Buffer? _instanceBuffer;
    private int _instanceCapacity;
    private ID3D11Buffer? _lineBuffer;
    private int _lineCapacity;

    // swap chain (window) target
    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _backBuffer;
    private ID3D11Texture2D? _depth;
    private ID3D11DepthStencilView? _depthView;
    public int Width { get; private set; }
    public int Height { get; private set; }

    public Renderer3D()
    {
        var levels = new[] { FeatureLevel.Level_11_0 };
        ID3D11Device? device;
        ID3D11DeviceContext? context;
        if (D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out device, out context).Failure)
            D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out device, out context).CheckError();
        Device = device!;
        Context = context!;
        using (var dxgi = Device.QueryInterface<IDXGIDevice>())
        using (var adapter = dxgi.GetAdapter())
            AdapterName = adapter.Description.Description;

        ReadOnlyMemory<byte> Compile(string entry, string profile) => Compiler.Compile(Hlsl, entry, "viewport.hlsl", profile);
        var meshVs = Compile("MeshVS", "vs_5_0");
        var terrainVs = Compile("TerrainVS", "vs_5_0");
        var lineVs = Compile("LineVS", "vs_5_0");
        var tileVs = Compile("TileVS", "vs_5_0");
        var spriteVs = Compile("SpriteVS", "vs_5_0");
        _spriteVs = Device.CreateVertexShader(spriteVs.Span);
        _spritePs = Device.CreatePixelShader(Compile("SpritePS", "ps_5_0").Span);
        _spriteLayout = Device.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 8, 0),
        ], spriteVs.Span);
        _tileVs = Device.CreateVertexShader(tileVs.Span);
        _tilePs = Device.CreatePixelShader(Compile("TilePS", "ps_5_0").Span);
        _meshVs = Device.CreateVertexShader(meshVs.Span);
        _terrainVs = Device.CreateVertexShader(terrainVs.Span);
        _lineVs = Device.CreateVertexShader(lineVs.Span);
        _meshPs = Device.CreatePixelShader(Compile("MeshPS", "ps_5_0").Span);
        _terrainPs = Device.CreatePixelShader(Compile("TerrainPS", "ps_5_0").Span);
        _linePs = Device.CreatePixelShader(Compile("LinePS", "ps_5_0").Span);

        _meshLayout = Device.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 24, 0),
            new InputElementDescription("WORLD", 0, Format.R32G32B32A32_Float, 0, 1, InputClassification.PerInstanceData, 1),
            new InputElementDescription("WORLD", 1, Format.R32G32B32A32_Float, 16, 1, InputClassification.PerInstanceData, 1),
            new InputElementDescription("WORLD", 2, Format.R32G32B32A32_Float, 32, 1, InputClassification.PerInstanceData, 1),
            new InputElementDescription("WORLD", 3, Format.R32G32B32A32_Float, 48, 1, InputClassification.PerInstanceData, 1),
            new InputElementDescription("TINT", 0, Format.R32G32B32A32_Float, 64, 1, InputClassification.PerInstanceData, 1),
        ], meshVs.Span);
        _terrainLayout = Device.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
        ], terrainVs.Span);
        _tileLayout = Device.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 24, 0),
            new InputElementDescription("TEXCOORD", 1, Format.R32G32B32A32_Float, 32, 0),
        ], tileVs.Span);
        _lineLayout = Device.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("COLOR", 0, Format.R32G32B32A32_Float, 12, 0),
        ], lineVs.Span);

        _frameCb = Device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<FrameConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _drawCb = Device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<DrawConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _groundCb = Device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<TerrainConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _tileCb = Device.CreateBuffer(new BufferDescription(16, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _solid = Device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid) { DepthClipEnable = true });
        _depthOn = Device.CreateDepthStencilState(DepthStencilDescription.Default);
        _depthOff = Device.CreateDepthStencilState(DepthStencilDescription.None);
        _depthReadOnly = Device.CreateDepthStencilState(DepthStencilDescription.DepthRead);
        _opaque = Device.CreateBlendState(BlendDescription.Opaque);
        _alpha = Device.CreateBlendState(BlendDescription.NonPremultiplied);
        _sampler = Device.CreateSamplerState(new SamplerDescription(Filter.Anisotropic, TextureAddressMode.Wrap, TextureAddressMode.Wrap, TextureAddressMode.Wrap) { MaxAnisotropy = 8, MaxLOD = float.MaxValue });
        _flatNormal = CreateTexture(1, 1, [255, 128, 0, 128]); // DXT5nm flat: x (alpha) = y (green) = 0.5
    }

    // ---------------------------------------------------------------- resources (thread-safe: device calls only)

    public GpuMesh CreateMesh(float[] positions, float[] normals, float[] uvs, ushort[] indices, GpuTexture? texture, bool alphaTest, float terrainOffset = 0, bool water = false)
    {
        var count = positions.Length / 3;
        var vertices = new float[count * 8];
        for (var i = 0; i < count; i++)
        {
            vertices[i * 8] = positions[i * 3];
            vertices[i * 8 + 1] = positions[i * 3 + 1];
            vertices[i * 8 + 2] = positions[i * 3 + 2];
            if (normals.Length >= (i + 1) * 3)
            {
                vertices[i * 8 + 3] = normals[i * 3];
                vertices[i * 8 + 4] = normals[i * 3 + 1];
                vertices[i * 8 + 5] = normals[i * 3 + 2];
            }
            if (uvs.Length >= (i + 1) * 2)
            {
                vertices[i * 8 + 6] = uvs[i * 2];
                vertices[i * 8 + 7] = uvs[i * 2 + 1];
            }
        }
        // Index buffers need an even byte count for some drivers; pad with a degenerate index.
        var idx = indices.Length % 2 == 0 ? indices : [.. indices, 0];
        return new GpuMesh
        {
            Vertices = Device.CreateBuffer(vertices, BindFlags.VertexBuffer),
            Indices = Device.CreateBuffer(idx, BindFlags.IndexBuffer),
            IndexCount = indices.Length,
            Texture = texture,
            AlphaTest = alphaTest,
            TerrainOffset = terrainOffset,
            Water = water,
        };
    }

    public GpuTexture CreateTexture(int width, int height, byte[] rgba)
    {
        var mips = 1 + (int)Math.Floor(Math.Log2(Math.Max(width, height)));
        var desc = new Texture2DDescription
        {
            Width = (uint)width, Height = (uint)height, MipLevels = (uint)mips, ArraySize = 1, Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget, MiscFlags = ResourceOptionFlags.GenerateMips,
        };
        var texture = Device.CreateTexture2D(desc);
        // Level 0 is filled on the render thread (UpdateSubresource is a context call): keep the pixels until then.
        _pendingUploads.Enqueue((texture, 0, rgba, width * 4));
        return new GpuTexture { Texture = texture, View = Device.CreateShaderResourceView(texture) };
    }

    private readonly System.Collections.Concurrent.ConcurrentQueue<(ID3D11Texture2D Texture, uint Subresource, byte[] Data, int RowPitch)> _pendingUploads = new();

    /// <summary>
    /// Ground for the terrain shader: group indices (width × height bytes, row 0 = north) and one 512×512 RGBA image
    /// per group (null = flat <paramref name="fallback"/> colour).
    /// </summary>
    public TerrainMaterial CreateTerrainMaterial(byte[] groups, int width, int height, IReadOnlyList<byte[]?> layers, IReadOnlyList<uint> fallback,
                                                 Vector2 worldSize, float repeatWorld)
    {
        const int size = 512;
        var groupTex = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = Format.R8_UInt,
            SampleDescription = new SampleDescription(1, 0), BindFlags = BindFlags.ShaderResource,
        });
        _pendingUploads.Enqueue((groupTex, 0, groups, width));
        var mips = 1 + (int)Math.Log2(size);
        var count = Math.Max(1, layers.Count);
        var array = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = size, Height = size, MipLevels = (uint)mips, ArraySize = (uint)count, Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            MiscFlags = ResourceOptionFlags.GenerateMips,
        });
        for (var i = 0; i < count; i++)
        {
            var data = i < layers.Count && layers[i] is { } l ? l : Flat(size, i < fallback.Count ? fallback[i] : 0xFF808080u);
            _pendingUploads.Enqueue((array, (uint)(i * mips), data, size * 4)); // subresource = mip 0 of slice i
        }
        return new TerrainMaterial
        {
            Groups = groupTex, GroupsView = Device.CreateShaderResourceView(groupTex),
            Textures = array, TexturesView = Device.CreateShaderResourceView(array),
            World = new Vector4(worldSize.X, worldSize.Y, width, height), RepeatWorld = repeatWorld,
        };

        static byte[] Flat(int n, uint rgba)
        {
            var b = new byte[n * n * 4];
            for (var k = 0; k < b.Length; k += 4)
            {
                b[k] = (byte)rgba; b[k + 1] = (byte)(rgba >> 8); b[k + 2] = (byte)(rgba >> 16); b[k + 3] = 255;
            }
            return b;
        }
    }

    /// <summary>A static coloured mesh (line-vertex format) with 32-bit indices, e.g. the water surface.</summary>
    public (ID3D11Buffer Vertices, ID3D11Buffer Indices, int IndexCount) CreateColoured(LineVertex[] vertices, uint[] indices) =>
        (Device.CreateBuffer(vertices, BindFlags.VertexBuffer), Device.CreateBuffer(indices, BindFlags.IndexBuffer), indices.Length);

    /// <summary>Draws a static coloured mesh translucently (depth tested, not written).</summary>
    public void DrawColoured(ID3D11Buffer vertices, ID3D11Buffer indices, int indexCount)
    {
        Context.OMSetDepthStencilState(_depthReadOnly);
        Context.OMSetBlendState(_alpha);
        Context.IASetInputLayout(_lineLayout);
        Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        Context.IASetVertexBuffer(0, vertices, (uint)Marshal.SizeOf<LineVertex>());
        Context.IASetIndexBuffer(indices, Format.R32_UInt, 0);
        Context.VSSetShader(_lineVs);
        Context.PSSetShader(_linePs);
        Context.DrawIndexed((uint)indexCount, 0, 0);
    }

    public (ID3D11Buffer Vertices, ID3D11Buffer Indices, int IndexCount) CreateTerrain(float[] vertices, uint[] indices) =>
        (Device.CreateBuffer(vertices, BindFlags.VertexBuffer), Device.CreateBuffer(indices, BindFlags.IndexBuffer), indices.Length);

    // ---------------------------------------------------------------- targets

    public void AttachWindow(IntPtr hwnd, int width, int height)
    {
        using var dxgi = Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgi.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        var desc = new SwapChainDescription1
        {
            Width = (uint)Math.Max(1, width), Height = (uint)Math.Max(1, height), Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2, BufferUsage = Usage.RenderTargetOutput, SampleDescription = new SampleDescription(1, 0),
            SwapEffect = SwapEffect.FlipDiscard, Scaling = Scaling.Stretch, AlphaMode = AlphaMode.Ignore,
        };
        _swapChain = factory.CreateSwapChainForHwnd(Device, hwnd, desc);
        factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAll);
        CreateTargets(width, height);
    }

    public void Resize(int width, int height)
    {
        if (_swapChain is null || width < 1 || height < 1 || (width == Width && height == Height)) return;
        Context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
        _backBuffer?.Dispose();
        _depthView?.Dispose();
        _depth?.Dispose();
        _swapChain.ResizeBuffers(2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
        CreateTargets(width, height);
    }

    private void CreateTargets(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        using var back = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        _backBuffer = Device.CreateRenderTargetView(back);
        (_depth, _depthView) = CreateDepth(Width, Height);
    }

    private (ID3D11Texture2D, ID3D11DepthStencilView) CreateDepth(int width, int height)
    {
        var depth = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = Format.D32_Float,
            SampleDescription = new SampleDescription(1, 0), BindFlags = BindFlags.DepthStencil,
        });
        return (depth, Device.CreateDepthStencilView(depth));
    }

    // ---------------------------------------------------------------- frame

    /// <summary>Starts a frame on the window (or on <paramref name="target"/>), clears it and sets the camera.</summary>
    public void Begin(Matrix4x4 viewProj, Vector3 cameraPos, Vector4 clearColour, ID3D11RenderTargetView? target = null,
                      ID3D11DepthStencilView? depth = null, int width = 0, int height = 0, bool clear = true)
    {
        while (_pendingUploads.TryDequeue(out var up))
            Context.UpdateSubresource(up.Data, up.Texture, up.Subresource, (uint)up.RowPitch);
        var rtv = target ?? _backBuffer!;
        var dsv = depth ?? _depthView!;
        Context.OMSetRenderTargets(rtv, dsv);
        if (clear)
        {
            Context.ClearRenderTargetView(rtv, new Color4(clearColour.X, clearColour.Y, clearColour.Z, clearColour.W));
            Context.ClearDepthStencilView(dsv, DepthStencilClearFlags.Depth, 1, 0);
        }
        Context.RSSetViewport(0, 0, width > 0 ? width : Width, height > 0 ? height : Height);
        Context.RSSetState(_solid);
        Context.PSSetSampler(0, _sampler);
        var light = Vector3.Normalize(new Vector3(0.35f, -0.8f, 0.45f));
        Write(_frameCb, new FrameConstants { ViewProj = viewProj, LightDir = new Vector4(light, 0), CameraPos = new Vector4(cameraPos, 1) });
        Context.VSSetConstantBuffer(0, _frameCb);
        Context.PSSetConstantBuffer(0, _frameCb);
        Context.VSSetConstantBuffer(1, _drawCb);
        Context.PSSetConstantBuffer(1, _drawCb);
    }

    public void Present() => _swapChain?.Present(1, PresentFlags.None);

    private GpuTexture? _snowMask, _snowColour, _flatNormal;
    private float _snowStrength;

    /// <summary>The season's snow for the ground shaders (null mask = no snow).</summary>
    public void SetSnow(GpuTexture? mask, GpuTexture? colour, float strength = 1.5f)
    {
        (_snowMask, _snowColour, _snowStrength) = (mask, colour, strength);
    }

    private void BindSnow()
    {
        foreach (var t in new[] { _snowMask, _snowColour, _overlay })
            if (t is { MipsGenerated: false })
            {
                Context.GenerateMips(t.View);
                t.MipsGenerated = true;
            }
        if (_overlay is not null) Context.PSSetShaderResource(7, _overlay.View);
        if (_snowMask is not null && _snowColour is not null)
        {
            Context.PSSetShaderResource(4, _snowMask.View);
            Context.PSSetShaderResource(5, _snowColour.View);
        }
    }

    private GpuTexture? _overlay;

    /// <summary>A world-covering RGBA overlay for the ground (the region mask; null = none).</summary>
    public void SetOverlay(GpuTexture? overlay) => _overlay = overlay;

    private Vector4 SnowConstants => new(_snowMask is not null && _snowColour is not null ? 1 : 0, _snowStrength, _overlay is not null ? 1 : 0, 0);

    public void DrawTerrain(ID3D11Buffer vertices, ID3D11Buffer indices, int indexCount, TerrainMaterial? ground = null)
    {
        if (ground is { MipsGenerated: false })
        {
            Context.GenerateMips(ground.TexturesView);
            ground.MipsGenerated = true;
        }
        Write(_groundCb, new TerrainConstants
        {
            World = ground?.World ?? Vector4.One,
            Texture = new Vector4(ground?.RepeatWorld ?? 1, ground is null ? 1 : ground.Textures.Description.ArraySize, ground is null ? 0 : 1, 0),
            Snow = ground is null ? Vector4.Zero : SnowConstants,
        });
        BindSnow();
        Context.PSSetConstantBuffer(2, _groundCb);
        Context.PSSetShaderResource(1, ground?.GroupsView!);
        Context.PSSetShaderResource(2, ground?.TexturesView!);
        Context.OMSetDepthStencilState(_depthOn);
        Context.OMSetBlendState(_opaque);
        Context.IASetInputLayout(_terrainLayout);
        Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        Context.IASetVertexBuffer(0, vertices, 24);
        Context.IASetIndexBuffer(indices, Format.R32_UInt, 0);
        Context.VSSetShader(_terrainVs);
        Context.PSSetShader(_terrainPs);
        Context.DrawIndexed((uint)indexCount, 0, 0);
    }

    /// <summary>
    /// Tile ground (after <see cref="DrawTerrain"/>, whose ground constants and texture array it reuses): the index
    /// range of one tile folder's draped grids (12 floats per vertex: position, lf normal, tile uv, world xz of the
    /// tile's a and b axes), its blend0 weights and normal map, and the ground group of each channel (-1 = climate,
    /// computed here from the global ground). Opaque over the terrain, no depth write.
    /// </summary>
    public void DrawTileGround(ID3D11Buffer vertices, ID3D11Buffer indices, int start, int count, GpuTexture blend, GpuTexture? normal,
                               (int R, int G, int B, int A) layers)
    {
        normal ??= _flatNormal!;
        foreach (var t in new[] { blend, normal })
            if (!t.MipsGenerated)
            {
                Context.GenerateMips(t.View);
                t.MipsGenerated = true;
            }
        Context.PSSetShaderResource(6, normal.View);
        BindSnow();
        Write(_tileCb, new TileConstants { Layers = new Int4 { X = layers.R, Y = layers.G, Z = layers.B, W = layers.A } });
        Context.PSSetConstantBuffer(2, _groundCb);
        Context.PSSetConstantBuffer(3, _tileCb);
        Context.PSSetShaderResource(3, blend.View);
        Context.OMSetDepthStencilState(_depthReadOnly);
        Context.OMSetBlendState(_alpha);
        Context.IASetInputLayout(_tileLayout);
        Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        Context.IASetVertexBuffer(0, vertices, 48);
        Context.IASetIndexBuffer(indices, Format.R32_UInt, 0);
        Context.VSSetShader(_tileVs);
        Context.PSSetShader(_tilePs);
        Context.DrawIndexed((uint)count, (uint)start, 0);
    }

    private ID3D11ShaderResourceView? _terrainHeight;
    private Vector4 _terrainConstants;

    /// <summary>
    /// The lf heights for the LF-offset mountain shader (R16 UNorm, row 0 = north; any resolution covering the
    /// world) and how raw values map to world heights (raw × step + offset).
    /// </summary>
    public void SetTerrainHeight(ushort[] raw, int width, int height, float worldW, float worldH, float step, float offset)
    {
        if (_terrainHeightTex is { } existing && (_terrainHeightSize) == (width, height))
        {
            // After a height edit: re-upload into the same texture rather than allocating a new one.
            var data = new byte[raw.Length * 2];
            Buffer.BlockCopy(raw, 0, data, 0, data.Length);
            _pendingUploads.Enqueue((existing, 0, data, width * 2));
            return;
        }
        var tex = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = Format.R16_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
        });
        var bytes = new byte[raw.Length * 2];
        Buffer.BlockCopy(raw, 0, bytes, 0, bytes.Length);
        _pendingUploads.Enqueue((tex, 0, bytes, width * 2));
        _terrainHeight = Device.CreateShaderResourceView(tex);
        _terrainHeightTex = tex;
        _terrainHeightSize = (width, height);
        _terrainConstants = new Vector4(worldW, worldH, step * 65535, offset);
    }
    private ID3D11Texture2D? _terrainHeightTex;
    private (int W, int H) _terrainHeightSize;

    /// <summary>Uploads every instance of the frame at once (one map); draw ranges of it with <see cref="DrawInstanceRange"/>.</summary>
    public void UploadInstances(ReadOnlySpan<InstanceData> instances)
    {
        if (instances.Length == 0) return;
        EnsureInstanceBuffer(instances.Length);
        var map = Context.Map(_instanceBuffer!, 0, MapMode.WriteDiscard);
        unsafe
        {
            fixed (InstanceData* src = instances)
                Buffer.MemoryCopy(src, (void*)map.DataPointer, (long)_instanceCapacity * sizeof(InstanceData), (long)instances.Length * sizeof(InstanceData));
        }
        Context.Unmap(_instanceBuffer!, 0);
    }

    /// <summary>Draws a mesh for instances [start, start + count) of the last <see cref="UploadInstances"/>.</summary>
    public void DrawInstanceRange(GpuMesh mesh, int start, int count)
    {
        if (count == 0 || mesh.IndexCount == 0) return;
        if (mesh.Texture is { MipsGenerated: false } t)
        {
            Context.GenerateMips(t.View);
            t.MipsGenerated = true;
        }
        Write(_drawCb, new DrawConstants { Flags = new Vector4(mesh.Texture is null ? 0 : 1, mesh.AlphaTest ? 1 : 0, mesh.TerrainOffset, mesh.Water ? 1 : 0), Terrain = _terrainConstants });
        Context.VSSetConstantBuffer(1, _drawCb);
        if (_terrainHeight is not null) Context.VSSetShaderResource(8, _terrainHeight);
        Context.VSSetSampler(0, _sampler);
        Context.OMSetDepthStencilState(_depthOn);
        Context.OMSetBlendState(_opaque);
        Context.IASetInputLayout(_meshLayout);
        Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        Context.IASetVertexBuffers(0, 2, [mesh.Vertices, _instanceBuffer!], [32u, (uint)Marshal.SizeOf<InstanceData>()], [0u, 0u]);
        Context.IASetIndexBuffer(mesh.Indices, Format.R16_UInt, 0);
        Context.VSSetShader(_meshVs);
        Context.PSSetShader(_meshPs);
        Context.PSSetShaderResource(0, mesh.Texture?.View!);
        Context.DrawIndexedInstanced((uint)mesh.IndexCount, (uint)count, 0, 0, (uint)start);
    }

    /// <summary>Draws one mesh for many instances.</summary>
    public void DrawInstanced(GpuMesh mesh, ReadOnlySpan<InstanceData> instances)
    {
        if (instances.Length == 0 || mesh.IndexCount == 0) return;
        EnsureInstanceBuffer(instances.Length);
        var map = Context.Map(_instanceBuffer!, 0, MapMode.WriteDiscard);
        unsafe
        {
            fixed (InstanceData* src = instances)
                Buffer.MemoryCopy(src, (void*)map.DataPointer, (long)_instanceCapacity * sizeof(InstanceData), (long)instances.Length * sizeof(InstanceData));
        }
        Context.Unmap(_instanceBuffer!, 0);
        if (mesh.Texture is { MipsGenerated: false } t)
        {
            Context.GenerateMips(t.View);
            t.MipsGenerated = true;
        }
        Write(_drawCb, new DrawConstants { Flags = new Vector4(mesh.Texture is null ? 0 : 1, mesh.AlphaTest ? 1 : 0, 0, 0) });
        Context.OMSetDepthStencilState(_depthOn);
        Context.OMSetBlendState(_opaque);
        Context.IASetInputLayout(_meshLayout);
        Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        Context.IASetVertexBuffers(0, 2, [mesh.Vertices, _instanceBuffer!], [32u, (uint)Marshal.SizeOf<InstanceData>()], [0u, 0u]);
        Context.IASetIndexBuffer(mesh.Indices, Format.R16_UInt, 0);
        Context.VSSetShader(_meshVs);
        Context.PSSetShader(_meshPs);
        Context.PSSetShaderResource(0, mesh.Texture?.View!);
        Context.DrawIndexedInstanced((uint)mesh.IndexCount, (uint)instances.Length, 0, 0, 0);
    }

    /// <summary>Coloured lines (pairs of vertices) or, with triangles, translucent fills. Overlay = drawn over everything.</summary>
    public void DrawLines(ReadOnlySpan<LineVertex> vertices, bool overlay = false, bool triangles = false)
    {
        if (vertices.Length == 0) return;
        if (_lineBuffer is null || _lineCapacity < vertices.Length)
        {
            _lineBuffer?.Dispose();
            _lineCapacity = Math.Max(vertices.Length, Math.Max(4096, _lineCapacity * 2));
            _lineBuffer = Device.CreateBuffer(new BufferDescription((uint)(_lineCapacity * Marshal.SizeOf<LineVertex>()), BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        }
        var map = Context.Map(_lineBuffer, 0, MapMode.WriteDiscard);
        unsafe
        {
            fixed (LineVertex* src = vertices)
                Buffer.MemoryCopy(src, (void*)map.DataPointer, (long)_lineCapacity * sizeof(LineVertex), (long)vertices.Length * sizeof(LineVertex));
        }
        Context.Unmap(_lineBuffer, 0);
        Context.OMSetDepthStencilState(overlay ? _depthOff : triangles ? _depthReadOnly : _depthOn);
        Context.OMSetBlendState(_alpha);
        Context.IASetInputLayout(_lineLayout);
        Context.IASetPrimitiveTopology(triangles ? PrimitiveTopology.TriangleList : PrimitiveTopology.LineList);
        Context.IASetVertexBuffer(0, _lineBuffer, (uint)Marshal.SizeOf<LineVertex>());
        Context.VSSetShader(_lineVs);
        Context.PSSetShader(_linePs);
        Context.Draw((uint)vertices.Length, 0);
    }

    /// <summary>Screen-space textured quads (x, y in clip space, u, v per vertex; 6 per quad), alpha blended on
    /// top of everything (labels).</summary>
    public void DrawSprites(GpuTexture texture, ReadOnlySpan<float> vertices)
    {
        var count = vertices.Length / 4;
        if (count == 0) return;
        if (_spriteBuffer is null || _spriteCapacity < count)
        {
            _spriteBuffer?.Dispose();
            _spriteCapacity = Math.Max(count, Math.Max(1024, _spriteCapacity * 2));
            _spriteBuffer = Device.CreateBuffer(new BufferDescription((uint)(_spriteCapacity * 16), BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        }
        var map = Context.Map(_spriteBuffer, 0, MapMode.WriteDiscard);
        unsafe
        {
            fixed (float* src = vertices)
                Buffer.MemoryCopy(src, (void*)map.DataPointer, (long)_spriteCapacity * 16, (long)vertices.Length * 4);
        }
        Context.Unmap(_spriteBuffer, 0);
        Context.OMSetDepthStencilState(_depthOff);
        Context.OMSetBlendState(_alpha);
        Context.IASetInputLayout(_spriteLayout);
        Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        Context.IASetVertexBuffer(0, _spriteBuffer, 16);
        Context.VSSetShader(_spriteVs);
        Context.PSSetShader(_spritePs);
        Context.PSSetShaderResource(0, texture.View);
        Context.Draw((uint)count, 0);
    }

    private void EnsureInstanceBuffer(int count)
    {
        if (_instanceBuffer is not null && _instanceCapacity >= count) return;
        _instanceBuffer?.Dispose();
        _instanceCapacity = Math.Max(count, Math.Max(1024, _instanceCapacity * 2));
        _instanceBuffer = Device.CreateBuffer(new BufferDescription((uint)(_instanceCapacity * Marshal.SizeOf<InstanceData>()), BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
    }

    private void Write<T>(ID3D11Buffer buffer, T value) where T : unmanaged
    {
        var map = Context.Map(buffer, 0, MapMode.WriteDiscard);
        unsafe { *(T*)map.DataPointer = value; }
        Context.Unmap(buffer, 0);
    }

    // ---------------------------------------------------------------- screenshots

    /// <summary>Renders with <paramref name="draw"/> into an offscreen target and returns BGRA pixels.</summary>
    public byte[] RenderOffscreen(int width, int height, Action<ID3D11RenderTargetView, ID3D11DepthStencilView> draw)
    {
        var desc = new Texture2DDescription
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), BindFlags = BindFlags.RenderTarget,
        };
        using var target = Device.CreateTexture2D(desc);
        using var rtv = Device.CreateRenderTargetView(target);
        var (depth, dsv) = CreateDepth(width, height);
        using (depth)
        using (dsv)
            draw(rtv, dsv);
        using var staging = Device.CreateTexture2D(desc with { BindFlags = BindFlags.None, Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read });
        Context.CopyResource(staging, target);
        var map = Context.Map(staging, 0, MapMode.Read);
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            Marshal.Copy((nint)map.DataPointer + y * (nint)map.RowPitch, pixels, y * width * 4, width * 4);
        Context.Unmap(staging, 0);
        return pixels;
    }

    public void Dispose()
    {
        _backBuffer?.Dispose();
        _depthView?.Dispose();
        _depth?.Dispose();
        _swapChain?.Dispose();
        _instanceBuffer?.Dispose();
        _lineBuffer?.Dispose();
        foreach (var d in new IDisposable[] { _meshVs, _terrainVs, _lineVs, _meshPs, _terrainPs, _linePs, _meshLayout, _terrainLayout, _lineLayout,
                                               _frameCb, _drawCb, _groundCb, _solid, _depthOn, _depthOff, _depthReadOnly, _opaque, _alpha, _sampler })
            d.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}
