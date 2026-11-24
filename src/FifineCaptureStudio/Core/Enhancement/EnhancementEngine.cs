using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FifineCaptureStudio.Core;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using D3DBuffer = SharpDX.Direct3D11.Buffer;
using D3DDevice = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;

namespace FifineCaptureStudio.Core.Enhancement;

/// <summary>
/// Direct3D 11 compute pipeline that processes real capture frames on the GPU.
/// A frame is uploaded once, kept resident through denoise, grading and upscaling,
/// then downloaded once for presentation. No pass round-trips through system memory.
/// </summary>
public sealed class EnhancementEngine : IDisposable
{
    private readonly object _gate = new();
    private readonly D3DDevice _device;
    private readonly DeviceContext _context;
    private readonly ComputeShader _denoise;
    private readonly ComputeShader _grade;
    private readonly ComputeShader _upscale;
    private readonly ComputeShader _copy;
    private readonly D3DBuffer _params;
    private readonly SamplerState _sampler;

    private Texture2D? _source;
    private ShaderResourceView? _sourceView;
    private Texture2D? _work;
    private ShaderResourceView? _workView;
    private UnorderedAccessView? _workUav;
    private Texture2D? _work2;
    private ShaderResourceView? _work2View;
    private UnorderedAccessView? _work2Uav;
    private Texture2D? _output;
    private ShaderResourceView? _outputView;
    private UnorderedAccessView? _outputUav;
    private Texture2D? _staging;

    private Query? _disjoint;
    private Query? _t0;
    private Query? _t1;
    private Query? _t2;
    private Query? _t3;
    private Query? _t4;

    private int _sourceWidth;
    private int _sourceHeight;
    private int _outputWidth;
    private int _outputHeight;
    private byte[] _pixels = Array.Empty<byte>();
    private bool _disposed;

    [StructLayout(LayoutKind.Sequential)]
    private struct ShaderParams
    {
        public Vector4 P0;
        public Vector4 P1;
        public Vector4 P2;
        public Vector4 P3;
        public uint Width;
        public uint Height;
        public Vector2 Texel;
    }

    public EnhancementEngine(GpuDetection.AdapterEntry entry)
    {
        using var adapter = new Factory1().GetAdapter1(entry.Index);
        _device = new D3DDevice(adapter, DeviceCreationFlags.BgraSupport, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1);
        _context = _device.ImmediateContext;
        AdapterName = entry.Info.Name;

        _denoise = new ComputeShader(_device, ShaderBytecode.Compile(EnhancementShaders.Denoise, "CSDenoise", "cs_5_0"));
        _grade = new ComputeShader(_device, ShaderBytecode.Compile(EnhancementShaders.Grade, "CSGrade", "cs_5_0"));
        _upscale = new ComputeShader(_device, ShaderBytecode.Compile(EnhancementShaders.Upscale, "CSUpscale", "cs_5_0"));
        _copy = new ComputeShader(_device, ShaderBytecode.Compile(EnhancementShaders.Copy, "CSCopy", "cs_5_0"));

        _params = new D3DBuffer(_device, new BufferDescription(
            Marshal.SizeOf<ShaderParams>(),
            ResourceUsage.Default,
            BindFlags.ConstantBuffer,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0));

        _sampler = new SamplerState(_device, new SamplerStateDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunction = Comparison.Never,
            MaximumLod = float.MaxValue
        });

        // Binding these once covers every pass; the shader switch does not clear them.
        _context.ComputeShader.SetConstantBuffer(0, _params);
        _context.ComputeShader.SetSampler(0, _sampler);

