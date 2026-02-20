// NormalToDisplacement.cs
// Namespace: FFXIV_FBX_to_Fusion
//
// Converts a tangent-space normal map PNG -> 16-bit grayscale displacement/height map PNG.
// CPU port of NormalMapTest.cpp + Shaders.hlsl, with discrete-GPU acceleration via ILGPU.
//
// -- NuGet dependencies --------------------------------------------------------
//   SixLabors.ImageSharp  >= 3.0      (PNG I/O)
//   ILGPU                 >= 1.5.0    (GPU compute - includes CUDA, OpenCL, CPU backends)
//
//   dotnet add package SixLabors.ImageSharp
//   dotnet add package ILGPU
//
// -- GPU backend priority ------------------------------------------------------
//   1. CUDA  (NVIDIA)  -- requires CUDA Toolkit installed on host
//   2. OpenCL GPU      -- AMD / Intel / NVIDIA without CUDA Toolkit
//   3. CPU fallback    -- existing Parallel.For implementation (no GPU required)
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
//               stepHeight  : 75f,
//               edgeMode    : EdgeMode.Clamp,
//               normalise   : true,
//               zRange      : ZRange.Half);
//       }
//   }
//
// -----------------------------------------------------------------------------
// Derived from original C++ code:
// https://skgenius.co.uk/FileDump/NormalToHeight_v0.2.1.zip
// Discussion here:
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
    /// How out-of-bounds pixel accesses are handled when sampling the normal map
    /// and height map during the iterative pass.
    /// Clamp: boundary pixel is repeated (matches CLAMP_EDGES in HLSL).
    /// Wrap:  texture wraps around (matches WRAPPED_TEXTURE in HLSL).
    /// </summary>
    public enum EdgeMode { Clamp, Wrap }

    /// <summary>
    /// Indicates the storage range of the Z (blue) channel in the normal map.
    /// Half: Z is stored in [0.5, 1.0] (positive hemisphere - standard tangent space).
    /// Full: Z is stored across the full [0, 1] range.
    /// Both modes use the same linear decode (pixel*2-1, normalise); this enum
    /// documents content and may be used for future channel-specific remapping.
    /// </summary>
    public enum ZRange { Half, Full }

    // -------------------------------------------------------------------------
    // Progress dialog
    // -------------------------------------------------------------------------

    /// <summary>
    /// A lightweight WinForms progress window that lives on its own STA thread
    /// so it stays responsive while compute threads are busy.
    ///
    /// Lifetime:
    ///   var dlg = ProgressForm.Show();
    ///   dlg.SetDevice("CUDA - RTX 3080");
    ///   dlg.Report(pass, total, mip, totalMips, w, h);
    ///   dlg.CloseAndWait();
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
            _lblMip = new Label
            {
                AutoSize = true,
                Location = new System.Drawing.Point(12, 34),
                Text = ""
            };
            _lblPass = new Label
            {
                AutoSize = true,
                Location = new System.Drawing.Point(12, 56),
                Text = ""
            };
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

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _ready.Set();
        }

        /// <summary>Creates and shows a ProgressForm on a new STA thread. Returns immediately.</summary>
        public static new ProgressForm Show()
        {
            ProgressForm form = null;
            var formCreated = new ManualResetEventSlim(false);

            var thread = new Thread(() =>
            {
                Application.EnableVisualStyles();
                form = new ProgressForm();
                formCreated.Set();
                Application.Run(form);
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            formCreated.Wait();
            form._ready.Wait();
            return form;
        }

        /// <summary>Sets the device label (e.g. "CUDA - RTX 3080" or "CPU"). Thread-safe.</summary>
        public void SetDevice(string label)
        {
            if (IsDisposed) return;
            BeginInvoke(new Action(() =>
            {
                if (!IsDisposed) _lblDevice.Text = label;
            }));
        }

        /// <summary>Updates mip/pass labels and progress bar. Thread-safe, non-blocking.</summary>
        public void Report(int pass, int totalPasses, int mipIndex, int totalMips, int mipW, int mipH)
        {
            if (IsDisposed) return;

            int done = totalMips - mipIndex - 1;
            double pct = (done + (double)(pass + 1) / totalPasses) / totalMips * 100.0;
            int barVal = (int)Math.Clamp(pct, 0, 100);
            int dispMip = totalMips - mipIndex;

            BeginInvoke(new Action(() =>
            {
                if (IsDisposed) return;
                _lblMip.Text = $"Mip level {dispMip} of {totalMips}  ({mipW}x{mipH})";
                _lblPass.Text = $"Pass {pass + 1:N0} of {totalPasses:N0}";
                _bar.Value = barVal;
            }));
        }

        /// <summary>Closes the form from any thread (fire-and-forget marshal).</summary>
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
    /// All ILGPU-compilable kernel entry points and their static helper methods.
    ///
    /// Rules that make these GPU-compilable:
    ///   - Every method is static.
    ///   - No access to C# static fields; all state is passed as parameters.
    ///   - No heap allocation (no new[], no LINQ, no delegates).
    ///   - GPU memory accessed only through ArrayView&lt;T&gt;.
    ///   - Uses tan(asin(x)) = x/sqrt(1-x^2) identity to avoid trig intrinsics.
    /// </summary>
    internal static class GpuKernels
    {
        // Direction indices - match HLSL static const int Dir*
        private const int DirNorth = 0;
        private const int DirNorthEast = 1;
        private const int DirEast = 2;
        private const int DirSouthEast = 3;
        private const int DirSouth = 4;
        private const int DirSouthWest = 5;
        private const int DirWest = 6;
        private const int DirNorthWest = 7;

        // -------------------------------------------------------------------------
        // Constant lookup tables expressed as if-else chains so the ILGPU compiler
        // can fold them into registers (static readonly arrays are not accessible
        // from kernel code).
        // -------------------------------------------------------------------------

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

        // -------------------------------------------------------------------------
        // Normal sampling helpers
        // -------------------------------------------------------------------------

        private static float SampleCh(
            ArrayView<float> nm, int w, int h,
            int nx, int ny, int ch, int edgeMode)
        {
            if (edgeMode == 0) // Clamp
            {
                nx = nx < 0 ? 0 : (nx >= w ? w - 1 : nx);
                ny = ny < 0 ? 0 : (ny >= h ? h - 1 : ny);
            }
            else // Wrap
            {
                nx = ((nx % w) + w) % w;
                ny = ((ny % h) + h) % h;
            }
            return nm[(ny * w + nx) * 4 + ch];
        }

        /// <summary>
        /// Decodes the normal at (nx, ny), normalises, and applies normalScale to XY.
        /// Matches HLSL GetNormal() and the CPU SampleNormal() helper.
        /// </summary>
        private static void SampleNormal(
            ArrayView<float> nm, int w, int h,
            int nx, int ny, int edgeMode, float normalScale,
            out float rnx, out float rny, out float rnz)
        {
            float rx = SampleCh(nm, w, h, nx, ny, 0, edgeMode) * 2f - 1f;
            float ry = SampleCh(nm, w, h, nx, ny, 1, edgeMode) * 2f - 1f;
            float rz = SampleCh(nm, w, h, nx, ny, 2, edgeMode) * 2f - 1f;

            float len = (float) Math.Sqrt(rx * rx + ry * ry + rz * rz);
            if (len > 1e-6f) { rx /= len; ry /= len; rz /= len; }

            rnx = rx * normalScale;   // XY scaled for contrast control
            rny = ry * normalScale;
            rnz = rz;                 // Z unscaled; used for sign check only
        }

        // -------------------------------------------------------------------------
        // Per-sample delta computation
        // -------------------------------------------------------------------------

        /// <summary>
        /// Computes one height delta from the normal at (px+dx, py+dy) projected
        /// onto DirVec[dir].
        ///
        /// Uses tan(asin(x)) = x / sqrt(1-x^2) to avoid trig functions, which
        /// are not universally available across GPU ISAs supported by ILGPU.
        ///
        /// Matches HLSL GetPixelDelta() and CPU GpuGetPixelDelta().
        /// </summary>
        private static float OneDelta(
            ArrayView<float> nm, int w, int h,
            int px, int py, int dx, int dy, int dir,
            int edgeMode, float normalScale, float maxStepHeight)
        {
            SampleNormal(nm, w, h, px + dx, py + dy, edgeMode, normalScale,
                out float nx, out float ny, out float nz);

            GetDirVec(dir, out float dvx, out float dvy);

            float dist = nx * dvx + ny * dvy;
            float xyLenSq = nx * nx + ny * ny;
            if (xyLenSq <= 0f) return 0f;

            float xyLen = (float) Math.Sqrt(xyLenSq);
            float clamped = xyLen > 1f ? 1f : xyLen;
            float underRoot = 1f - clamped * clamped;

            float tanAsin = underRoot < 1e-6f
                ? (dist < 0f ? -maxStepHeight : maxStepHeight)   // near-vertical: cap
                : clamped / (float) Math.Sqrt(underRoot);        // tan(asin(x)) = x/sqrt(1-x^2)

            float delta = tanAsin * (dist / xyLen);
            if (delta > maxStepHeight) delta = maxStepHeight;
            if (delta < -maxStepHeight) delta = -maxStepHeight;
            if (nz < 0f) delta = -delta;                          // sign flip for downward normals
            return delta;
        }

        // -------------------------------------------------------------------------
        // Kernel: ComputeDeltas
        // -------------------------------------------------------------------------

        /// <summary>
        /// GPU equivalent of the GenerateDeltas pixel shader.
        /// One GPU thread per pixel; writes 20 height deltas and a validity bitmask.
        ///
        /// edgeMode: 0 = Clamp, 1 = Wrap.
        /// Output deltas[pixelIdx*20 + i] and enableFlags[pixelIdx].
        /// </summary>
        public static void ComputeDeltasKernel(
            Index1D pixelIdx,
            ArrayView<float> normals,
            ArrayView<float> deltas,
            ArrayView<uint> enableFlags,
            int w, int h,
            int edgeMode,
            float normalScale,
            float maxStepHeight)
        {
            if (pixelIdx >= w * h) return;

            int px = (int)pixelIdx % w;
            int py = (int)pixelIdx / w;
            bool wrap = edgeMode != 0;

            uint f = 0u;
            int db = (int)pixelIdx * 20;

            // ---- Samples 0-3: cardinal distance-1 ---------------------------------
            if (wrap || px > 1)
            {
                deltas[db + 0] = OneDelta(normals, w, h, px, py, -1, 0, DirEast, edgeMode, normalScale, maxStepHeight);
                f |= 1u << 0;
            }
            if (wrap || py > 1)
            {
                deltas[db + 1] = OneDelta(normals, w, h, px, py, 0, -1, DirSouth, edgeMode, normalScale, maxStepHeight);
                f |= 1u << 1;
            }
            if (wrap || px < w - 1)
            {
                deltas[db + 2] = OneDelta(normals, w, h, px, py, 0, 0, DirWest, edgeMode, normalScale, maxStepHeight);
                f |= 1u << 2;
            }
            if (wrap || py < h - 1)
            {
                deltas[db + 3] = OneDelta(normals, w, h, px, py, 0, 0, DirNorth, edgeMode, normalScale, maxStepHeight);
                f |= 1u << 3;
            }

            // ---- Samples 4-7: diagonal distance-1 ---------------------------------
            if (wrap || (px > 1 && py > 1))
            {
                deltas[db + 4] = OneDelta(normals, w, h, px, py, -1, -1, DirSouthEast, edgeMode, normalScale, maxStepHeight);
                f |= 1u << 4;
            }
            if (wrap || (px < w - 1 && py < h - 1))
            {
                deltas[db + 5] = OneDelta(normals, w, h, px, py, 0, 0, DirNorthWest, edgeMode, normalScale, maxStepHeight);
                f |= 1u << 5;
            }
            if (wrap || (px > 1 && py < h - 1))
            {
                deltas[db + 6] = OneDelta(normals, w, h, px, py, -1, 0, DirNorthEast, edgeMode, normalScale, maxStepHeight);
                f |= 1u << 6;
            }
            if (wrap || (px < w - 1 && py > 1))
            {
                deltas[db + 7] = OneDelta(normals, w, h, px, py, 0, -1, DirSouthWest, edgeMode, normalScale, maxStepHeight);
                f |= 1u << 7;
            }

            // ---- Samples 8-11: cardinal distance-2 (sum of 2 OneDelta calls) ------
            if (wrap || px > 2)
            {
                deltas[db + 8] =
                    OneDelta(normals, w, h, px, py, -2, 0, DirEast, edgeMode, normalScale, maxStepHeight) +
                    OneDelta(normals, w, h, px, py, -1, 0, DirEast, edgeMode, normalScale, maxStepHeight);
                f |= 1u << 8;
            }
            if (wrap || py > 2)
            {
                deltas[db + 9] =
                    OneDelta(normals, w, h, px, py, 0, -2, DirSouth, edgeMode, normalScale, maxStepHeight) +
                    OneDelta(normals, w, h, px, py, 0, -1, DirSouth, edgeMode, normalScale, maxStepHeight);
                f |= 1u << 9;
            }
            if (wrap || px < w - 2)
            {
                deltas[db + 10] =
                    OneDelta(normals, w, h, px, py, 1, 0, DirWest, edgeMode, normalScale, maxStepHeight) +
                    OneDelta(normals, w, h, px, py, 0, 0, DirWest, edgeMode, normalScale, maxStepHeight);
                f |= 1u << 10;
            }
            if (wrap || py < h - 2)
            {
                deltas[db + 11] =
                    OneDelta(normals, w, h, px, py, 0, 1, DirNorth, edgeMode, normalScale, maxStepHeight) +
                    OneDelta(normals, w, h, px, py, 0, 0, DirNorth, edgeMode, normalScale, maxStepHeight);
                f |= 1u << 11;
            }

            // ---- Samples 12-19: knight-like (4 OneDelta calls * 0.5) --------------
            if (wrap || (px > 1 && py > 2))
            {
                deltas[db + 12] = (
                    OneDelta(normals, w, h, px, py, -1, -2, DirSouthEast, edgeMode, normalScale, maxStepHeight) +
                    OneDelta(normals, w, h, px, py, 0, -1, DirSouth, edgeMode, normalScale, maxStepHeight) +
                    OneDelta(normals, w, h, px, py, -1, -2, DirSouth, edgeMode, normalScale, maxStepHeight) +
                    OneDelta(normals, w, h, px, py, -1, -1, DirSouthEast, edgeMode, normalScale, maxStepHeight)
                ) * 0.5f;
                f |= 1u << 12;
            }
            if (wrap || (px < w - 1 && py > 2))
            {
                deltas[db + 13] = (
                    OneDelta(normals, w, h, px, py, 0, -2, DirSouthWest, edgeMode, normalScale, maxStepHeight) +
                    OneDelta(normals, w, h, px, py, 0, -1, DirSouth, edgeMode, normalScale, maxStepHeight) +
                    OneDelta(normals, w, h, px, py, 1, -2, DirSouth, edgeMode, normalScale, maxStepHeight) +
                    OneDelta(normals, w, h, px, py, 0, -1, DirSouthWest, edgeMode, normalScale, maxStepHeight)
                ) * 0.5f;
                f |= 1u << 13;
            }
            if (wrap || (px < w - 2 && py > 1))
            {
                deltas[db + 14] = (
                    OneDelta(normals, w, h, px, py, 1, -1, DirSouthWest, edgeMode, normalScale, maxStepHeight) +
                    OneDelta(normals, w, h, px, py, 0, 0, DirWest, edgeMode, normalScale, maxStepHeight) +
                    OneDelta(normals, w, h, px, py, 1, -1, DirWest, edgeMode, normalScale, maxStepHeight) +
                    OneDelta(normals, w, h, px, py, 0, -1, DirSouthWest, edgeMode, normalScale, maxStepHeight)
                ) * 0.5f;
                f |= 1u << 14;
            }
            if (wrap || (px < w - 2 && py < h - 1))
            {
                deltas[db + 15] = (
                    OneDelta(normals, w, h, px, py, 1, 0, DirNorthWest, edgeMode, normalScale, maxStepHeight) +
                    OneDelta(normals, w, h, px, py, 0, 0, DirWest, edgeMode, normalScale, maxStepHeight) +
                    OneDelta(normals, w, h, px, py, 1, 1, DirWest, edgeMode, normalScale, maxStepHeight) +
                    OneDelta(normals, w, h, px, py, 0, 0, DirNorthWest, edgeMode, normalScale, maxStepHeight)
                ) * 0.5f;
                f |= 1u << 15;
            }
            if (wrap || (px < w - 1 && py < h - 2))
            {
                deltas[db + 16] = (
                    OneDelta(normals, w, h, px, py, 0, 1, DirNorthWest, edgeMode, normalScale, maxStepHeight) +
                    OneDelta(normals, w, h, px, py, 0, 0, DirNorth, edgeMode, normalScale, maxStepHeight) +
                    OneDelta(normals, w, h, px, py, 1, 1, DirNorth, edgeMode, normalScale, maxStepHeight) +
                    OneDelta(normals, w, h, px, py, 0, 0, DirNorthWest, edgeMode, normalScale, maxStepHeight)
                ) * 0.5f;
                f |= 1u << 16;
            }
            if (wrap || (px > 1 && py < h - 2))
            {
                deltas[db + 17] = (
                    OneDelta(normals, w, h, px, py, -1, 1, DirNorthEast, edgeMode, normalScale, maxStepHeight) +
                    OneDelta(normals, w, h, px, py, 0, 0, DirNorth, edgeMode, normalScale, maxStepHeight) +
                    OneDelta(normals, w, h, px, py, -1, 1, DirNorth, edgeMode, normalScale, maxStepHeight) +
                    OneDelta(normals, w, h, px, py, -1, 0, DirNorthEast, edgeMode, normalScale, maxStepHeight)
                ) * 0.5f;
                f |= 1u << 17;
            }
            if (wrap || (px > 2 && py < h - 1))
            {
                deltas[db + 18] = (
                    OneDelta(normals, w, h, px, py, -2, 0, DirNorthEast, edgeMode, normalScale, maxStepHeight) +
                    OneDelta(normals, w, h, px, py, -1, 0, DirEast, edgeMode, normalScale, maxStepHeight) +
                    OneDelta(normals, w, h, px, py, -2, 1, DirEast, edgeMode, normalScale, maxStepHeight) +
                    OneDelta(normals, w, h, px, py, -1, 0, DirNorthEast, edgeMode, normalScale, maxStepHeight)
                ) * 0.5f;
                f |= 1u << 18;
            }
            if (wrap || (px > 2 && py > 1))
            {
                deltas[db + 19] = (
                    OneDelta(normals, w, h, px, py, -2, -1, DirSouthEast, edgeMode, normalScale, maxStepHeight) +
                    OneDelta(normals, w, h, px, py, -1, 0, DirEast, edgeMode, normalScale, maxStepHeight) +
                    OneDelta(normals, w, h, px, py, -2, -1, DirEast, edgeMode, normalScale, maxStepHeight) +
                    OneDelta(normals, w, h, px, py, -1, -1, DirSouthEast, edgeMode, normalScale, maxStepHeight)
                ) * 0.5f;
                f |= 1u << 19;
            }

            enableFlags[(int)pixelIdx] = f;
        }

        // -------------------------------------------------------------------------
        // Kernel: UpdateHeights
        // -------------------------------------------------------------------------

        /// <summary>
        /// GPU equivalent of the UpdateHeights pixel shader.
        /// One GPU thread per pixel, called once per pass.
        ///
        /// Clamp mode (edgeMode=0): uses all 20 neighbours with clamped coordinates,
        /// matching the #define CLAMP_EDGES path in the HLSL.
        /// Wrap  mode (edgeMode=1): uses only flagged (in-bounds) neighbours.
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
            bool clamp = (edgeMode == 0);

            int numSamples = clamp ? 20 : Popcnt32(f);
            if (numSamples == 0) { dst[(int)pixelIdx] = src[(int)pixelIdx]; return; }

            double height = 0.0;
            double scale = 1.0 / numSamples;
            int di = (int)pixelIdx * 20;

            for (int i = 0; i < 20; i++)
            {
                if (!clamp && (f & (1u << i)) == 0) continue;

                GetSampleOffset(i, out int ox, out int oy);

                int nx, ny;
                if (clamp)
                {
                    nx = px + ox; if (nx < 0) nx = 0; if (nx >= w) nx = w - 1;
                    ny = py + oy; if (ny < 0) ny = 0; if (ny >= h) ny = h - 1;
                }
                else
                {
                    nx = ((px + ox) % w + w) % w;
                    ny = ((py + oy) % h + h) % h;
                }

                double refH = src[ny * w + nx] + deltas[di + i];
                height += refH * scale;
            }

            dst[(int)pixelIdx] = (float)height;
        }

        // -------------------------------------------------------------------------
        // Kernel: BilinearDownsample4Ch (matches GenNormalMip shader)
        // -------------------------------------------------------------------------

        public static void BilinearDownsample4ChKernel(
            Index1D dstIdx,
            ArrayView<float> src,
            ArrayView<float> dst,
            int srcW, int srcH,
            int dstW, int dstH)
        {
            if (dstIdx >= dstW * dstH) return;

            int dx = (int)dstIdx % dstW;
            int dy = (int)dstIdx / dstW;
            float scX = (float)srcW / dstW;
            float scY = (float)srcH / dstH;

            float sx = (dx + 0.5f) * scX - 0.5f;
            float sy = (dy + 0.5f) * scY - 0.5f;

            int x0 = (int)sx; if (x0 < 0) x0 = 0; if (x0 >= srcW) x0 = srcW - 1;
            int y0 = (int)sy; if (y0 < 0) y0 = 0; if (y0 >= srcH) y0 = srcH - 1;
            int x1 = x0 + 1; if (x1 >= srcW) x1 = srcW - 1;
            int y1 = y0 + 1; if (y1 >= srcH) y1 = srcH - 1;

            float fx = sx - x0; if (fx < 0f) fx = 0f; if (fx > 1f) fx = 1f;
            float fy = sy - y0; if (fy < 0f) fy = 0f; if (fy > 1f) fy = 1f;

            int db = (int)dstIdx * 4;
            for (int c = 0; c < 4; c++)
            {
                float v00 = src[(y0 * srcW + x0) * 4 + c];
                float v10 = src[(y0 * srcW + x1) * 4 + c];
                float v01 = src[(y1 * srcW + x0) * 4 + c];
                float v11 = src[(y1 * srcW + x1) * 4 + c];
                dst[db + c] = v00 * (1 - fx) * (1 - fy) + v10 * fx * (1 - fy) + v01 * (1 - fx) * fy + v11 * fx * fy;
            }
        }

        // -------------------------------------------------------------------------
        // Kernel: BilinearUpsample1ChX2 (matches UpscaleHeight shader)
        // -------------------------------------------------------------------------

        public static void BilinearUpsample1ChX2Kernel(
            Index1D dstIdx,
            ArrayView<float> src,
            ArrayView<float> dst,
            int srcW, int srcH,
            int dstW, int dstH)
        {
            if (dstIdx >= dstW * dstH) return;

            int dx = (int)dstIdx % dstW;
            int dy = (int)dstIdx / dstW;
            float scX = (float)srcW / dstW;
            float scY = (float)srcH / dstH;

            float sx = (dx + 0.5f) * scX - 0.5f;
            float sy = (dy + 0.5f) * scY - 0.5f;

            int x0 = (int)sx; if (x0 < 0) x0 = 0; if (x0 >= srcW) x0 = srcW - 1;
            int y0 = (int)sy; if (y0 < 0) y0 = 0; if (y0 >= srcH) y0 = srcH - 1;
            int x1 = x0 + 1; if (x1 >= srcW) x1 = srcW - 1;
            int y1 = y0 + 1; if (y1 >= srcH) y1 = srcH - 1;

            float fx = sx - x0; if (fx < 0f) fx = 0f; if (fx > 1f) fx = 1f;
            float fy = sy - y0; if (fy < 0f) fy = 0f; if (fy > 1f) fy = 1f;

            float v = src[y0 * srcW + x0] * (1 - fx) * (1 - fy) + src[y0 * srcW + x1] * fx * (1 - fy) +
                      src[y1 * srcW + x0] * (1 - fx) * fy + src[y1 * srcW + x1] * fx * fy;
            dst[(int)dstIdx] = v * 2f;
        }

        // -------------------------------------------------------------------------
        // Utility
        // -------------------------------------------------------------------------

        /// <summary>Parallel-prefix popcount for a 32-bit value (GPU-friendly, no branch).</summary>
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
    /// Manages an ILGPU context, a discrete-GPU accelerator, and compiled kernel
    /// delegates.  Create via TryCreate(); returns null if no discrete GPU is
    /// available.  Implements IDisposable to release GPU resources.
    /// </summary>
    internal sealed class GpuPipeline : IDisposable
    {
        private readonly Context _ctx;
        private readonly Accelerator _acc;

        // Compiled kernel delegates.
        // Each generic argument list after Index1D matches the kernel's parameters.
        private readonly Action<Index1D,
            ArrayView<float>, ArrayView<float>, ArrayView<uint>,
            int, int, int, float, float>
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

        public string DeviceName { get; }

        // ---- Construction ---------------------------------------------------

        private GpuPipeline(Context ctx, Accelerator acc, string name)
        {
            _ctx = ctx;
            _acc = acc;
            DeviceName = name;

            // Compile all four kernels once.
            // LoadAutoGroupedStreamKernel selects optimal group sizes automatically
            // and uses the accelerator's default stream.
            _computeDeltas = acc.LoadAutoGroupedStreamKernel<
                Index1D,
                ArrayView<float>, ArrayView<float>, ArrayView<uint>,
                int, int, int, float, float>(
                GpuKernels.ComputeDeltasKernel);

            _updateHeights = acc.LoadAutoGroupedStreamKernel<
                Index1D,
                ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<uint>,
                int, int, int>(
                GpuKernels.UpdateHeightsKernel);

            _downsample4Ch = acc.LoadAutoGroupedStreamKernel<
                Index1D,
                ArrayView<float>, ArrayView<float>,
                int, int, int, int>(
                GpuKernels.BilinearDownsample4ChKernel);

            _upsample1ChX2 = acc.LoadAutoGroupedStreamKernel<
                Index1D,
                ArrayView<float>, ArrayView<float>,
                int, int, int, int>(
                GpuKernels.BilinearUpsample1ChX2Kernel);
        }

        // ---- Factory --------------------------------------------------------

        /// <summary>
        /// Probes for a discrete GPU in priority order: CUDA -> OpenCL GPU.
        /// Returns null if none is found or if ILGPU fails to initialise.
        /// </summary>
        public static GpuPipeline TryCreate()
        {
            Context ctx = null;
            try
            {
                ctx = Context.CreateDefault();

                // Pass 1: CUDA (NVIDIA)
                foreach (var device in ctx.Devices)
                {
                    if (device.AcceleratorType != AcceleratorType.Cuda) continue;
                    try
                    {
                        var acc = device.CreateAccelerator(ctx);
                        Console.WriteLine($"[GpuPipeline] CUDA device: {device.Name}");
                        return new GpuPipeline(ctx, acc, $"CUDA - {device.Name}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[GpuPipeline] CUDA '{device.Name}' failed: {ex.Message}");
                    }
                }

                // Pass 2: OpenCL GPU (AMD / Intel / NVIDIA without CUDA toolkit)
                foreach (var device in ctx.Devices)
                {
                    if (device.AcceleratorType != AcceleratorType.OpenCL) continue;
                    // Exclude OpenCL CPU devices (report themselves as OpenCL)
                    if (device.Name.IndexOf("CPU", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    try
                    {
                        var acc = device.CreateAccelerator(ctx);
                        Console.WriteLine($"[GpuPipeline] OpenCL device: {device.Name}");
                        return new GpuPipeline(ctx, acc, $"OpenCL - {device.Name}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[GpuPipeline] OpenCL '{device.Name}' failed: {ex.Message}");
                    }
                }

                Console.WriteLine("[GpuPipeline] No discrete GPU found. Falling back to CPU.");
                ctx.Dispose();
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GpuPipeline] ILGPU init failed: {ex.Message}");
                ctx?.Dispose();
                return null;
            }
        }

        // ---- GPU pipeline stages --------------------------------------------

        /// <summary>
        /// GPU version of ComputeDeltas.
        /// Uploads normals, runs ComputeDeltasKernel, downloads results.
        /// deltasOut and flagsOut must be pre-allocated to (w*h*20) and (w*h).
        /// </summary>
        public void ComputeDeltas(
            float[] normals, int w, int h,
            int edgeMode, float normalScale, float maxStepHeight,
            float[] deltasOut, uint[] flagsOut)
        {
            int pixels = w * h;
            using var gpuNormals = _acc.Allocate1D<float>(normals.Length);
            using var gpuDeltas = _acc.Allocate1D<float>((long)pixels * 20);
            using var gpuFlags = _acc.Allocate1D<uint>(pixels);

            gpuNormals.CopyFromCPU(normals);
            gpuDeltas.MemSetToZero();   // unset slots read as 0.0 in UpdateHeights

            _computeDeltas(
                pixels,
                gpuNormals.View, gpuDeltas.View, gpuFlags.View,
                w, h, edgeMode, normalScale, maxStepHeight);
            _acc.Synchronize();

            gpuDeltas.CopyToCPU(deltasOut);
            gpuFlags.CopyToCPU(flagsOut);
        }

        /// <summary>
        /// GPU version of RunPasses.
        /// Ping-pongs between two GPU buffers for numPasses iterations.
        /// Deltas and flags are uploaded once; height buffers are kept on-device
        /// throughout the loop to eliminate redundant transfers.
        /// </summary>
        public float[] RunPasses(
            float[] initial, float[] deltas, uint[] flags,
            int w, int h, int numPasses, int edgeMode,
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
                // Report is a fire-and-forget BeginInvoke; negligible overhead
                progress.Report(pass, numPasses, mipIndex, totalMips, w, h);

                _updateHeights(
                    pixels,
                    src.View, dst.View,
                    gpuDeltas.View, gpuFlags.View,
                    w, h, edgeMode);
                _acc.Synchronize();

                (src, dst) = (dst, src);
            }

            // Single download at the end of all passes
            return src.GetAsArray1D();
        }

        /// <summary>GPU version of BilinearDownsample4Ch.</summary>
        public float[] BilinearDownsample4Ch(
            float[] src, int srcW, int srcH, int dstW, int dstH)
        {
            using var gpuSrc = _acc.Allocate1D<float>(src.Length);
            using var gpuDst = _acc.Allocate1D<float>(dstW * dstH * 4);
            gpuSrc.CopyFromCPU(src);
            _downsample4Ch(dstW * dstH, gpuSrc.View, gpuDst.View, srcW, srcH, dstW, dstH);
            _acc.Synchronize();
            return gpuDst.GetAsArray1D();
        }

        /// <summary>GPU version of BilinearUpsample1Ch_x2.</summary>
        public float[] BilinearUpsample1ChX2(
            float[] src, int srcW, int srcH, int dstW, int dstH)
        {
            using var gpuSrc = _acc.Allocate1D<float>(src.Length);
            using var gpuDst = _acc.Allocate1D<float>(dstW * dstH);
            gpuSrc.CopyFromCPU(src);
            _upsample1ChX2(dstW * dstH, gpuSrc.View, gpuDst.View, srcW, srcH, dstW, dstH);
            _acc.Synchronize();
            return gpuDst.GetAsArray1D();
        }

        // ---- IDisposable ----------------------------------------------------

        public void Dispose()
        {
            _acc?.Dispose();
            _ctx?.Dispose();
        }
    }

    // -------------------------------------------------------------------------
    // Main converter
    // -------------------------------------------------------------------------

    /// <summary>
    /// Converts a tangent-space normal map to a displacement (height) map.
    ///
    /// At runtime, NormalConvert() calls GpuPipeline.TryCreate() to probe for
    /// a discrete GPU.  If one is found, all heavy compute stages are dispatched
    /// there via ILGPU kernels.  If none is found, the equivalent Parallel.For
    /// CPU implementations are used transparently — output is identical.
    /// </summary>
    public static class NormalToDisplacement
    {
        // Direction constants (CPU-side; mirrored as literals in GpuKernels)
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
            (-1, 0),(0,-1),(1, 0),(0, 1),
            (-1,-1),(1, 1),(-1,1),(1,-1),
            (-2, 0),(0,-2),(2, 0),(0, 2),
            (-1,-2),(1,-2),(2,-1),(2, 1),
            ( 1, 2),(-1,2),(-2,1),(-2,-1)
        };

        // Per-call state (set once at the top of NormalConvert)
        private static float s_maxStepHeight;
        private static float s_normalScale;
        private static bool s_normalise;
        private static float s_scale;
        private static int s_numPasses;
        private static EdgeMode s_edgeMode;
        private static ZRange s_zRange;

        // ---- Public entry point ---------------------------------------------

        /// <summary>
        /// Converts a normal map to a 16-bit grayscale displacement map PNG.
        /// Automatically selects GPU (CUDA or OpenCL) when available; otherwise
        /// falls back to CPU Parallel.For with identical output.
        /// </summary>
        public static void NormalConvert(
            string inputPath,
            string outputPath,
            float scale = 1.0f,
            int numPasses = 4096,
            float normalScale = 1.0f,
            float stepHeight = 250.0f,
            EdgeMode edgeMode = EdgeMode.Wrap,
            bool normalise = true,
            ZRange zRange = ZRange.Half)
        {
            s_scale = scale;
            s_numPasses = numPasses;
            s_normalScale = normalScale;
            s_maxStepHeight = stepHeight;
            s_edgeMode = edgeMode;
            s_normalise = normalise;
            s_zRange = zRange;

            int edgeModeInt = (edgeMode == EdgeMode.Clamp) ? 0 : 1;

            // Probe for a discrete GPU.  'using' ensures Dispose() even on exception.
            using GpuPipeline gpu = GpuPipeline.TryCreate();
            bool usingGpu = gpu != null;
            string backendLabel = usingGpu ? gpu.DeviceName : "CPU (Parallel.For)";

            Console.WriteLine($"[NormalToDisplacement] Compute backend : {backendLabel}");
            Console.WriteLine($"[NormalToDisplacement] Loading         : {inputPath}");

            LoadNormalMap(inputPath, out float[] rawNormals, out int imgW, out int imgH);
            Console.WriteLine($"[NormalToDisplacement] Image size      : {imgW} x {imgH}");

            // Compute mip pyramid depth (same logic as original C++)
            int numMips = 0, totalMips;
            { int me = Math.Min(imgW, imgH); while (me > 256) { me /= 2; numMips++; } }
            totalMips = numMips + 1;
            Console.WriteLine($"[NormalToDisplacement] Mip levels      : {totalMips}  (coarsest first)");

            float[] prevHeights = null;
            int prevW = 0, prevH = 0;

            using var progress = ProgressForm.Show();
            progress.SetDevice(backendLabel);

            for (int mip = numMips; mip >= 0; mip--)
            {
                int step = 1 << mip;
                int mipW = imgW / step;
                int mipH = imgH / step;
                Console.WriteLine($"[NormalToDisplacement]   Mip {mip}: {mipW}x{mipH}");

                // ---- Downsample normal map to mip resolution -----------------
                float[] mipNormals;
                if (mip == 0)
                    mipNormals = rawNormals;
                else if (usingGpu)
                    mipNormals = gpu.BilinearDownsample4Ch(rawNormals, imgW, imgH, mipW, mipH);
                else
                    mipNormals = CpuBilinearDownsample4Ch(rawNormals, imgW, imgH, mipW, mipH);

                // ---- Initialise height map for this mip ---------------------
                float[] heights;
                if (prevHeights == null)
                {
                    // Coarsest level: sequential left-to-right, top-to-bottom sweep.
                    // This is inherently serial and fast at the small coarsest size.
                    heights = InitSweep(mipNormals, mipW, mipH);
                }
                else if (usingGpu)
                    heights = gpu.BilinearUpsample1ChX2(prevHeights, prevW, prevH, mipW, mipH);
                else
                    heights = CpuBilinearUpsample1Ch_x2(prevHeights, prevW, prevH, mipW, mipH);

                // ---- Precompute per-pixel 20-neighbour deltas ----------------
                var deltas = new float[mipW * mipH * 20];
                var enableFlags = new uint[mipW * mipH];

                if (usingGpu)
                    gpu.ComputeDeltas(mipNormals, mipW, mipH, edgeModeInt, normalScale, stepHeight, deltas, enableFlags);
                else
                    CpuComputeDeltas(mipNormals, mipW, mipH, deltas, enableFlags);

                // ---- Iterative height propagation ---------------------------
                if (usingGpu)
                    heights = gpu.RunPasses(heights, deltas, enableFlags, mipW, mipH, numPasses, edgeModeInt, progress, mip, totalMips);
                else
                    heights = CpuRunPasses(heights, deltas, enableFlags, mipW, mipH, progress, mip, totalMips);

                prevHeights = heights;
                prevW = mipW;
                prevH = mipH;
            }

            progress.CloseAndWait();

            Console.WriteLine($"[NormalToDisplacement] Saving: {outputPath}");
            SaveHeightMap(prevHeights, prevW, prevH, outputPath);
            Console.WriteLine("[NormalToDisplacement] Done.");
        }

        // ---- PNG I/O --------------------------------------------------------

        private static void LoadNormalMap(
            string path, out float[] data, out int w, out int h)
        {
            using var img = SixLabors.ImageSharp.Image.Load<Rgba32>(path);
            int lw = img.Width, lh = img.Height;
            float[] ld = new float[lw * lh * 4];

            img.ProcessPixelRows(acc =>
            {
                for (int y = 0; y < lh; y++)
                {
                    var row = acc.GetRowSpan(y);
                    for (int x = 0; x < lw; x++)
                    {
                        int i = (y * lw + x) * 4;
                        ld[i] = row[x].R / 255f;
                        ld[i + 1] = row[x].G / 255f;
                        ld[i + 2] = row[x].B / 255f;
                        ld[i + 3] = row[x].A / 255f;
                    }
                }
            });

            w = lw; h = lh; data = ld;
        }

        private static void SaveHeightMap(
            float[] heights, int w, int h, string outputPath)
        {
            float hMin = float.MaxValue, hMax = float.MinValue;
            foreach (float v in heights)
            {
                if (v < hMin) hMin = v;
                if (v > hMax) hMax = v;
            }
            float range = hMax - hMin;

            using var img = new Image<L16>(w, h);
            img.ProcessPixelRows(acc =>
            {
                for (int y = 0; y < h; y++)
                {
                    var row = acc.GetRowSpan(y);
                    for (int x = 0; x < w; x++)
                    {
                        float val = heights[y * w + x] - hMin;
                        float out16 = s_normalise
                            ? ((range > 0f) ? (val / range) * 65535f : 0f)
                            : val * (65535f / MathF.Sqrt((float)(w * w + h * h))) * s_scale;
                        row[x] = new L16((ushort)Math.Clamp((int)out16, 0, 65535));
                    }
                }
            });

            using var fs = File.Open(outputPath, FileMode.Create, FileAccess.Write);
            img.SaveAsPng(fs);
        }

        // ---- CPU fallback: normal sampling ----------------------------------

        private static (float x, float y, float z) SampleNormal(
            float[] normals, int w, int h, int nx, int ny)
        {
            if (s_edgeMode == EdgeMode.Clamp)
            {
                nx = Math.Clamp(nx, 0, w - 1);
                ny = Math.Clamp(ny, 0, h - 1);
            }
            else
            {
                nx = ((nx % w) + w) % w;
                ny = ((ny % h) + h) % h;
            }
            int i = (ny * w + nx) * 4;
            float x = normals[i] * 2f - 1f, y = normals[i + 1] * 2f - 1f, z = normals[i + 2] * 2f - 1f;
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
            float delta = Math.Clamp(MathF.Tan(MathF.Asin(xyLen)) * (dist / xyLen), -s_maxStepHeight, s_maxStepHeight);
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

        // ---- CPU fallback: InitSweep (always CPU; sequential by design) -----

        /// <summary>
        /// Runs four directional sweeps (L->R T->B, R->L B->T, L->R B->T, R->L T->B)
        /// and averages the results.  The original single L->R T->B pass created a
        /// strong negative-height artifact on the left edge wherever a downward
        /// normal chain accumulated unchecked (no left neighbour to counterbalance).
        /// Averaging four opposing sweeps cancels these directional biases so the
        /// iterative passes refine shape rather than recover from a bad seed.
        /// </summary>
        private static float[] InitSweep(float[] normals, int w, int h)
        {
            float[] a = SweepPass(normals, w, h, xFwd: true, yFwd: true);   // L->R, T->B
            float[] b = SweepPass(normals, w, h, xFwd: false, yFwd: false);  // R->L, B->T
            float[] c = SweepPass(normals, w, h, xFwd: true, yFwd: false);  // L->R, B->T
            float[] d = SweepPass(normals, w, h, xFwd: false, yFwd: true);   // R->L, T->B

            float[] result = new float[w * h];
            for (int i = 0; i < w * h; i++)
                result[i] = (a[i] + b[i] + c[i] + d[i]) * 0.25f;
            return result;
        }

        /// <summary>
        /// A single directional sweep accumulating height deltas from the
        /// horizontal and vertical neighbours in the chosen direction.
        /// xFwd=true sweeps left->right; yFwd=true sweeps top->bottom.
        /// The delta direction vectors flip with the sweep so the normal
        /// projection remains geometrically correct in each direction.
        /// </summary>
        private static float[] SweepPass(float[] normals, int w, int h, bool xFwd, bool yFwd)
        {
            float[] hm = new float[w * h];

            int yStart = yFwd ? 0 : h - 1;
            int yEnd = yFwd ? h : -1;
            int yStep = yFwd ? 1 : -1;

            // Direction vectors for CpuGetPixelDelta.
            // When sweeping L->R we accumulate from the left neighbour, so the
            // "step toward the neighbour" vector is (-1, 0) -- East in HLSL terms.
            // When sweeping R->L we accumulate from the right neighbour: (+1, 0).
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

                    // Horizontal neighbour (the pixel we came from in x)
                    int prevX = px - xStep;
                    if (prevX >= 0 && prevX < w)
                    {
                        float delta = CpuGetPixelDelta(normals, w, h, px, py, xVec, 0f);
                        sum += hm[py * w + prevX] + delta;
                        cnt++;
                    }

                    // Vertical neighbour (the pixel we came from in y)
                    int prevY = py - yStep;
                    if (prevY >= 0 && prevY < h)
                    {
                        float delta = CpuGetPixelDelta(normals, w, h, px, py, 0f, yVec);
                        sum += hm[prevY * w + px] + delta;
                        cnt++;
                    }

                    hm[py * w + px] = cnt > 0 ? sum / cnt : 0f;
                }
            }

            return hm;
        }

        // ---- CPU fallback: CpuComputeDeltas ---------------------------------

        private static void CpuComputeDeltas(
            float[] normals, int w, int h,
            float[] deltas, uint[] flags)
        {
            bool wrap = (s_edgeMode == EdgeMode.Wrap);

            Parallel.For(0, h, py =>
            {
                for (int px = 0; px < w; px++)
                {
                    int b = (py * w + px) * 20;
                    uint f = 0u;

                    void D1(int i, bool ok, int dx, int dy, int dir)
                    {
                        if (!wrap && !ok) return;
                        deltas[b + i] = GpuGetPixelDelta(normals, w, h, px, py, dx, dy, dir);
                        f |= 1u << i;
                    }
                    void D2(int i, bool ok, int ax, int ay, int da, int bx, int by, int db2)
                    {
                        if (!wrap && !ok) return;
                        deltas[b + i] = GpuGetPixelDelta(normals, w, h, px, py, ax, ay, da)
                                    + GpuGetPixelDelta(normals, w, h, px, py, bx, by, db2);
                        f |= 1u << i;
                    }
                    void D4h(int i, bool ok,
                             int ax, int ay, int da, int bx, int by, int db2,
                             int cx, int cy, int dc, int ex, int ey, int de)
                    {
                        if (!wrap && !ok) return;
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

        // ---- CPU fallback: CpuRunPasses -------------------------------------

        private static float[] CpuRunPasses(
            float[] initial, float[] deltas, uint[] enableFlags,
            int w, int h,
            ProgressForm progress, int mipIndex, int totalMips)
        {
            bool clamp = (s_edgeMode == EdgeMode.Clamp);
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
                        int n = clamp ? 20 : PopCount(f);

                        if (n == 0) { dst[idx] = src[idx]; continue; }

                        double height = 0.0, scale = 1.0 / n;
                        int di = idx * 20;

                        for (int i = 0; i < 20; i++)
                        {
                            if (!clamp && (f & (1u << i)) == 0) continue;
                            int nx, ny;
                            if (clamp)
                            {
                                nx = Math.Clamp(px + SampleOffsets[i].x, 0, w - 1);
                                ny = Math.Clamp(py + SampleOffsets[i].y, 0, h - 1);
                            }
                            else
                            {
                                nx = ((px + SampleOffsets[i].x) % w + w) % w;
                                ny = ((py + SampleOffsets[i].y) % h + h) % h;
                            }
                            height += (src[ny * w + nx] + deltas[di + i]) * scale;
                        }
                        dst[idx] = (float)height;
                    }
                });

                (src, dst) = (dst, src);
            }
            return src;
        }

        // ---- CPU fallback: bilinear resampling ------------------------------

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
                    {
                        dst[db + c] = src[(y0 * srcW + x0) * 4 + c] * (1 - fx) * (1 - fy) + src[(y0 * srcW + x1) * 4 + c] * fx * (1 - fy)
                                  + src[(y1 * srcW + x0) * 4 + c] * (1 - fx) * fy + src[(y1 * srcW + x1) * 4 + c] * fx * fy;
                    }
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

        private static int PopCount(uint v)
        {
            int c = 0; while (v != 0) { v &= v - 1; c++; }
            return c;
        }
    }
}