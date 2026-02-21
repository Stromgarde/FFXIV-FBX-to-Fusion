// NormalToDisplacement.cs
// Namespace: FFXIV_FBX_to_Fusion
//
// Converts a tangent-space normal map PNG -> 16-bit grayscale displacement/height map PNG.
// CPU port of NormalMapTest.cpp + Shaders.hlsl, with discrete-GPU acceleration via ILGPU.
//
// -- NuGet dependencies --------------------------------------------------------
//   SixLabors.ImageSharp  >= 3.0      (PNG I/O)
//   ILGPU                 >= 1.5.0    (GPU compute - CUDA, OpenCL, CPU backends)
//
//   dotnet add package SixLabors.ImageSharp
//   dotnet add package ILGPU
//
// -- GPU backend priority ------------------------------------------------------
//   1. CUDA  (NVIDIA)  -- requires CUDA Toolkit installed on host
//   2. OpenCL GPU      -- AMD / Intel / NVIDIA without CUDA Toolkit
//   3. CPU fallback    -- Parallel.For implementation (no GPU required)
//
// -- Edge mode semantics (v5.1) ------------------------------------------------
//   Free  (default): OOB sample references are ignored; edges can be any height.
//   Clamp:           All 20 neighbours always sampled; OOB height treated as 0.
//   Wrap:            Map repeats; OOB coordinates wrap to the other side.
//
// -- Channel mapping format ----------------------------------------------------
//   "XrYgZb"   standard RGB (default)
//   "XgYaZn"   X from green, Y from alpha, Z absent (treated as straight-up)
//   "XrYfgZn"  X from red, Y from green flipped, Z absent
//   Channels: r=red(0), g=green(1), b=blue(2), a=alpha(3)
//   Prefix 'f' before channel letter flips that component (multiply by -1).
//   'n' for Z = component absent; Z defaults to 1 (prevents sign-flip errors).
//
// -- Example: calling NormalConvert from your primary program ------------------
//
//   using FFXIV_FBX_to_Fusion;
//
//   class Program
//   {
//       static void Main(string[] args)
//       {
//           NormalToDisplacement.NormalConvert(
//               inputPath   : "mt_w2310b0002_a_n.png",
//               outputPath  : "displacement_map.png",
//               scale       : 1.00f,
//               numPasses   : 4096,
//               normalScale : 1.000f,
//               stepHeight  : 120f,
//               edgeMode    : EdgeMode.Free,
//               normalise   : true,
//               zRange      : ZRange.Half);
//       }
//   }
//
// -----------------------------------------------------------------------------
// Created largely by Claude.ai and derived from the following package:
// https://skgenius.co.uk/FileDump/NormalToHeight_v0.5.1.zip
// Discussed here:
// https://www.reddit.com/r/gamedev/comments/fffskm/convert_normal_map_to_displacement_map/

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ILGPU;
using ILGPU.Runtime;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FFXIV_FBX_to_Fusion
{
    // -------------------------------------------------------------------------
    // Supporting enums
    // -------------------------------------------------------------------------

    /// <summary>
    /// How out-of-bounds pixel accesses are handled (v5.1 semantics).
    /// Free  (default): OOB sample references are ignored; edges float freely.
    /// Clamp: All 20 neighbours always sampled; OOB height contribution = 0.
    /// Wrap:  Map is tiling; OOB coordinates wrap to the opposite edge.
    /// </summary>
    public enum EdgeMode { Free, Clamp, Wrap }

    /// <summary>
    /// Storage range of the Z (blue) channel in the normal map.
    /// Half:    Z in [0.5, 1.0] -- positive hemisphere (standard tangent space).
    /// Full:    Z in [0.0, 1.0] mapped to [-1, 1].
    /// Clamped: Same decode as Full but Z is clamped to [0, 1] after decode,
    ///          preventing the nz-less-than-0 sign-flip path from triggering.
    /// </summary>
    public enum ZRange { Half, Full, Clamped }

    // -------------------------------------------------------------------------
    // Channel mapping
    // -------------------------------------------------------------------------

    /// <summary>
    /// Describes how the RGBA channels of the normal map PNG map to the XYZ
    /// components of the encoded normal vector.
    ///
    /// Parse a mapping string like "XrYgZb", "XgYaZn", "XrYfgZn" via
    /// <see cref="Parse"/>, or use <see cref="Default"/> for standard RGB.
    ///
    /// Format: X[f?][r|g|b|a] Y[f?][r|g|b|a] Z[f?][r|g|b|a|n]
    ///   - 'f' prefix flips (negates) the component after decoding.
    ///   - 'n' for Z means the Z channel is absent; Z defaults to 1.0.
    /// </summary>
    public struct ChannelMapping
    {
        public int XChannel;   // 0=R, 1=G, 2=B, 3=A
        public bool XFlip;
        public int YChannel;
        public bool YFlip;
        public int ZChannel;   // 0-3, or -1 if absent ('n')
        public bool ZFlip;

        /// <summary>Standard XrYgZb mapping (red=X, green=Y, blue=Z).</summary>
        public static readonly ChannelMapping Default =
            new ChannelMapping { XChannel = 0, YChannel = 1, ZChannel = 2 };

        /// <summary>
        /// Parses a mapping descriptor string.
        /// Returns <see cref="Default"/> if the string is null, empty, or malformed.
        /// </summary>
        public static ChannelMapping Parse(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return Default;

            var m = Default;
            int pos = 0;

            // Each token: [X|Y|Z] [f?] [r|g|b|a|n]
            while (pos < s.Length)
            {
                char axis = char.ToUpperInvariant(s[pos]); pos++;
                if (axis != 'X' && axis != 'Y' && axis != 'Z') continue;

                bool flip = pos < s.Length && char.ToLowerInvariant(s[pos]) == 'f';
                if (flip) pos++;

                if (pos >= s.Length) break;
                char ch = char.ToLowerInvariant(s[pos]); pos++;
                int channel = ch == 'r' ? 0 : ch == 'g' ? 1 : ch == 'b' ? 2 : ch == 'a' ? 3 : -1;

                switch (axis)
                {
                    case 'X': m.XChannel = channel; m.XFlip = flip; break;
                    case 'Y': m.YChannel = channel; m.YFlip = flip; break;
                    case 'Z': m.ZChannel = channel; m.ZFlip = flip; break;
                }
            }
            return m;
        }
    }

    // -------------------------------------------------------------------------
    // Progress dialog
    // -------------------------------------------------------------------------

    /// <summary>
    /// Lightweight WinForms progress window on its own STA thread.
    /// Lifetime: Show() -> SetDevice() -> Report() (many) -> CloseAndWait().
    /// </summary>
    internal sealed class ProgressForm : Form
    {
        private readonly Label _lblDevice;
        private readonly Label _lblMip;
        private readonly Label _lblPass;
        private readonly ProgressBar _bar;
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);

        private ProgressForm()
        {
            Text = "Normal -> Displacement";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new System.Drawing.Size(440, 132);
            ControlBox = false;

            _lblDevice = new Label
            {
                AutoSize = true,
                Location = new System.Drawing.Point(12, 10),
                Text = "Initialising...",
                ForeColor = System.Drawing.Color.DimGray
            };
            _lblMip = new Label { AutoSize = true, Location = new System.Drawing.Point(12, 34), Text = "" };
            _lblPass = new Label { AutoSize = true, Location = new System.Drawing.Point(12, 56), Text = "" };
            _bar = new ProgressBar
            {
                Location = new System.Drawing.Point(12, 82),
                Size = new System.Drawing.Size(416, 28),
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Style = ProgressBarStyle.Continuous
            };
            Controls.AddRange(new Control[] { _lblDevice, _lblMip, _lblPass, _bar });
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); _ready.Set(); }

        public static new ProgressForm Show()
        {
            ProgressForm form = null;
            var created = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                Application.EnableVisualStyles();
                form = new ProgressForm();
                created.Set();
                Application.Run(form);
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            created.Wait();
            form._ready.Wait();
            return form;
        }

        public void SetDevice(string label)
        {
            if (IsDisposed) return;
            BeginInvoke(new Action(() => { if (!IsDisposed) _lblDevice.Text = label; }));
        }

        public void Report(int pass, int totalPasses, int mipIndex, int totalMips, int mipW, int mipH)
        {
            if (IsDisposed) return;
            int done = totalMips - mipIndex - 1;
            double pct = (done + (double)(pass + 1) / totalPasses) / totalMips * 100.0;
            int barVal = (int)Math.Clamp(pct, 0, 100);
            int disp = totalMips - mipIndex;
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed) return;
                _lblMip.Text = $"Mip level {disp} of {totalMips}  ({mipW}x{mipH})";
                _lblPass.Text = $"Pass {pass + 1:N0} of {totalPasses:N0}";
                _bar.Value = barVal;
            }));
        }

        public void CloseAndWait()
        {
            if (IsDisposed) return;
            BeginInvoke(new Action(() => { if (!IsDisposed) Close(); }));
        }
    }

    // -------------------------------------------------------------------------
    // GPU kernel methods
    // -------------------------------------------------------------------------

    /// <summary>
    /// ILGPU-compilable kernel entry points.
    ///
    /// Edge mode integer encoding used throughout GPU code:
    ///   0 = Free  -- only in-bounds (flagged) samples contribute
    ///   1 = Clamp -- all 20 samples always used; OOB height = 0
    ///   2 = Wrap  -- all 20 samples always used; OOB coords wrap
    ///
    /// Restrictions enforced to keep methods GPU-compilable:
    ///   - All methods are static; no C# static field access.
    ///   - No heap allocation; GPU memory via ArrayView only.
    ///   - Lookup tables expressed as if-else chains (foldable by JIT).
    ///   - tan(asin(x)) replaced by x/sqrt(1-x^2) to avoid trig intrinsics.
    /// </summary>
    internal static class GpuKernels
    {
        // Direction index constants -- match HLSL static const int Dir*
        private const int DirNorth = 0;
        private const int DirNorthEast = 1;
        private const int DirEast = 2;
        private const int DirSouthEast = 3;
        private const int DirSouth = 4;
        private const int DirSouthWest = 5;
        private const int DirWest = 6;
        private const int DirNorthWest = 7;

        private static void GetSampleOffset(int i, out int ox, out int oy)
        {
            ox = 0; oy = 0;
            if (i == 0) { ox = -1; oy = 0; return; }
            if (i == 1) { ox = 0; oy = -1; return; }
            if (i == 2) { ox = 1; oy = 0; return; }
            if (i == 3) { ox = 0; oy = 1; return; }
            if (i == 4) { ox = -1; oy = -1; return; }
            if (i == 5) { ox = 1; oy = 1; return; }
            if (i == 6) { ox = -1; oy = 1; return; }
            if (i == 7) { ox = 1; oy = -1; return; }
            if (i == 8) { ox = -2; oy = 0; return; }
            if (i == 9) { ox = 0; oy = -2; return; }
            if (i == 10) { ox = 2; oy = 0; return; }
            if (i == 11) { ox = 0; oy = 2; return; }
            if (i == 12) { ox = -1; oy = -2; return; }
            if (i == 13) { ox = 1; oy = -2; return; }
            if (i == 14) { ox = 2; oy = -1; return; }
            if (i == 15) { ox = 2; oy = 1; return; }
            if (i == 16) { ox = 1; oy = 2; return; }
            if (i == 17) { ox = -1; oy = 2; return; }
            if (i == 18) { ox = -2; oy = 1; return; }
            if (i == 19) { ox = -2; oy = -1; return; }
        }

        private static void GetDirVec(int dir, out float dx, out float dy)
        {
            dx = 0f; dy = 0f;
            if (dir == DirNorth) { dx = 0f; dy = -1f; return; }
            if (dir == DirNorthEast) { dx = -1f; dy = -1f; return; }
            if (dir == DirEast) { dx = -1f; dy = 0f; return; }
            if (dir == DirSouthEast) { dx = -1f; dy = 1f; return; }
            if (dir == DirSouth) { dx = 0f; dy = 1f; return; }
            if (dir == DirSouthWest) { dx = 1f; dy = 1f; return; }
            if (dir == DirWest) { dx = 1f; dy = 0f; return; }
            if (dir == DirNorthWest) { dx = 1f; dy = -1f; return; }
        }

        // ---- Normal sampling ------------------------------------------------

        /// <summary>
        /// Reads one float channel from the normal map ArrayView.
        ///
        /// edgeMode=0 (Free): caller guarantees coords are in-bounds.
        /// edgeMode=1 (Clamp): OOB returns 0.5 (decodes to 0 after *2-1),
        ///   yielding a zero normal component -- effectively flat/absent.
        /// edgeMode=2 (Wrap): coords are wrapped before access.
        /// </summary>
        private static float SampleCh(
            ArrayView<float> nm, int w, int h,
            int nx, int ny, int ch, int edgeMode)
        {
            // Clamp mode: OOB returns 0.5 so (*2-1) decodes to 0
            if (edgeMode == 1)
            {
                if (nx < 0 || nx >= w || ny < 0 || ny >= h) return 0.5f;
            }
            else if (edgeMode == 2) // Wrap
            {
                nx = ((nx % w) + w) % w;
                ny = ((ny % h) + h) % h;
            }
            // Free: caller only calls with in-bounds coords
            return nm[(ny * w + nx) * 4 + ch];
        }

        /// <summary>
        /// Decodes and normalises the normal at (nx, ny).
        /// Applies normalScale to XY only; Z is used for sign checking only.
        /// zRange: 0=Half, 1=Full, 2=Clamped (Z clamped to [0,1] after decode).
        /// </summary>
        private static void SampleNormal(
            ArrayView<float> nm, int w, int h,
            int nx, int ny, int edgeMode, float normalScale, int zRange,
            out float rnx, out float rny, out float rnz)
        {
            float rx = SampleCh(nm, w, h, nx, ny, 0, edgeMode) * 2f - 1f;
            float ry = SampleCh(nm, w, h, nx, ny, 1, edgeMode) * 2f - 1f;
            float rz = SampleCh(nm, w, h, nx, ny, 2, edgeMode) * 2f - 1f;

            // ZRange.Clamped: clamp Z to [0,1] before normalisation
            // prevents the nz<0 sign-flip from triggering
            if (zRange == 2 && rz < 0f) rz = 0f;

            float len = (float) Math.Sqrt(rx * rx + ry * ry + rz * rz);
            if (len > 1e-6f) { rx /= len; ry /= len; rz /= len; }

            rnx = rx * normalScale;
            rny = ry * normalScale;
            rnz = rz;
        }

        // ---- Per-sample delta -----------------------------------------------

        /// <summary>
        /// Computes one height delta from the normal at (px+dx, py+dy).
        /// Uses tan(asin(x)) = x/sqrt(1-x^2) to avoid GPU trig intrinsics.
        /// </summary>
        private static float OneDelta(
            ArrayView<float> nm, int w, int h,
            int px, int py, int dx, int dy, int dir,
            int edgeMode, float normalScale, float maxStepHeight, int zRange)
        {
            SampleNormal(nm, w, h, px + dx, py + dy, edgeMode, normalScale, zRange,
                out float nx, out float ny, out float nz);

            GetDirVec(dir, out float dvx, out float dvy);

            float dist = nx * dvx + ny * dvy;
            float xyLenSq = nx * nx + ny * ny;
            if (xyLenSq <= 0f) return 0f;

            float xyLen = (float) Math.Sqrt(xyLenSq);
            float clamped = xyLen > 1f ? 1f : xyLen;
            float underRoot = 1f - clamped * clamped;
            float tanAsin = underRoot < 1e-6f
                ? (dist < 0f ? -maxStepHeight : maxStepHeight)
                : clamped / (float) Math.Sqrt(underRoot);

            float delta = tanAsin * (dist / xyLen);
            if (delta > maxStepHeight) delta = maxStepHeight;
            if (delta < -maxStepHeight) delta = -maxStepHeight;
            if (nz < 0f) delta = -delta;
            return delta;
        }

        // ---- Kernel: ComputeDeltas ------------------------------------------

        /// <summary>
        /// GPU equivalent of GenerateDeltas pixel shader.
        /// One thread per pixel; writes 20 deltas and an enable-flags bitmask.
        ///
        /// edgeMode=0 (Free):  flag bit set only when sample is in-bounds.
        /// edgeMode=1 (Clamp): all 20 flags always set; OOB normals return 0
        ///                     from SampleCh (yielding zero delta contribution).
        /// edgeMode=2 (Wrap):  all 20 flags always set; coords wrap.
        /// </summary>
        public static void ComputeDeltasKernel(
            Index1D pixelIdx,
            ArrayView<float> normals,
            ArrayView<float> deltas,
            ArrayView<uint> enableFlags,
            int w, int h,
            int edgeMode,
            float normalScale,
            float maxStepHeight,
            int zRange)
        {
            if (pixelIdx >= w * h) return;

            int px = (int)pixelIdx % w;
            int py = (int)pixelIdx / w;
            // For Clamp and Wrap modes all flags are set; Free only sets in-bounds
            bool allSamples = (edgeMode != 0);

            uint f = 0u;
            int db = (int)pixelIdx * 20;

            // Samples 0-3: cardinal distance-1
            if (allSamples || px > 1)
            {
                deltas[db + 0] = OneDelta(normals, w, h, px, py, -1, 0, DirEast, edgeMode, normalScale, maxStepHeight, zRange); f |= 1u << 0;
            }
            if (allSamples || py > 1)
            {
                deltas[db + 1] = OneDelta(normals, w, h, px, py, 0, -1, DirSouth, edgeMode, normalScale, maxStepHeight, zRange); f |= 1u << 1;
            }
            if (allSamples || px < w - 1)
            {
                deltas[db + 2] = OneDelta(normals, w, h, px, py, 0, 0, DirWest, edgeMode, normalScale, maxStepHeight, zRange); f |= 1u << 2;
            }
            if (allSamples || py < h - 1)
            {
                deltas[db + 3] = OneDelta(normals, w, h, px, py, 0, 0, DirNorth, edgeMode, normalScale, maxStepHeight, zRange); f |= 1u << 3;
            }
            // Samples 4-7: diagonal distance-1
            if (allSamples || (px > 1 && py > 1))
            {
                deltas[db + 4] = OneDelta(normals, w, h, px, py, -1, -1, DirSouthEast, edgeMode, normalScale, maxStepHeight, zRange); f |= 1u << 4;
            }
            if (allSamples || (px < w - 1 && py < h - 1))
            {
                deltas[db + 5] = OneDelta(normals, w, h, px, py, 0, 0, DirNorthWest, edgeMode, normalScale, maxStepHeight, zRange); f |= 1u << 5;
            }
            if (allSamples || (px > 1 && py < h - 1))
            {
                deltas[db + 6] = OneDelta(normals, w, h, px, py, -1, 0, DirNorthEast, edgeMode, normalScale, maxStepHeight, zRange); f |= 1u << 6;
            }
            if (allSamples || (px < w - 1 && py > 1))
            {
                deltas[db + 7] = OneDelta(normals, w, h, px, py, 0, -1, DirSouthWest, edgeMode, normalScale, maxStepHeight, zRange); f |= 1u << 7;
            }
            // Samples 8-11: cardinal distance-2
            if (allSamples || px > 2)
            {
                deltas[db + 8] = OneDelta(normals, w, h, px, py, -2, 0, DirEast, edgeMode, normalScale, maxStepHeight, zRange)
                                + OneDelta(normals, w, h, px, py, -1, 0, DirEast, edgeMode, normalScale, maxStepHeight, zRange);
                f |= 1u << 8;
            }
            if (allSamples || py > 2)
            {
                deltas[db + 9] = OneDelta(normals, w, h, px, py, 0, -2, DirSouth, edgeMode, normalScale, maxStepHeight, zRange)
                                + OneDelta(normals, w, h, px, py, 0, -1, DirSouth, edgeMode, normalScale, maxStepHeight, zRange);
                f |= 1u << 9;
            }
            if (allSamples || px < w - 2)
            {
                deltas[db + 10] = OneDelta(normals, w, h, px, py, 1, 0, DirWest, edgeMode, normalScale, maxStepHeight, zRange)
                                + OneDelta(normals, w, h, px, py, 0, 0, DirWest, edgeMode, normalScale, maxStepHeight, zRange);
                f |= 1u << 10;
            }
            if (allSamples || py < h - 2)
            {
                deltas[db + 11] = OneDelta(normals, w, h, px, py, 0, 1, DirNorth, edgeMode, normalScale, maxStepHeight, zRange)
                                + OneDelta(normals, w, h, px, py, 0, 0, DirNorth, edgeMode, normalScale, maxStepHeight, zRange);
                f |= 1u << 11;
            }
            // Samples 12-19: knight-like (4 deltas * 0.5)
            if (allSamples || (px > 1 && py > 2))
            {
                deltas[db + 12] = (OneDelta(normals, w, h, px, py, -1, -2, DirSouthEast, edgeMode, normalScale, maxStepHeight, zRange) + OneDelta(normals, w, h, px, py, 0, -1, DirSouth, edgeMode, normalScale, maxStepHeight, zRange) + OneDelta(normals, w, h, px, py, -1, -2, DirSouth, edgeMode, normalScale, maxStepHeight, zRange) + OneDelta(normals, w, h, px, py, -1, -1, DirSouthEast, edgeMode, normalScale, maxStepHeight, zRange)) * 0.5f; f |= 1u << 12;
            }
            if (allSamples || (px < w - 1 && py > 2))
            {
                deltas[db + 13] = (OneDelta(normals, w, h, px, py, 0, -2, DirSouthWest, edgeMode, normalScale, maxStepHeight, zRange) + OneDelta(normals, w, h, px, py, 0, -1, DirSouth, edgeMode, normalScale, maxStepHeight, zRange) + OneDelta(normals, w, h, px, py, 1, -2, DirSouth, edgeMode, normalScale, maxStepHeight, zRange) + OneDelta(normals, w, h, px, py, 0, -1, DirSouthWest, edgeMode, normalScale, maxStepHeight, zRange)) * 0.5f; f |= 1u << 13;
            }
            if (allSamples || (px < w - 2 && py > 1))
            {
                deltas[db + 14] = (OneDelta(normals, w, h, px, py, 1, -1, DirSouthWest, edgeMode, normalScale, maxStepHeight, zRange) + OneDelta(normals, w, h, px, py, 0, 0, DirWest, edgeMode, normalScale, maxStepHeight, zRange) + OneDelta(normals, w, h, px, py, 1, -1, DirWest, edgeMode, normalScale, maxStepHeight, zRange) + OneDelta(normals, w, h, px, py, 0, -1, DirSouthWest, edgeMode, normalScale, maxStepHeight, zRange)) * 0.5f; f |= 1u << 14;
            }
            if (allSamples || (px < w - 2 && py < h - 1))
            {
                deltas[db + 15] = (OneDelta(normals, w, h, px, py, 1, 0, DirNorthWest, edgeMode, normalScale, maxStepHeight, zRange) + OneDelta(normals, w, h, px, py, 0, 0, DirWest, edgeMode, normalScale, maxStepHeight, zRange) + OneDelta(normals, w, h, px, py, 1, 1, DirWest, edgeMode, normalScale, maxStepHeight, zRange) + OneDelta(normals, w, h, px, py, 0, 0, DirNorthWest, edgeMode, normalScale, maxStepHeight, zRange)) * 0.5f; f |= 1u << 15;
            }
            if (allSamples || (px < w - 1 && py < h - 2))
            {
                deltas[db + 16] = (OneDelta(normals, w, h, px, py, 0, 1, DirNorthWest, edgeMode, normalScale, maxStepHeight, zRange) + OneDelta(normals, w, h, px, py, 0, 0, DirNorth, edgeMode, normalScale, maxStepHeight, zRange) + OneDelta(normals, w, h, px, py, 1, 1, DirNorth, edgeMode, normalScale, maxStepHeight, zRange) + OneDelta(normals, w, h, px, py, 0, 0, DirNorthWest, edgeMode, normalScale, maxStepHeight, zRange)) * 0.5f; f |= 1u << 16;
            }
            if (allSamples || (px > 1 && py < h - 2))
            {
                deltas[db + 17] = (OneDelta(normals, w, h, px, py, -1, 1, DirNorthEast, edgeMode, normalScale, maxStepHeight, zRange) + OneDelta(normals, w, h, px, py, 0, 0, DirNorth, edgeMode, normalScale, maxStepHeight, zRange) + OneDelta(normals, w, h, px, py, -1, 1, DirNorth, edgeMode, normalScale, maxStepHeight, zRange) + OneDelta(normals, w, h, px, py, -1, 0, DirNorthEast, edgeMode, normalScale, maxStepHeight, zRange)) * 0.5f; f |= 1u << 17;
            }
            if (allSamples || (px > 2 && py < h - 1))
            {
                deltas[db + 18] = (OneDelta(normals, w, h, px, py, -2, 0, DirNorthEast, edgeMode, normalScale, maxStepHeight, zRange) + OneDelta(normals, w, h, px, py, -1, 0, DirEast, edgeMode, normalScale, maxStepHeight, zRange) + OneDelta(normals, w, h, px, py, -2, 1, DirEast, edgeMode, normalScale, maxStepHeight, zRange) + OneDelta(normals, w, h, px, py, -1, 0, DirNorthEast, edgeMode, normalScale, maxStepHeight, zRange)) * 0.5f; f |= 1u << 18;
            }
            if (allSamples || (px > 2 && py > 1))
            {
                deltas[db + 19] = (OneDelta(normals, w, h, px, py, -2, -1, DirSouthEast, edgeMode, normalScale, maxStepHeight, zRange) + OneDelta(normals, w, h, px, py, -1, 0, DirEast, edgeMode, normalScale, maxStepHeight, zRange) + OneDelta(normals, w, h, px, py, -2, -1, DirEast, edgeMode, normalScale, maxStepHeight, zRange) + OneDelta(normals, w, h, px, py, -1, -1, DirSouthEast, edgeMode, normalScale, maxStepHeight, zRange)) * 0.5f; f |= 1u << 19;
            }

            enableFlags[(int)pixelIdx] = f;
        }

        // ---- Kernel: UpdateHeights ------------------------------------------

        /// <summary>
        /// GPU equivalent of UpdateHeights pixel shader.  One thread per pixel per pass.
        ///
        /// Free  (0): numSamples = popcount(flags); only flagged samples used.
        /// Clamp (1): numSamples = 20; OOB height = 0 (not boundary-clamped).
        /// Wrap  (2): numSamples = 20; coords wrap (all in-bounds after wrap).
        /// </summary>
        public static void UpdateHeightsKernel(
            Index1D pixelIdx,
            ArrayView<float> src,
            ArrayView<float> dst,
            ArrayView<float> deltas,
            ArrayView<uint> enableFlags,
            int w, int h,
            int edgeMode)
        {
            if (pixelIdx >= w * h) return;

            int px = (int)pixelIdx % w;
            int py = (int)pixelIdx / w;
            uint f = enableFlags[(int)pixelIdx];
            int n = (edgeMode == 0) ? Popcnt32(f) : 20;

            if (n == 0) { dst[(int)pixelIdx] = src[(int)pixelIdx]; return; }

            double height = 0.0;
            double scale = 1.0 / n;
            int di = (int)pixelIdx * 20;

            for (int i = 0; i < 20; i++)
            {
                // Free mode: skip unflagged (OOB) samples
                if (edgeMode == 0 && (f & (1u << i)) == 0) continue;

                GetSampleOffset(i, out int ox, out int oy);
                int nx = px + ox, ny = py + oy;

                float neighbourHeight;
                if (edgeMode == 1) // Clamp: OOB = 0 height
                {
                    neighbourHeight = (nx >= 0 && nx < w && ny >= 0 && ny < h)
                        ? src[ny * w + nx]
                        : 0f;
                }
                else if (edgeMode == 2) // Wrap
                {
                    nx = ((nx % w) + w) % w;
                    ny = ((ny % h) + h) % h;
                    neighbourHeight = src[ny * w + nx];
                }
                else // Free: flagged => in-bounds
                {
                    neighbourHeight = src[ny * w + nx];
                }

                height += (neighbourHeight + deltas[di + i]) * scale;
            }

            dst[(int)pixelIdx] = (float)height;
        }

        // ---- Kernel: BilinearDownsample4Ch ----------------------------------

        public static void BilinearDownsample4ChKernel(
            Index1D dstIdx,
            ArrayView<float> src,
            ArrayView<float> dst,
            int srcW, int srcH,
            int dstW, int dstH)
        {
            if (dstIdx >= dstW * dstH) return;
            int dx = (int)dstIdx % dstW, dy = (int)dstIdx / dstW;
            float scX = (float)srcW / dstW, scY = (float)srcH / dstH;
            float sx = (dx + 0.5f) * scX - 0.5f, sy = (dy + 0.5f) * scY - 0.5f;
            int x0 = (int)sx; if (x0 < 0) x0 = 0; if (x0 >= srcW) x0 = srcW - 1;
            int y0 = (int)sy; if (y0 < 0) y0 = 0; if (y0 >= srcH) y0 = srcH - 1;
            int x1 = x0 + 1; if (x1 >= srcW) x1 = srcW - 1;
            int y1 = y0 + 1; if (y1 >= srcH) y1 = srcH - 1;
            float fx = sx - x0; if (fx < 0f) fx = 0f; if (fx > 1f) fx = 1f;
            float fy = sy - y0; if (fy < 0f) fy = 0f; if (fy > 1f) fy = 1f;
            int db = (int)dstIdx * 4;
            for (int c = 0; c < 4; c++)
            {
                dst[db + c] = src[(y0 * srcW + x0) * 4 + c] * (1 - fx) * (1 - fy) + src[(y0 * srcW + x1) * 4 + c] * fx * (1 - fy)
                         + src[(y1 * srcW + x0) * 4 + c] * (1 - fx) * fy + src[(y1 * srcW + x1) * 4 + c] * fx * fy;
            }
        }

        // ---- Kernel: BilinearUpsample1ChX2 ----------------------------------

        public static void BilinearUpsample1ChX2Kernel(
            Index1D dstIdx,
            ArrayView<float> src,
            ArrayView<float> dst,
            int srcW, int srcH,
            int dstW, int dstH)
        {
            if (dstIdx >= dstW * dstH) return;
            int dx = (int)dstIdx % dstW, dy = (int)dstIdx / dstW;
            float scX = (float)srcW / dstW, scY = (float)srcH / dstH;
            float sx = (dx + 0.5f) * scX - 0.5f, sy = (dy + 0.5f) * scY - 0.5f;
            int x0 = (int)sx; if (x0 < 0) x0 = 0; if (x0 >= srcW) x0 = srcW - 1;
            int y0 = (int)sy; if (y0 < 0) y0 = 0; if (y0 >= srcH) y0 = srcH - 1;
            int x1 = x0 + 1; if (x1 >= srcW) x1 = srcW - 1;
            int y1 = y0 + 1; if (y1 >= srcH) y1 = srcH - 1;
            float fx = sx - x0; if (fx < 0f) fx = 0f; if (fx > 1f) fx = 1f;
            float fy = sy - y0; if (fy < 0f) fy = 0f; if (fy > 1f) fy = 1f;
            dst[(int)dstIdx] = (src[y0 * srcW + x0] * (1 - fx) * (1 - fy) + src[y0 * srcW + x1] * fx * (1 - fy)
                             + src[y1 * srcW + x0] * (1 - fx) * fy + src[y1 * srcW + x1] * fx * fy) * 2f;
        }

        // ---- Kernel: ApplyMask ----------------------------------------------

        /// <summary>
        /// Zeroes any height pixel where the mask value is below 0.5.
        /// Applied once after all passes complete.
        /// </summary>
        public static void ApplyMaskKernel(
            Index1D pixelIdx,
            ArrayView<float> heights,
            ArrayView<float> mask)
        {
            if (pixelIdx >= heights.Length) return;
            if (mask[(int)pixelIdx] < 0.5f) heights[(int)pixelIdx] = 0f;
        }

        // ---- Utility --------------------------------------------------------

        private static int Popcnt32(uint v)
        {
            v -= (v >> 1) & 0x55555555u;
            v = (v & 0x33333333u) + ((v >> 2) & 0x33333333u);
            return (int)(((v + (v >> 4)) & 0x0f0f0f0fu) * 0x01010101u >> 24);
        }
    }

    // -------------------------------------------------------------------------
    // GPU pipeline manager
    // -------------------------------------------------------------------------

    /// <summary>
    /// Manages the ILGPU context, accelerator, and compiled kernel delegates.
    /// Create via TryCreate(); returns null if no discrete GPU is available.
    /// </summary>
    internal sealed class GpuPipeline : IDisposable
    {
        private readonly Context _ctx;
        private readonly Accelerator _acc;

        private readonly Action<Index1D,
            ArrayView<float>, ArrayView<float>, ArrayView<uint>,
            int, int, int, float, float, int>
            _computeDeltas;

        private readonly Action<Index1D,
            ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<uint>,
            int, int, int>
            _updateHeights;

        private readonly Action<Index1D,
            ArrayView<float>, ArrayView<float>,
            int, int, int, int>
            _downsample4Ch;

        private readonly Action<Index1D,
            ArrayView<float>, ArrayView<float>,
            int, int, int, int>
            _upsample1ChX2;

        private readonly Action<Index1D,
            ArrayView<float>, ArrayView<float>>
            _applyMask;

        public string DeviceName { get; }

        private GpuPipeline(Context ctx, Accelerator acc, string name)
        {
            _ctx = ctx; _acc = acc; DeviceName = name;

            _computeDeltas = acc.LoadAutoGroupedStreamKernel<
                Index1D, ArrayView<float>, ArrayView<float>, ArrayView<uint>,
                int, int, int, float, float, int>(GpuKernels.ComputeDeltasKernel);

            _updateHeights = acc.LoadAutoGroupedStreamKernel<
                Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<uint>,
                int, int, int>(GpuKernels.UpdateHeightsKernel);

            _downsample4Ch = acc.LoadAutoGroupedStreamKernel<
                Index1D, ArrayView<float>, ArrayView<float>,
                int, int, int, int>(GpuKernels.BilinearDownsample4ChKernel);

            _upsample1ChX2 = acc.LoadAutoGroupedStreamKernel<
                Index1D, ArrayView<float>, ArrayView<float>,
                int, int, int, int>(GpuKernels.BilinearUpsample1ChX2Kernel);

            _applyMask = acc.LoadAutoGroupedStreamKernel<
                Index1D, ArrayView<float>, ArrayView<float>>(GpuKernels.ApplyMaskKernel);
        }

        public static GpuPipeline TryCreate()
        {
            Context ctx = null;
            try
            {
                ctx = Context.CreateDefault();

                // CUDA (NVIDIA) first
                foreach (var device in ctx.Devices)
                {
                    if (device.AcceleratorType != AcceleratorType.Cuda) continue;
                    try
                    {
                        var acc = device.CreateAccelerator(ctx);
                        Console.WriteLine($"[GpuPipeline] CUDA: {device.Name}");
                        return new GpuPipeline(ctx, acc, $"CUDA - {device.Name}");
                    }
                    catch (Exception ex) { Console.WriteLine($"[GpuPipeline] CUDA failed: {ex.Message}"); }
                }

                // OpenCL GPU second
                foreach (var device in ctx.Devices)
                {
                    if (device.AcceleratorType != AcceleratorType.OpenCL) continue;
                    if (device.Name.IndexOf("CPU", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    try
                    {
                        var acc = device.CreateAccelerator(ctx);
                        Console.WriteLine($"[GpuPipeline] OpenCL: {device.Name}");
                        return new GpuPipeline(ctx, acc, $"OpenCL - {device.Name}");
                    }
                    catch (Exception ex) { Console.WriteLine($"[GpuPipeline] OpenCL failed: {ex.Message}"); }
                }

                Console.WriteLine("[GpuPipeline] No discrete GPU found. Using CPU fallback.");
                ctx.Dispose(); return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GpuPipeline] Init failed: {ex.Message}");
                ctx?.Dispose(); return null;
            }
        }

        // ---- GPU pipeline stages --------------------------------------------

        public void ComputeDeltas(
            float[] normals, int w, int h,
            int edgeMode, float normalScale, float maxStepHeight, int zRange,
            float[] deltasOut, uint[] flagsOut)
        {
            int pixels = w * h;
            using var gpuNormals = _acc.Allocate1D<float>(normals.Length);
            using var gpuDeltas = _acc.Allocate1D<float>((long)pixels * 20);
            using var gpuFlags = _acc.Allocate1D<uint>(pixels);
            gpuNormals.CopyFromCPU(normals);
            gpuDeltas.MemSetToZero();
            _computeDeltas(pixels, gpuNormals.View, gpuDeltas.View, gpuFlags.View,
                           w, h, edgeMode, normalScale, maxStepHeight, zRange);
            _acc.Synchronize();
            gpuDeltas.CopyToCPU(deltasOut);
            gpuFlags.CopyToCPU(flagsOut);
        }

        public float[] RunPasses(
            float[] initial, float[] deltas, uint[] flags,
            int w, int h, int numPasses, int edgeMode,
            float[] mask,
            ProgressForm progress, int mipIndex, int totalMips)
        {
            int pixels = w * h;
            using var gpuDeltas = _acc.Allocate1D<float>((long)pixels * 20);
            using var gpuFlags = _acc.Allocate1D<uint>(pixels);
            using var gpuBufA = _acc.Allocate1D<float>(pixels);
            using var gpuBufB = _acc.Allocate1D<float>(pixels);
            gpuDeltas.CopyFromCPU(deltas);
            gpuFlags.CopyFromCPU(flags);
            gpuBufA.CopyFromCPU(initial);

            var src = gpuBufA;
            var dst = gpuBufB;

            for (int pass = 0; pass < numPasses; pass++)
            {
                progress.Report(pass, numPasses, mipIndex, totalMips, w, h);
                _updateHeights(pixels, src.View, dst.View, gpuDeltas.View, gpuFlags.View, w, h, edgeMode);
                _acc.Synchronize();
                (src, dst) = (dst, src);
            }

            // Apply mask after all passes (GPU)
            if (mask != null)
            {
                using var gpuMask = _acc.Allocate1D<float>(pixels);
                gpuMask.CopyFromCPU(mask);
                _applyMask(pixels, src.View, gpuMask.View);
                _acc.Synchronize();
            }

            return src.GetAsArray1D();
        }

        public float[] BilinearDownsample4Ch(float[] src, int srcW, int srcH, int dstW, int dstH)
        {
            using var gpuSrc = _acc.Allocate1D<float>(src.Length);
            using var gpuDst = _acc.Allocate1D<float>(dstW * dstH * 4);
            gpuSrc.CopyFromCPU(src);
            _downsample4Ch(dstW * dstH, gpuSrc.View, gpuDst.View, srcW, srcH, dstW, dstH);
            _acc.Synchronize();
            return gpuDst.GetAsArray1D();
        }

        public float[] BilinearUpsample1ChX2(float[] src, int srcW, int srcH, int dstW, int dstH)
        {
            using var gpuSrc = _acc.Allocate1D<float>(src.Length);
            using var gpuDst = _acc.Allocate1D<float>(dstW * dstH);
            gpuSrc.CopyFromCPU(src);
            _upsample1ChX2(dstW * dstH, gpuSrc.View, gpuDst.View, srcW, srcH, dstW, dstH);
            _acc.Synchronize();
            return gpuDst.GetAsArray1D();
        }

        public void Dispose() { _acc?.Dispose(); _ctx?.Dispose(); }
    }

    // -------------------------------------------------------------------------
    // Main converter
    // -------------------------------------------------------------------------

    /// <summary>
    /// Converts a tangent-space normal map to a 16-bit grayscale displacement map.
    /// Incorporates improvements reverse-engineered from NormalToHeight.exe v5.1.
    /// Automatically uses a discrete GPU (CUDA/OpenCL) when available.
    /// </summary>
    public static class NormalToDisplacement
    {
        // Direction index constants (CPU-side mirror of GpuKernels constants)
        private const int DirNorth = 0;
        private const int DirNorthEast = 1;
        private const int DirEast = 2;
        private const int DirSouthEast = 3;
        private const int DirSouth = 4;
        private const int DirSouthWest = 5;
        private const int DirWest = 6;
        private const int DirNorthWest = 7;

        private static readonly (float x, float y)[] DirVec =
        {
            ( 0f,-1f),(-1f,-1f),(-1f, 0f),(-1f, 1f),
            ( 0f, 1f),( 1f, 1f),( 1f, 0f),( 1f,-1f)
        };

        private static readonly (int x, int y)[] SampleOffsets =
        {
            (-1,0),(0,-1),(1,0),(0,1),
            (-1,-1),(1,1),(-1,1),(1,-1),
            (-2,0),(0,-2),(2,0),(0,2),
            (-1,-2),(1,-2),(2,-1),(2,1),
            (1,2),(-1,2),(-2,1),(-2,-1)
        };

        // Per-call state
        private static float s_maxStepHeight;
        private static float s_normalScale;
        private static bool s_normalise;
        private static float s_scale;
        private static int s_numPasses;
        private static int s_edgeModeInt;   // 0=Free,1=Clamp,2=Wrap
        private static ZRange s_zRange;
        private static ChannelMapping s_mapping;

        // ---- Public entry point ---------------------------------------------

        /// <summary>
        /// Converts a normal map to a 16-bit grayscale displacement map PNG.
        ///
        /// New parameters vs earlier version:
        ///   mask:      Optional path to a grayscale PNG. Black pixels force height=0.
        ///   mapping:   Channel mapping string e.g. "XrYgZb" (default), "XgYaZn".
        ///   outputRaw: If true, saves un-normalised float values scaled only by
        ///              the diagonal length, ignoring normalise/scale. Equivalent
        ///              to the -OutputRaw flag in NormalToHeight.exe v5.1.
        /// </summary>
        public static void NormalConvert(
            string inputPath,
            string outputPath,
            float scale = 1.0f,
            int numPasses = 4096,
            float normalScale = 1.0f,
            float stepHeight = 75.0f,
            EdgeMode edgeMode = EdgeMode.Free,
            bool normalise = true,
            ZRange zRange = ZRange.Half,
            string mask = null,
            string mapping = null,
            bool outputRaw = false)
        {
            s_scale = scale;
            s_numPasses = numPasses;
            s_normalScale = normalScale;
            s_maxStepHeight = stepHeight;
            s_normalise = normalise;
            s_zRange = zRange;
            s_mapping = ChannelMapping.Parse(mapping);

            // Encode edge mode as int: 0=Free, 1=Clamp, 2=Wrap
            s_edgeModeInt = edgeMode == EdgeMode.Clamp ? 1
                          : edgeMode == EdgeMode.Wrap ? 2
                          : 0;

            int zRangeInt = zRange == ZRange.Full ? 1
                          : zRange == ZRange.Clamped ? 2
                          : 0;

            using GpuPipeline gpu = GpuPipeline.TryCreate();
            bool usingGpu = gpu != null;
            string backend = usingGpu ? gpu.DeviceName : "CPU (Parallel.For)";

            Console.WriteLine($"[NTD] Backend  : {backend}");
            Console.WriteLine($"[NTD] EdgeMode : {edgeMode}");
            Console.WriteLine($"[NTD] Loading  : {inputPath}");

            LoadNormalMap(inputPath, out float[] rawNormals, out int imgW, out int imgH);
            Console.WriteLine($"[NTD] Image    : {imgW} x {imgH}");

            // Load optional mask (grayscale, same dimensions as input)
            float[] maskData = null;
            if (!string.IsNullOrEmpty(mask))
            {
                LoadMask(mask, imgW, imgH, out maskData);
                Console.WriteLine($"[NTD] Mask     : {mask}");
            }

            int numMips = 0, totalMips;
            { int me = Math.Min(imgW, imgH); while (me > 256) { me /= 2; numMips++; } }
            totalMips = numMips + 1;
            Console.WriteLine($"[NTD] Mip levels: {totalMips}");

            float[] prevHeights = null;
            int prevW = 0, prevH = 0;

            using var progress = ProgressForm.Show();
            progress.SetDevice(backend);

            for (int mip = numMips; mip >= 0; mip--)
            {
                int step = 1 << mip;
                int mipW = imgW / step;
                int mipH = imgH / step;
                Console.WriteLine($"[NTD]   Mip {mip}: {mipW}x{mipH}");

                // Downsample normals to mip resolution
                float[] mipNormals;
                if (mip == 0)
                    mipNormals = rawNormals;
                else if (usingGpu)
                    mipNormals = gpu.BilinearDownsample4Ch(rawNormals, imgW, imgH, mipW, mipH);
                else
                    mipNormals = CpuBilinearDownsample4Ch(rawNormals, imgW, imgH, mipW, mipH);

                // Downsample mask to mip resolution if present
                float[] mipMask = null;
                if (maskData != null)
                {
                    mipMask = mip == 0
                        ? maskData
                        : CpuBilinearDownsample1Ch(maskData, imgW, imgH, mipW, mipH);
                }

                // Initialise height map
                float[] heights;
                if (prevHeights == null)
                    heights = InitSweep(mipNormals, mipW, mipH);
                else if (usingGpu)
                    heights = gpu.BilinearUpsample1ChX2(prevHeights, prevW, prevH, mipW, mipH);
                else
                    heights = CpuBilinearUpsample1Ch_x2(prevHeights, prevW, prevH, mipW, mipH);

                // Compute deltas
                var deltas = new float[mipW * mipH * 20];
                var enableFlags = new uint[mipW * mipH];

                if (usingGpu)
                    gpu.ComputeDeltas(mipNormals, mipW, mipH, s_edgeModeInt, normalScale, stepHeight, zRangeInt, deltas, enableFlags);
                else
                    CpuComputeDeltas(mipNormals, mipW, mipH, deltas, enableFlags, zRangeInt);

                // Iterative height propagation
                if (usingGpu)
                    heights = gpu.RunPasses(heights, deltas, enableFlags, mipW, mipH, numPasses, s_edgeModeInt, mipMask, progress, mip, totalMips);
                else
                    heights = CpuRunPasses(heights, deltas, enableFlags, mipW, mipH, mipMask, progress, mip, totalMips);

                prevHeights = heights;
                prevW = mipW;
                prevH = mipH;
            }

            progress.CloseAndWait();

            Console.WriteLine($"[NTD] Saving: {outputPath}");
            SaveHeightMap(prevHeights, prevW, prevH, outputPath, maskData, outputRaw);
            Console.WriteLine("[NTD] Done.");
        }

        // ---- PNG I/O --------------------------------------------------------

        /// <summary>
        /// Loads a normal map PNG into a flat RGBA float array applying the
        /// channel mapping specified in <see cref="s_mapping"/>.
        /// </summary>
        private static void LoadNormalMap(
            string path, out float[] data, out int w, out int h)
        {
            using var img = SixLabors.ImageSharp.Image.Load<Rgba32>(path);
            int lw = img.Width, lh = img.Height;
            float[] ld = new float[lw * lh * 4];
            var m = s_mapping;

            img.ProcessPixelRows(acc =>
            {
                for (int y = 0; y < lh; y++)
                {
                    var row = acc.GetRowSpan(y);
                    for (int x = 0; x < lw; x++)
                    {
                        // Read all four raw channels
                        float[] raw = {
                            row[x].R / 255f,
                            row[x].G / 255f,
                            row[x].B / 255f,
                            row[x].A / 255f
                        };

                        int i = (y * lw + x) * 4;

                        // Map X component
                        ld[i] = m.XChannel >= 0 ? (m.XFlip ? 1f - raw[m.XChannel] : raw[m.XChannel]) : 0.5f;
                        // Map Y component
                        ld[i + 1] = m.YChannel >= 0 ? (m.YFlip ? 1f - raw[m.YChannel] : raw[m.YChannel]) : 0.5f;
                        // Map Z component; absent ('n') -> 1.0 (straight up, no sign flip)
                        ld[i + 2] = m.ZChannel >= 0 ? (m.ZFlip ? 1f - raw[m.ZChannel] : raw[m.ZChannel]) : 1.0f;
                        ld[i + 3] = raw[3]; // alpha always passthrough
                    }
                }
            });

            w = lw; h = lh; data = ld;
        }

        /// <summary>
        /// Loads a grayscale mask PNG as a flat float array (one value per pixel).
        /// Values below 0.5 are considered masked (height = 0).
        /// If the mask dimensions differ from the image, it is resampled.
        /// </summary>
        private static void LoadMask(string path, int expectedW, int expectedH, out float[] data)
        {
            using var img = SixLabors.ImageSharp.Image.Load<L8>(path);
            int lw = img.Width, lh = img.Height;
            float[] ld = new float[lw * lh];

            img.ProcessPixelRows(acc =>
            {
                for (int y = 0; y < lh; y++)
                {
                    var row = acc.GetRowSpan(y);
                    for (int x = 0; x < lw; x++)
                        ld[y * lw + x] = row[x].PackedValue / 255f;
                }
            });

            // Resample if mask dimensions differ from the normal map
            if (lw != expectedW || lh != expectedH)
                ld = CpuBilinearDownsample1Ch(ld, lw, lh, expectedW, expectedH);

            data = ld;
        }

        /// <summary>
        /// Saves the height map as a 16-bit grayscale PNG.
        /// If mask is provided, masked pixels are zeroed before saving.
        /// outputRaw skips normalisation and uses diagonal-based scaling.
        /// </summary>
        private static void SaveHeightMap(
            float[] heights, int w, int h,
            string outputPath, float[] mask, bool outputRaw)
        {
            // Apply mask to final output (CPU path or after GPU pass)
            if (mask != null)
            {
                heights = (float[])heights.Clone();
                for (int i = 0; i < heights.Length; i++)
                    if (mask[i] < 0.5f) heights[i] = 0f;
            }

            float hMin = float.MaxValue, hMax = float.MinValue;
            foreach (float v in heights)
            {
                if (v < hMin) hMin = v;
                if (v > hMax) hMax = v;
            }
            float range = hMax - hMin;
            float diag = MathF.Sqrt((float)(w * w + h * h));

            using var img = new Image<L16>(w, h);
            img.ProcessPixelRows(acc =>
            {
                for (int y = 0; y < h; y++)
                {
                    var row = acc.GetRowSpan(y);
                    for (int x = 0; x < w; x++)
                    {
                        float val = heights[y * w + x] - hMin;
                        float out16;

                        if (outputRaw)
                            out16 = val * (65535f / diag);
                        else if (s_normalise)
                            out16 = range > 0f ? (val / range) * 65535f : 0f;
                        else
                            out16 = val * (65535f / diag) * s_scale;

                        row[x] = new L16((ushort)Math.Clamp((int)out16, 0, 65535));
                    }
                }
            });

            using var fs = File.Open(outputPath, FileMode.Create, FileAccess.Write);
            img.SaveAsPng(fs);
        }

        // ---- CPU: normal decoding -------------------------------------------

        /// <summary>
        /// Decodes the normal at (nx, ny) applying edge mode and ZRange.
        /// edgeModeInt: 0=Free (caller guarantees in-bounds),
        ///              1=Clamp (OOB returns 0.5 -> decodes to 0),
        ///              2=Wrap.
        /// </summary>
        private static (float x, float y, float z) SampleNormal(
            float[] normals, int w, int h, int nx, int ny)
        {
            if (s_edgeModeInt == 1) // Clamp: OOB -> 0.5 (decodes to 0)
            {
                if (nx < 0 || nx >= w || ny < 0 || ny >= h) return (0f, 0f, 1f);
            }
            else if (s_edgeModeInt == 2) // Wrap
            {
                nx = ((nx % w) + w) % w;
                ny = ((ny % h) + h) % h;
            }
            // Free: caller guarantees in-bounds

            int i = (ny * w + nx) * 4;
            float x = normals[i] * 2f - 1f;
            float y = normals[i + 1] * 2f - 1f;
            float z = normals[i + 2] * 2f - 1f;

            // ZRange.Clamped: prevent nz < 0 sign flip
            if (s_zRange == ZRange.Clamped && z < 0f) z = 0f;

            float len = MathF.Sqrt(x * x + y * y + z * z);
            if (len > 1e-6f) { x /= len; y /= len; z /= len; }
            return (x, y, z);
        }

        private static float GpuGetPixelDelta(
            float[] normals, int w, int h,
            int px, int py, int dx, int dy, int dir)
        {
            var (nx, ny, nz) = SampleNormal(normals, w, h, px + dx, py + dy);
            float sx = nx * s_normalScale, sy = ny * s_normalScale;
            float dist = sx * DirVec[dir].x + sy * DirVec[dir].y;
            float xyLen = Math.Clamp(MathF.Sqrt(sx * sx + sy * sy), 0f, 1f);
            if (xyLen <= 0f) return 0f;
            float delta = Math.Clamp(MathF.Tan(MathF.Asin(xyLen)) * (dist / xyLen),
                                     -s_maxStepHeight, s_maxStepHeight);
            if (nz < 0f) delta = -delta;
            return delta;
        }

        private static float CpuGetPixelDelta(
            float[] normals, int w, int h,
            int px, int py, float vecX, float vecY)
        {
            var (nx, ny, _) = SampleNormal(normals, w, h, px, py);
            float dist = nx * vecX + ny * vecY;
            float angDot = Math.Clamp(MathF.Sqrt(nx * nx + ny * ny), 0f, 1f);
            if (angDot <= 0f) return 0f;
            return Math.Clamp(MathF.Tan(MathF.Asin(angDot)) * (dist / angDot), -30f, 30f);
        }

        // ---- CPU: InitSweep -------------------------------------------------

        /// <summary>
        /// Four-directional sweep averaged to cancel edge bias artifacts.
        /// Each sweep seeds from a different corner so no edge is privileged.
        /// </summary>
        private static float[] InitSweep(float[] normals, int w, int h)
        {
            float[] a = SweepPass(normals, w, h, xFwd: true, yFwd: true);
            float[] b = SweepPass(normals, w, h, xFwd: false, yFwd: false);
            float[] c = SweepPass(normals, w, h, xFwd: true, yFwd: false);
            float[] d = SweepPass(normals, w, h, xFwd: false, yFwd: true);

            float[] result = new float[w * h];
            for (int i = 0; i < w * h; i++)
                result[i] = (a[i] + b[i] + c[i] + d[i]) * 0.25f;
            return result;
        }

        private static float[] SweepPass(float[] normals, int w, int h, bool xFwd, bool yFwd)
        {
            float[] hm = new float[w * h];
            int yStart = yFwd ? 0 : h - 1;
            int yEnd = yFwd ? h : -1;
            int yStep = yFwd ? 1 : -1;
            float xVec = xFwd ? -1f : 1f;
            float yVec = yFwd ? 1f : -1f;

            for (int py = yStart; py != yEnd; py += yStep)
            {
                int xStart = xFwd ? 0 : w - 1;
                int xEnd = xFwd ? w : -1;
                int xStep = xFwd ? 1 : -1;

                for (int px = xStart; px != xEnd; px += xStep)
                {
                    float sum = 0f; int cnt = 0;

                    int prevX = px - xStep;
                    if (prevX >= 0 && prevX < w)
                    {
                        sum += hm[py * w + prevX] + CpuGetPixelDelta(normals, w, h, px, py, xVec, 0f);
                        cnt++;
                    }

                    int prevY = py - yStep;
                    if (prevY >= 0 && prevY < h)
                    {
                        sum += hm[prevY * w + px] + CpuGetPixelDelta(normals, w, h, px, py, 0f, yVec);
                        cnt++;
                    }

                    hm[py * w + px] = cnt > 0 ? sum / cnt : 0f;
                }
            }
            return hm;
        }

        // ---- CPU: CpuComputeDeltas ------------------------------------------

        private static void CpuComputeDeltas(
            float[] normals, int w, int h,
            float[] deltas, uint[] flags, int zRangeInt)
        {
            // For Clamp and Wrap, all 20 samples are always included
            bool allSamples = (s_edgeModeInt != 0);

            Parallel.For(0, h, py =>
            {
                for (int px = 0; px < w; px++)
                {
                    int b = (py * w + px) * 20;
                    uint f = 0u;

                    void D1(int i, bool ok, int dx, int dy, int dir)
                    {
                        if (!allSamples && !ok) return;
                        deltas[b + i] = GpuGetPixelDelta(normals, w, h, px, py, dx, dy, dir);
                        f |= 1u << i;
                    }
                    void D2(int i, bool ok, int ax, int ay, int da, int bx, int by, int db2)
                    {
                        if (!allSamples && !ok) return;
                        deltas[b + i] = GpuGetPixelDelta(normals, w, h, px, py, ax, ay, da)
                                    + GpuGetPixelDelta(normals, w, h, px, py, bx, by, db2);
                        f |= 1u << i;
                    }
                    void D4h(int i, bool ok,
                             int ax, int ay, int da, int bx, int by, int db2,
                             int cx, int cy, int dc, int ex, int ey, int de)
                    {
                        if (!allSamples && !ok) return;
                        deltas[b + i] = (GpuGetPixelDelta(normals, w, h, px, py, ax, ay, da)
                                     + GpuGetPixelDelta(normals, w, h, px, py, bx, by, db2)
                                     + GpuGetPixelDelta(normals, w, h, px, py, cx, cy, dc)
                                     + GpuGetPixelDelta(normals, w, h, px, py, ex, ey, de)) * 0.5f;
                        f |= 1u << i;
                    }

                    D1(0, px > 1, -1, 0, DirEast);
                    D1(1, py > 1, 0, -1, DirSouth);
                    D1(2, px < w - 1, 0, 0, DirWest);
                    D1(3, py < h - 1, 0, 0, DirNorth);
                    D1(4, px > 1 && py > 1, -1, -1, DirSouthEast);
                    D1(5, px < w - 1 && py < h - 1, 0, 0, DirNorthWest);
                    D1(6, px > 1 && py < h - 1, -1, 0, DirNorthEast);
                    D1(7, px < w - 1 && py > 1, 0, -1, DirSouthWest);
                    D2(8, px > 2, -2, 0, DirEast, -1, 0, DirEast);
                    D2(9, py > 2, 0, -2, DirSouth, 0, -1, DirSouth);
                    D2(10, px < w - 2, 1, 0, DirWest, 0, 0, DirWest);
                    D2(11, py < h - 2, 0, 1, DirNorth, 0, 0, DirNorth);
                    D4h(12, px > 1 && py > 2, -1, -2, DirSouthEast, 0, -1, DirSouth, -1, -2, DirSouth, -1, -1, DirSouthEast);
                    D4h(13, px < w - 1 && py > 2, 0, -2, DirSouthWest, 0, -1, DirSouth, 1, -2, DirSouth, 0, -1, DirSouthWest);
                    D4h(14, px < w - 2 && py > 1, 1, -1, DirSouthWest, 0, 0, DirWest, 1, -1, DirWest, 0, -1, DirSouthWest);
                    D4h(15, px < w - 2 && py < h - 1, 1, 0, DirNorthWest, 0, 0, DirWest, 1, 1, DirWest, 0, 0, DirNorthWest);
                    D4h(16, px < w - 1 && py < h - 2, 0, 1, DirNorthWest, 0, 0, DirNorth, 1, 1, DirNorth, 0, 0, DirNorthWest);
                    D4h(17, px > 1 && py < h - 2, -1, 1, DirNorthEast, 0, 0, DirNorth, -1, 1, DirNorth, -1, 0, DirNorthEast);
                    D4h(18, px > 2 && py < h - 1, -2, 0, DirNorthEast, -1, 0, DirEast, -2, 1, DirEast, -1, 0, DirNorthEast);
                    D4h(19, px > 2 && py > 1, -2, -1, DirSouthEast, -1, 0, DirEast, -2, -1, DirEast, -1, -1, DirSouthEast);

                    flags[py * w + px] = f;
                }
            });
        }

        // ---- CPU: CpuRunPasses ----------------------------------------------

        private static float[] CpuRunPasses(
            float[] initial, float[] deltas, uint[] enableFlags,
            int w, int h,
            float[] mask,
            ProgressForm progress, int mipIndex, int totalMips)
        {
            float[] bufA = (float[])initial.Clone();
            float[] bufB = new float[w * h];
            float[] src = bufA, dst = bufB;

            for (int pass = 0; pass < s_numPasses; pass++)
            {
                progress.Report(pass, s_numPasses, mipIndex, totalMips, w, h);

                Parallel.For(0, h, py =>
                {
                    for (int px = 0; px < w; px++)
                    {
                        int idx = py * w + px;
                        uint f = enableFlags[idx];

                        // Free: only in-bounds (flagged) samples; Clamp/Wrap: all 20
                        int n = (s_edgeModeInt == 0) ? PopCount(f) : 20;
                        if (n == 0) { dst[idx] = src[idx]; continue; }

                        double height = 0.0, scale = 1.0 / n;
                        int di = idx * 20;

                        for (int i = 0; i < 20; i++)
                        {
                            // Free: skip unflagged
                            if (s_edgeModeInt == 0 && (f & (1u << i)) == 0) continue;

                            int nx = px + SampleOffsets[i].x;
                            int ny = py + SampleOffsets[i].y;

                            float neighbourH;
                            if (s_edgeModeInt == 1) // Clamp: OOB -> height 0
                            {
                                neighbourH = (nx >= 0 && nx < w && ny >= 0 && ny < h)
                                    ? src[ny * w + nx]
                                    : 0f;
                            }
                            else if (s_edgeModeInt == 2) // Wrap
                            {
                                nx = ((nx % w) + w) % w;
                                ny = ((ny % h) + h) % h;
                                neighbourH = src[ny * w + nx];
                            }
                            else // Free: flagged => guaranteed in-bounds
                            {
                                neighbourH = src[ny * w + nx];
                            }

                            height += (neighbourH + deltas[di + i]) * scale;
                        }

                        dst[idx] = (float)height;
                    }
                });

                (src, dst) = (dst, src);
            }

            // Apply mask after all passes
            if (mask != null)
            {
                for (int i = 0; i < src.Length; i++)
                    if (mask[i] < 0.5f) src[i] = 0f;
            }

            return src;
        }

        // ---- CPU: bilinear resampling ---------------------------------------

        private static float[] CpuBilinearDownsample4Ch(
            float[] src, int srcW, int srcH, int dstW, int dstH)
        {
            float[] dst = new float[dstW * dstH * 4];
            float scX = (float)srcW / dstW, scY = (float)srcH / dstH;
            Parallel.For(0, dstH, dy =>
            {
                for (int dx = 0; dx < dstW; dx++)
                {
                    float sx = (dx + 0.5f) * scX - 0.5f, sy = (dy + 0.5f) * scY - 0.5f;
                    int x0 = Math.Clamp((int)MathF.Floor(sx), 0, srcW - 1), y0 = Math.Clamp((int)MathF.Floor(sy), 0, srcH - 1);
                    int x1 = Math.Clamp(x0 + 1, 0, srcW - 1), y1 = Math.Clamp(y0 + 1, 0, srcH - 1);
                    float fx = Math.Clamp(sx - x0, 0f, 1f), fy = Math.Clamp(sy - y0, 0f, 1f);
                    int db = (dy * dstW + dx) * 4;
                    for (int c = 0; c < 4; c++)
                        dst[db + c] = src[(y0 * srcW + x0) * 4 + c] * (1 - fx) * (1 - fy) + src[(y0 * srcW + x1) * 4 + c] * fx * (1 - fy)
                                 + src[(y1 * srcW + x0) * 4 + c] * (1 - fx) * fy + src[(y1 * srcW + x1) * 4 + c] * fx * fy;
                }
            });
            return dst;
        }

        /// <summary>Bilinear downsample of a 1-channel float image (for mask resampling).</summary>
        private static float[] CpuBilinearDownsample1Ch(
            float[] src, int srcW, int srcH, int dstW, int dstH)
        {
            float[] dst = new float[dstW * dstH];
            float scX = (float)srcW / dstW, scY = (float)srcH / dstH;
            Parallel.For(0, dstH, dy =>
            {
                for (int dx = 0; dx < dstW; dx++)
                {
                    float sx = (dx + 0.5f) * scX - 0.5f, sy = (dy + 0.5f) * scY - 0.5f;
                    int x0 = Math.Clamp((int)MathF.Floor(sx), 0, srcW - 1), y0 = Math.Clamp((int)MathF.Floor(sy), 0, srcH - 1);
                    int x1 = Math.Clamp(x0 + 1, 0, srcW - 1), y1 = Math.Clamp(y0 + 1, 0, srcH - 1);
                    float fx = Math.Clamp(sx - x0, 0f, 1f), fy = Math.Clamp(sy - y0, 0f, 1f);
                    dst[dy * dstW + dx] = src[y0 * srcW + x0] * (1 - fx) * (1 - fy) + src[y0 * srcW + x1] * fx * (1 - fy)
                                   + src[y1 * srcW + x0] * (1 - fx) * fy + src[y1 * srcW + x1] * fx * fy;
                }
            });
            return dst;
        }

        private static float[] CpuBilinearUpsample1Ch_x2(
            float[] src, int srcW, int srcH, int dstW, int dstH)
        {
            float[] dst = new float[dstW * dstH];
            float scX = (float)srcW / dstW, scY = (float)srcH / dstH;
            Parallel.For(0, dstH, dy =>
            {
                for (int dx = 0; dx < dstW; dx++)
                {
                    float sx = (dx + 0.5f) * scX - 0.5f, sy = (dy + 0.5f) * scY - 0.5f;
                    int x0 = Math.Clamp((int)MathF.Floor(sx), 0, srcW - 1), y0 = Math.Clamp((int)MathF.Floor(sy), 0, srcH - 1);
                    int x1 = Math.Clamp(x0 + 1, 0, srcW - 1), y1 = Math.Clamp(y0 + 1, 0, srcH - 1);
                    float fx = Math.Clamp(sx - x0, 0f, 1f), fy = Math.Clamp(sy - y0, 0f, 1f);
                    dst[dy * dstW + dx] = (src[y0 * srcW + x0] * (1 - fx) * (1 - fy) + src[y0 * srcW + x1] * fx * (1 - fy)
                                   + src[y1 * srcW + x0] * (1 - fx) * fy + src[y1 * srcW + x1] * fx * fy) * 2f;
                }
            });
            return dst;
        }

        // ---- Utility --------------------------------------------------------

        private static int PopCount(uint v) { int c = 0; while (v != 0) { v &= v - 1; c++; } return c; }
    }
}