        // GPU timestamp queries give real per-pass timings instead of CPU submit time.
        _disjoint = new Query(_device, new QueryDescription { Type = QueryType.TimestampDisjoint });
        _t0 = new Query(_device, new QueryDescription { Type = QueryType.Timestamp });
        _t1 = new Query(_device, new QueryDescription { Type = QueryType.Timestamp });
        _t2 = new Query(_device, new QueryDescription { Type = QueryType.Timestamp });
        _t3 = new Query(_device, new QueryDescription { Type = QueryType.Timestamp });
        _t4 = new Query(_device, new QueryDescription { Type = QueryType.Timestamp });
    }

    public string AdapterName { get; }


    /// <summary>Processes one frame and returns its BGRA pixels, or null when work is impossible.</summary>
    public EnhancedFrame? Process(BitmapSource source, EnhancementConfig config)
    {
        if (_disposed || source.PixelWidth < 2 || source.PixelHeight < 2)
        {
            return null;
        }

        lock (_gate)
        {
            var (targetWidth, targetHeight) = EnhancementConfig.Resolution(config.Target);
            var outWidth = (targetWidth > 0 ? targetWidth : source.PixelWidth) & ~1;
            var outHeight = (targetHeight > 0 ? targetHeight : source.PixelHeight) & ~1;
            EnsureResources(source.PixelWidth, source.PixelHeight, outWidth, outHeight);

            var total = Stopwatch.StartNew();
            var upload = Stopwatch.StartNew();
            Upload(source);
            var uploadMs = upload.Elapsed.TotalMilliseconds;

            var denoiseAmount = config.Denoise
                ? Math.Clamp(Math.Max(config.CompressionReduction, 0.15), 0, 1)
                : 0;
            var deblock = config.Deblock ? denoiseAmount : 0;

            _context.Begin(_disjoint!);
            _context.End(_t0!);
            _context.ComputeShader.Set(_denoise);
            SetParams(config, source.PixelWidth, source.PixelHeight, denoiseAmount, deblock);
            _context.ComputeShader.SetShaderResource(0, _sourceView);
            _context.ComputeShader.SetUnorderedAccessView(0, _workUav);
            _context.Dispatch((source.PixelWidth + 7) / 8, (source.PixelHeight + 7) / 8, 1);
            _context.End(_t1!);
            _context.ComputeShader.Set(_grade);
            SetParams(config, source.PixelWidth, source.PixelHeight, denoiseAmount, deblock);
            _context.ComputeShader.SetUnorderedAccessView(0, _work2Uav);
            _context.ComputeShader.SetShaderResource(0, _workView);
            _context.Dispatch((source.PixelWidth + 7) / 8, (source.PixelHeight + 7) / 8, 1);
            _context.End(_t2!);
            _context.ComputeShader.SetUnorderedAccessView(0, null);
            _context.ComputeShader.Set(outWidth == source.PixelWidth && outHeight == source.PixelHeight ? _copy : _upscale);
            SetParams(config, outWidth, outHeight, denoiseAmount, deblock);
            _context.ComputeShader.SetShaderResource(0, _work2View);
            _context.ComputeShader.SetUnorderedAccessView(0, _outputUav);
            _context.Dispatch((outWidth + 7) / 8, (outHeight + 7) / 8, 1);
            _context.End(_t3!);

            _context.ComputeShader.SetUnorderedAccessView(0, null);
            _context.CopyResource(_output, _staging);
            _context.End(_t4!);
            _context.End(_disjoint!);

            // Map blocks until the GPU copy has completed. That wait is real presentation latency but
            // it is not shader work, so it is measured separately from the GPU-side copy timestamp.
            var readbackWait = Stopwatch.StartNew();
            var pixels = Readback();
            readbackWait.Stop();
            var (denoiseMs, gradeMs, upscaleMs, readbackMs) = ReadGpuTimings(uploadMs);

            return new EnhancedFrame(
                pixels,
                outWidth * 4,
                source.PixelWidth,
                source.PixelHeight,
                outWidth,
                outHeight,
                new EnhancementTimings(uploadMs, denoiseMs, gradeMs, upscaleMs, readbackMs, readbackWait.Elapsed.TotalMilliseconds),
                true);
        }
    }

    private (double Denoise, double Grade, double Upscale, double Readback) ReadGpuTimings(double uploadMs)
    {
        try
        {
            // The disjoint query is only readable once the GPU has finished the frame; without this
            // the driver reports S_FALSE and the timings would silently read as zero.
            QueryDataTimestampDisjoint disjoint = default;
            var okDisjoint = false;
            for (var attempt = 0; attempt < 20 && !okDisjoint; attempt++)
            {
                _context.Flush();
                okDisjoint = _context.GetData(_disjoint!, out disjoint);
                if (!okDisjoint)
                {
                    Thread.Sleep(1);
                }
            }

            if (!okDisjoint || disjoint.Disjoint || disjoint.Frequency == 0)
            {
                return (0, 0, 0, 0);
            }

            _context.GetData(_t0!, out long t0);
            _context.GetData(_t1!, out long t1);
            _context.GetData(_t2!, out long t2);
            _context.GetData(_t3!, out long t3);
            _context.GetData(_t4!, out long t4);
            var frequency = (double)disjoint.Frequency;
            double Ms(long a, long b) => Math.Max(0, (b - a) * 1000.0 / frequency);
            return (Ms(t0, t1), Ms(t1, t2), Ms(t2, t3), Ms(t3, t4));
        }
        catch
        {
            return (0, 0, 0, 0);
        }
    }

    private void EnsureResources(int sourceWidth, int sourceHeight, int outWidth, int outHeight)
    {
        if (_sourceWidth == sourceWidth && _sourceHeight == sourceHeight
            && _outputWidth == outWidth && _outputHeight == outHeight)
        {
            return;
        }

        ReleaseResources();

        _source = new Texture2D(_device, new Texture2DDescription
        {
            Width = sourceWidth,
            Height = sourceHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None
        });
        _sourceView = CreateSrv(_source);

        _work = CreateTarget(sourceWidth, sourceHeight);
        _workView = CreateSrv(_work);
        _workUav = new UnorderedAccessView(_device, _work);

        _work2 = CreateTarget(sourceWidth, sourceHeight);
        _work2View = CreateSrv(_work2);
        _work2Uav = new UnorderedAccessView(_device, _work2);

        _output = CreateTarget(outWidth, outHeight);
        _outputView = CreateSrv(_output);
        _outputUav = new UnorderedAccessView(_device, _output);

        _staging = new Texture2D(_device, new Texture2DDescription
        {
            Width = outWidth,
            Height = outHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Read,
            OptionFlags = ResourceOptionFlags.None
        });

        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
        _outputWidth = outWidth;
        _outputHeight = outHeight;
        _pixels = new byte[outWidth * outHeight * 4];
    }

    /// <summary>
    /// Explicit SRV description: the parameterless view leaves the mip range empty on some drivers,
    /// which reads back as black even though the texture holds valid pixels.
    /// </summary>
    private ShaderResourceView CreateSrv(Texture2D texture) => new(_device, texture, new ShaderResourceViewDescription
    {
        Format = Format.B8G8R8A8_UNorm,
        Dimension = SharpDX.Direct3D.ShaderResourceViewDimension.Texture2D,
        Texture2D = new ShaderResourceViewDescription.Texture2DResource
        {
            MostDetailedMip = 0,
            MipLevels = 1
        }
    });

    private Texture2D CreateTarget(int width, int height) => new(_device, new Texture2DDescription
    {
        Width = width,
        Height = height,
        MipLevels = 1,
        ArraySize = 1,
        Format = Format.B8G8R8A8_UNorm,
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Default,
        BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
        CpuAccessFlags = CpuAccessFlags.None,
        OptionFlags = ResourceOptionFlags.None
    });

    private void Upload(BitmapSource source)
    {
        var bytes = ToBgra(source, out var stride);
        if (stride != _sourceWidth * 4)
        {
            return;
        }

        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            _context.UpdateSubresource(
                (SharpDX.Direct3D11.Resource)_source!,
                0,
                null,
                handle.AddrOfPinnedObject(),
                stride,
                0);
        }
        finally
        {
            handle.Free();
        }
    }

    private static byte[] ToBgra(BitmapSource source, out int stride)
    {
        BitmapSource bgra = source;
        if (source.Format != PixelFormats.Bgra32)
        {
            bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        }

        stride = bgra.PixelWidth * 4;
        var bytes = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(bytes, stride, 0);
        return bytes;
    }

    private byte[] Readback()
    {
        var box = _context.MapSubresource((SharpDX.Direct3D11.Resource)_staging!, 0, MapMode.Read, MapFlags.None);
        try
        {
            var rowBytes = _outputWidth * 4;
            for (var y = 0; y < _outputHeight; y++)
            {
                Marshal.Copy(box.DataPointer + (y * box.RowPitch), _pixels, y * rowBytes, rowBytes);
            }
        }
        finally
        {
            _context.UnmapSubresource(_staging!, 0);
        }

        return _pixels;
    }

    private void SetParams(EnhancementConfig config, int width, int height, double denoise, double deblock)
    {
        var strength = EnhancementConfig.StrengthFactor(config.Upscale);
        var p = new ShaderParams
        {
            P0 = new Vector4(
                (float)config.Brightness,
                (float)config.Contrast,
                (float)config.Saturation,
                (float)config.Vibrance),
            P1 = new Vector4(
                (float)config.Gamma,
                (float)config.Temperature,
                (float)config.BlackLevel,
                (float)config.ShadowDetail),
            P2 = new Vector4(
                (float)config.HighlightRecovery,
                (float)Math.Clamp(config.Sharpening * (0.55 + strength), 0, 1.5),
                (float)(config.DetailRecovery * Math.Max(strength, 0.35)),
                (float)config.TextureEnhancement),
            P3 = new Vector4(
                (float)denoise,
                (float)config.AntiAliasing,
                (float)deblock,
                config.Enabled ? 1 : 0),
            Width = (uint)width,
            Height = (uint)height,
            Texel = new Vector2(1f / width, 1f / height)
        };
        _context.UpdateSubresource(ref p, _params);
    }

    /// <summary>Approximate VRAM in use by this process' D3D device, in megabytes.</summary>
    public double QueryVramUsedMb()
    {
        try
        {
            using var dxgiDevice = _device.QueryInterface<SharpDX.DXGI.Device>();
            using var adapter = dxgiDevice.Adapter.QueryInterface<Adapter3>();
            var info = adapter.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
            return info.CurrentUsage / (1024.0 * 1024);
        }
        catch
        {
            return 0;
        }
    }

    private void ReleaseResources()
    {
        _staging?.Dispose();
        _outputUav?.Dispose();
        _outputView?.Dispose();
        _output?.Dispose();
        _work2Uav?.Dispose();
        _work2View?.Dispose();
        _work2?.Dispose();
        _workUav?.Dispose();
        _workView?.Dispose();
        _work?.Dispose();
        _sourceView?.Dispose();
        _source?.Dispose();
        _staging = null;
        _output = null;
        _outputView = null;
        _outputUav = null;
        _work2 = null;
        _work2View = null;
        _work2Uav = null;
        _work = null;
        _workView = null;
        _workUav = null;
        _source = null;
        _sourceView = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ReleaseResources();
            _sampler.Dispose();
            _t4?.Dispose();
            _t3?.Dispose();
            _t2?.Dispose();
            _t1?.Dispose();
            _t0?.Dispose();
            _disjoint?.Dispose();
            _params.Dispose();
            _copy.Dispose();
            _upscale.Dispose();
            _grade.Dispose();
            _denoise.Dispose();
            _context.Dispose();
            _device.Dispose();
        }
    }
}
