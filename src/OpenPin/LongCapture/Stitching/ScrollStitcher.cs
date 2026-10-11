namespace OpenPin.LongCapture.Stitching;

/// <summary>
/// UI-free scrolling-screenshot stitcher. Feed it consecutive grabs of the same screen region (BGRA, top-down,
/// stride = width*4, alpha ignored) and it estimates how far the content scrolled between the last accepted frame
/// and the new one, then appends only the newly revealed lines.
/// <para>
/// Internally every frame is converted to a canonical "vertical" layout (horizontal mode transposes the frame), so the
/// algorithm only deals with <em>lines</em> along the scroll axis:
/// </para>
/// <list type="number">
/// <item>Line profile: per line, the mean gray level in up to 64 bins across the line (sub-sampled, one pass).</item>
/// <item>Same-position comparison with the reference frame → fixed header / footer lines (prefix / suffix that did not
/// change) and fixed columns (sidebars, scrollbars that did not move) which are excluded from matching.</item>
/// <item>8 features per line (bin groups over the moving columns) → robust coarse search over ALL offsets (truncated L1,
/// lines averaged in groups of 2–4), then a full-resolution refinement around the best local minima, the previous
/// offset and 0. Lines without texture are ignored; a candidate needs enough informative matching lines.</item>
/// <item>Pixel verification of the best candidates on sampled full-resolution pixels.</item>
/// </list>
/// Positions are tracked in content coordinates, so scrolling backwards never duplicates content and over-estimated
/// footers only delay appending. The result is stored in blocks (<see cref="StripStore"/>) plus the "tail" of the
/// current reference frame, and composed on demand. Not thread-safe.
/// </summary>
internal sealed unsafe class ScrollStitcher
{
    private const int F = 8; // features per line

    private readonly StitchOptions _opt;
    private readonly int _lines;  // lines per frame (along the scroll axis)
    private readonly int _cw;     // pixels per line (across the scroll axis)
    private readonly int _bins;
    private readonly int _maxLength;
    private readonly int[] _sampleX;
    private readonly int[] _sampleBin;
    private readonly float[] _binScale;

    private byte[] _ref, _cur;
    private float[] _refProf, _curProf;
    private readonly float[] _lastProf;

    // per-match scratch
    private readonly bool[] _lineStatic;
    private readonly bool[] _binStatic;
    private readonly int[] _activeBins;
    private readonly float[] _featA, _featB, _coarseA, _coarseB;
    private readonly bool[] _infoA, _infoB, _coarseInfoA, _coarseInfoB;

    private StripStore _store;
    private bool _hasRef;
    private bool _dirSet;         // stitch direction established by the first real movement
    private bool _flipped;        // working space = frames with reversed line order (stitching up / left)
    private bool _started;        // first successful movement seen → the store holds the output prefix
    private int _refPos;          // content position of the reference frame (line 0 of the reference ↔ output line _refPos)
    private int _frontier;        // output lines committed to the store
    private int _pred;            // last non-zero offset (prediction / tie-break)
    private int _headerSession = int.MaxValue;
    private int _footerSession = int.MaxValue;
    private bool _limitReached;
    private int _limitTailStart;

    public ScrollStitcher(int frameWidth, int frameHeight, LongCaptureDirection direction, StitchOptions? options = null)
    {
        if (frameWidth <= 0 || frameHeight <= 0) throw new ArgumentOutOfRangeException(nameof(frameWidth));
        _opt = options ?? new StitchOptions();
        FrameWidth = frameWidth;
        FrameHeight = frameHeight;
        Direction = direction;
        bool vertical = direction == LongCaptureDirection.Vertical;
        _lines = vertical ? frameHeight : frameWidth;
        _cw = vertical ? frameWidth : frameHeight;
        _maxLength = Math.Max(_opt.MaxLength, _lines);

        int step = Math.Max(1, (_cw + _opt.MaxSamplesPerLine - 1) / Math.Max(1, _opt.MaxSamplesPerLine));
        int samples = (_cw + step - 1) / step;
        _bins = Math.Clamp(Math.Min(_opt.MaxBins, _cw / 6), 1, samples);
        _sampleX = new int[samples];
        _sampleBin = new int[samples];
        var counts = new int[_bins];
        for (int s = 0; s < samples; s++)
        {
            int x = Math.Min(_cw - 1, s * step + step / 2);
            _sampleX[s] = x;
            int b = Math.Min(_bins - 1, (int)((long)x * _bins / _cw));
            _sampleBin[s] = b;
            counts[b]++;
        }
        _binScale = new float[_bins];
        for (int b = 0; b < _bins; b++) _binScale[b] = counts[b] == 0 ? 0 : 1f / (256f * counts[b]);

        int frameBytes = _lines * _cw * 4;
        _ref = new byte[frameBytes];
        _cur = new byte[frameBytes];
        _refProf = new float[_lines * _bins];
        _curProf = new float[_lines * _bins];
        _lastProf = new float[_lines * _bins];
        _lineStatic = new bool[_lines];
        _binStatic = new bool[_bins];
        _activeBins = new int[_bins];
        _featA = new float[_lines * F];
        _featB = new float[_lines * F];
        _coarseA = new float[_lines * F];
        _coarseB = new float[_lines * F];
        _infoA = new bool[_lines];
        _infoB = new bool[_lines];
        _coarseInfoA = new bool[_lines];
        _coarseInfoB = new bool[_lines];
        _store = new StripStore(_cw);
    }

    public int FrameWidth { get; }
    public int FrameHeight { get; }
    public LongCaptureDirection Direction { get; }

    /// <summary>Lines per frame along the scroll axis.</summary>
    public int FrameLines => _lines;

    /// <summary>Total result length along the scroll axis (0 before the first frame).</summary>
    public int Length => !_hasRef ? 0 : !_started ? _lines : _frontier + (_lines - TailStart);

    /// <summary>True once at least one scroll step was stitched (the result is longer than one frame).</summary>
    public bool HasStitched => _started && Length > _lines;

    /// <summary>Output line where the current reference frame (≈ the visible viewport) starts.</summary>
    public int ViewportStart
    {
        get
        {
            if (!_started) return 0;
            int pos = _flipped ? Length - _refPos - _lines : _refPos;
            return Math.Clamp(pos, 0, Math.Max(0, Length - 1));
        }
    }

    /// <summary>True when the stitch direction is opposite to the scroll axis (content was stitched upward / leftward).</summary>
    public bool StitchesBackward => _flipped;

    /// <summary>The next real movement re-establishes the stitch direction (call when capturing resumes).</summary>
    public void ResetDirection() => _dirSet = false;

    public bool LimitReached => _limitReached;

    /// <summary>Number of times content was lost because a step was larger than the matchable band (diagnostics).</summary>
    public int GapCount { get; private set; }

    /// <summary>Memory held by the stitched strips (bytes).</summary>
    public long StoredBytes => _store.AllocatedBytes;

    private int FooterEstimate => _footerSession == int.MaxValue ? 0 : _footerSession;

    private int TailStart => _limitReached
        ? _limitTailStart
        : Math.Clamp(Math.Min(_frontier - _refPos, _lines - FooterEstimate), 0, _lines);

    // ============================================================================================ push

    /// <summary>Process one frame (BGRA, <see cref="FrameWidth"/> × <see cref="FrameHeight"/>, stride = width*4).</summary>
    public StitchResult Push(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < FrameWidth * FrameHeight * 4) throw new ArgumentException("Frame too small", nameof(frame));

        if (Direction == LongCaptureDirection.Vertical) frame[..(_lines * _cw * 4)].CopyTo(_cur);
        else Transpose(frame, FrameWidth, FrameHeight, _cur);
        if (_flipped) FlipLines(_cur, _cw * 4);
        ComputeProfile(_cur, _curProf);

        if (!_hasRef)
        {
            _curProf.CopyTo(_lastProf, 0);
            SwapReference();
            _hasRef = true;
            _refPos = 0;
            return Result(StitchStatus.First);
        }

        bool unchanged = _curProf.AsSpan().SequenceEqual(_lastProf);
        _curProf.CopyTo(_lastProf, 0);
        if (unchanged) return Result(StitchStatus.Unchanged);
        return Match();
    }

    private StitchResult Result(StitchStatus status, int offset = 0, int appended = 0, int header = 0, int footer = 0) =>
        new(status, offset, appended, Length, header, footer);

    private void SwapReference()
    {
        (_ref, _cur) = (_cur, _ref);
        (_refProf, _curProf) = (_curProf, _refProf);
    }

    // ============================================================================================ matching

    private StitchResult Match()
    {
        int n = _lines, k = _bins;
        float tol = _opt.BinTolerance;

        // 1. Same-position comparison: fixed header (prefix) / footer (suffix) lines.
        // A few bins may differ slightly (noise, a blinking caret in a wide bin); any strong difference = changed.
        int allowed = Math.Max(1, k / 16);
        float strong = 4 * tol;
        int changedLines = 0;
        fixed (float* pa = _refProf, pb = _curProf)
        {
            for (int i = 0; i < n; i++)
            {
                float* ra = pa + i * k, rb = pb + i * k;
                int diff = 0;
                for (int b = 0; b < k; b++)
                {
                    float dv = MathF.Abs(ra[b] - rb[b]);
                    if (dv > tol) diff += dv > strong ? k : 1;
                }
                _lineStatic[i] = diff <= allowed;
                if (!_lineStatic[i]) changedLines++;
            }
        }
        int h = 0;
        while (h < n && _lineStatic[h]) h++;
        if (h == n)
        {
            SwapReference(); // tiny local change only: follow the latest appearance
            return Result(StitchStatus.NoMovement, header: n);
        }
        int f = 0;
        while (f < n - h && _lineStatic[n - 1 - f]) f++;

        (h, f) = EnsureBand(h, f);
        int? found = FindOffset(h, f);
        if (found == null && _headerSession != int.MaxValue)
        {
            // Over-estimated fixed bars (blank content that happened to stay equal) shrink the matchable band and with
            // it the largest detectable step; under-estimated ones (a bar that flickered) pollute the band. Retry with
            // the session-level estimates (min over previous steps), then with the larger of both.
            foreach (var (hs, fs) in new[]
                     {
                         (Math.Min(h, _headerSession), Math.Min(f, _footerSession)),
                         (Math.Max(h, _headerSession), Math.Max(f, _footerSession)),
                     })
            {
                var (h2, f2) = EnsureBand(hs, fs);
                if (h2 == h && f2 == f) continue;
                found = FindOffset(h2, f2);
                if (found == null) continue;
                (h, f) = (h2, f2);
                break;
            }
        }
        if (found is { } offset) return Accept(offset, h, f);

        // No reliable match. Small local changes (caret, hover, spinner) over an otherwise identical frame = no movement.
        if (changedLines <= n * 15 / 100 && ZeroOffsetPlausible())
        {
            SwapReference();
            return Result(StitchStatus.NoMovement, header: h, footer: f);
        }
        return Result(StitchStatus.Failed, header: h, footer: f);
    }

    /// <summary>Offset 0 judged over the whole frame and all columns (no exclusion of unchanged areas).</summary>
    private bool ZeroOffsetPlausible()
    {
        for (int b = 0; b < _bins; b++) { _binStatic[b] = false; _activeBins[b] = b; }
        BuildFeatures(_refProf, 0, _lines, _bins, _featA, _infoA);
        BuildFeatures(_curProf, 0, _lines, _bins, _featB, _infoB);
        Evaluate(_featA, _featB, _infoA, _infoB, _lines, 0, Math.Max(4, _lines * 3 / 100), out bool ok, out int info);
        return (ok || info == 0) && VerifyPixels(0, 0, _lines);
    }

    /// <summary>Static lines next to the band carry no information, but the band must be large enough to match.</summary>
    private (int H, int F) EnsureBand(int h, int f)
    {
        int n = _lines;
        int minBand = Math.Min(n, Math.Max(16, n / 10));
        if (n - h - f < minBand)
        {
            int extra = minBand - (n - h - f);
            int dh = Math.Min(h, (extra + 1) / 2);
            h -= dh; extra -= dh;
            int df = Math.Min(f, extra);
            f -= df; extra -= df;
            if (extra > 0) h -= Math.Min(h, extra);
        }
        return (h, f);
    }

    /// <summary>
    /// Finds the scroll offset of the current frame relative to the reference, matching only the band [h, n − f).
    /// Returns null if no candidate is reliable.
    /// </summary>
    private int? FindOffset(int h, int f)
    {
        int n = _lines, k = _bins, m = n - h - f;
        float tol = _opt.BinTolerance;

        // Fixed columns inside the band (sidebars, scrollbar track…) are excluded from matching.
        int active = 0;
        fixed (float* pa = _refProf, pb = _curProf)
        {
            int maxDiff = m / 200;
            for (int b = 0; b < k; b++)
            {
                int cnt = 0;
                for (int i = h; i < n - f; i++)
                    if (MathF.Abs(pa[i * k + b] - pb[i * k + b]) > tol) cnt++;
                _binStatic[b] = cnt <= maxDiff;
                if (!_binStatic[b]) _activeBins[active++] = b;
            }
        }
        if (active == 0)
        {
            for (int b = 0; b < k; b++) { _binStatic[b] = false; _activeBins[b] = b; }
            active = k;
        }

        // Features + informative lines of both frames (band only).
        BuildFeatures(_refProf, h, m, active, _featA, _infoA);
        BuildFeatures(_curProf, h, m, active, _featB, _infoB);

        int minOverlap = Math.Min(m, Math.Max(_opt.MinOverlapLines, m - (int)(m * _opt.MaxOffsetFraction)));
        int maxD = Math.Max(0, m - minOverlap);
        int minInfo = Math.Max(4, m * 3 / 100);

        // Coarse search over all offsets.
        int q = m >= 768 ? 4 : m >= 256 ? 2 : 1;
        int mc = m / q;
        var candidates = new List<int>(64);
        if (q > 1 && mc >= 4)
        {
            Downsample(_featA, _infoA, m, q, _coarseA, _coarseInfoA);
            Downsample(_featB, _infoB, m, q, _coarseB, _coarseInfoB);
            int maxDc = maxD / q;
            int minInfoC = Math.Max(2, mc * 3 / 100);
            var scores = new double[2 * maxDc + 1];
            for (int dc = -maxDc; dc <= maxDc; dc++)
                scores[dc + maxDc] = Evaluate(_coarseA, _coarseB, _coarseInfoA, _coarseInfoB, mc, dc, minInfoC, out _, out _);
            foreach (int dc in BestLocalMinima(scores, 6))
                for (int d = (dc - maxDc) * q - q + 1; d <= (dc - maxDc) * q + q - 1; d++)
                    candidates.Add(d);
        }
        else
        {
            for (int d = -maxD; d <= maxD; d++) candidates.Add(d);
        }
        for (int d = -2; d <= 2; d++) { candidates.Add(d); candidates.Add(_pred + d); }

        // Full-resolution evaluation.
        var evaluated = new HashSet<int>();
        var valid = new List<(int D, double Score)>();
        foreach (int d in candidates)
        {
            if (d < -maxD || d > maxD || !evaluated.Add(d)) continue;
            double score = Evaluate(_featA, _featB, _infoA, _infoB, m, d, minInfo, out bool ok, out _);
            if (ok) valid.Add((d, score + 0.25 * Math.Abs(d - _pred) / Math.Max(1, m)));
        }
        valid.Sort((a, b) => a.Score.CompareTo(b.Score));

        // Pixel verification of the best few.
        for (int i = 0; i < valid.Count && i < 3; i++)
            if (VerifyPixels(valid[i].D, h, m)) return valid[i].D;
        return null;
    }

    private StitchResult Accept(int d, int h, int f)
    {
        int n = _lines;
        if (d == 0)
        {
            SwapReference();
            return Result(StitchStatus.NoMovement, 0, 0, h, f);
        }

        if (_limitReached && !(d < 0 && _dirSet && _opt.AutoCrop))
            return Result(StitchStatus.LimitReached, d, 0, h, f); // keep the state; only trimming is still possible

        if (!_dirSet)
        {
            // The first real movement sets the stitch direction. Backward → flip the working space so stitching
            // always runs "forward" (the output is flipped back when composed).
            if (d < 0)
            {
                FlipWorkingSpace();
                d = -d;
                (h, f) = (f, h);
            }
            _dirSet = true;
        }

        int newPos = _refPos + d;
        if (d < 0 && _opt.AutoCrop) return TrimTo(newPos, d, h, f);

        int appended = 0;
        bool limit = false;
        if (!_started)
        {
            // The first frame without its footer opens the result (the footer comes from the last frame).
            int first = Math.Min(n - f, _maxLength - f);
            _store.Append(_ref, first);
            _frontier = first;
            _started = true;
        }

        else if (d < 0)
        {
            // Scrolling back: the old reference may still hold pending lines that its (over-estimated) footer hid and
            // that the new frame no longer shows. Commit them now using the best footer estimate.
            int flushEnd = n - Math.Min(_footerSession, f);
            int from = _frontier - _refPos;
            if (from >= 0 && flushEnd > from)
            {
                int count = Math.Min(flushEnd - from, Math.Max(0, _maxLength - f - _frontier));
                if (count > 0)
                {
                    _store.Append(_ref.AsSpan(from * _cw * 4), count);
                    _frontier += count;
                }
            }
        }

        int start = _frontier - newPos, end = n - f;
        if (start < h && end > h)
        {
            // The step skipped content (larger than the matchable band). Never re-emit the fixed header.
            GapCount++;
            start = h;
        }
        if (end > start)
        {
            int room = _maxLength - f - _frontier;
            int count = Math.Min(end - start, Math.Max(0, room));
            if (count > 0)
            {
                _store.Append(_cur.AsSpan(start * _cw * 4), count);
                _frontier += count;
                appended = count;
            }
            if (count < end - start) limit = true;
        }

        SwapReference();
        _refPos = newPos;
        _pred = d;
        _headerSession = Math.Min(_headerSession, h);
        _footerSession = Math.Min(_footerSession, f);
        if (limit)
        {
            _limitTailStart = n - f;
            _limitReached = true;
            return Result(StitchStatus.LimitReached, d, appended, h, f);
        }
        return Result(appended > 0 ? StitchStatus.Appended : StitchStatus.Moved, d, appended, h, f);
    }

    /// <summary>Auto-crop: the content moved back to <paramref name="newPos"/>; cut the result at the visible content.</summary>
    private StitchResult TrimTo(int newPos, int d, int h, int f)
    {
        int n = _lines;
        int before = Length;
        SwapReference();
        _refPos = newPos;
        _pred = d;
        _headerSession = Math.Min(_headerSession, h);
        _footerSession = Math.Min(_footerSession, f);
        _limitReached = false;

        if (newPos <= 0 || !_started)
        {
            // Back at (or before) the start: the result is exactly the visible frame again; direction is re-established.
            _store.Clear();
            _frontier = 0;
            _started = false;
            if (_flipped) FlipWorkingSpace();
            _refPos = 0;
            _pred = 0;
            _dirSet = false;
        }
        else
        {
            int keep = newPos + n - f;
            if (keep < _frontier)
            {
                _store.Truncate(keep);
                _frontier = keep;
            }
        }
        return Result(Length < before ? StitchStatus.Trimmed : StitchStatus.Moved, d, 0, h, f);
    }

    /// <summary>
    /// Reverses the working space: both frames, their profiles and the stitched result (the old first lines — the
    /// fixed header — become the tail taken from the reference frame).
    /// </summary>
    private void FlipWorkingSpace()
    {
        int n = _lines;
        if (_started)
        {
            int length = Length;
            int tail = Math.Min(n, _headerSession == int.MaxValue ? 0 : _headerSession);
            var store = new StripStore(_cw);
            var line = new byte[_cw * 4];
            for (int i = length - 1; i >= tail; i--)
            {
                CopyWorkingLine(i, line);
                store.Append(line, 1);
            }
            _store = store;
            _frontier = length - tail;
            _refPos = length - _refPos - n;
        }
        FlipLines(_ref, _cw * 4);
        FlipLines(_cur, _cw * 4);
        FlipLines(_refProf, _bins);
        FlipLines(_curProf, _bins);
        FlipLines(_lastProf, _bins);
        (_headerSession, _footerSession) = (_footerSession, _headerSession);
        _pred = -_pred;
        _flipped = !_flipped;
    }

    /// <summary>Reverses the order of the lines (each <paramref name="lineLength"/> elements) of a frame-sized buffer.</summary>
    private void FlipLines<T>(T[] buffer, int lineLength) where T : unmanaged
    {
        int lineBytes = lineLength * sizeof(T);
        var tmp = new byte[lineBytes];
        fixed (T* p = buffer)
        fixed (byte* t = tmp)
        {
            byte* b = (byte*)p;
            for (int i = 0, j = _lines - 1; i < j; i++, j--)
            {
                Buffer.MemoryCopy(b + (long)i * lineBytes, t, lineBytes, lineBytes);
                Buffer.MemoryCopy(b + (long)j * lineBytes, b + (long)i * lineBytes, lineBytes, lineBytes);
                Buffer.MemoryCopy(t, b + (long)j * lineBytes, lineBytes, lineBytes);
            }
        }
    }

    /// <summary>8 features per band line (mean of the active bins split into 8 groups) + "has texture" flags.</summary>
    private void BuildFeatures(float[] prof, int h, int m, int active, float[] feat, bool[] info)
    {
        int k = _bins;
        int groups = Math.Min(F, active);
        fixed (float* p = prof, ft = feat)
        fixed (int* ab = _activeBins)
        {
            for (int j = 0; j < m; j++)
            {
                float* row = p + (h + j) * k;
                float* fr = ft + j * F;
                float min = float.MaxValue, max = float.MinValue;
                for (int g = 0; g < F; g++) fr[g] = 0;
                for (int g = 0; g < groups; g++)
                {
                    int a0 = g * active / groups, a1 = (g + 1) * active / groups;
                    float sum = 0;
                    for (int a = a0; a < a1; a++)
                    {
                        float v = row[ab[a]];
                        sum += v;
                        if (v < min) min = v;
                        if (v > max) max = v;
                    }
                    fr[g] = sum / (a1 - a0);
                }
                bool texture = max - min > 8f;
                if (!texture && j > 0)
                {
                    float* prev = fr - F;
                    float edge = 0;
                    for (int g = 0; g < F; g++) edge += MathF.Abs(fr[g] - prev[g]);
                    texture = edge > 8f;
                }
                info[j] = texture;
            }
        }
    }

    private static void Downsample(float[] feat, bool[] info, int m, int q, float[] outFeat, bool[] outInfo)
    {
        int mc = m / q;
        float inv = 1f / q;
        for (int jc = 0; jc < mc; jc++)
        {
            bool any = false;
            for (int g = 0; g < F; g++)
            {
                float s = 0;
                for (int t = 0; t < q; t++) s += feat[(jc * q + t) * F + g];
                outFeat[jc * F + g] = s * inv;
            }
            for (int t = 0; t < q; t++) any |= info[jc * q + t];
            outInfo[jc] = any;
        }
    }

    /// <summary>
    /// Robust cost of offset <paramref name="d"/> (a[j] ↔ b[j − d]): mean truncated-L1 feature cost over informative
    /// lines, plus a penalty when the overlap holds too few informative lines (blank overlaps prove nothing).
    /// </summary>
    private double Evaluate(float[] fa, float[] fb, bool[] ia, bool[] ib, int m, int d, int minInfo, out bool valid, out int nInfo)
    {
        int j0 = Math.Max(0, d), j1 = Math.Min(m, m + d);
        float cap = _opt.FeatureCap, rowTol = _opt.RowTolerance;
        double sum = 0;
        int info = 0, bad = 0;
        fixed (float* pa = fa, pb = fb)
        fixed (bool* qa = ia, qb = ib)
        {
            for (int j = j0; j < j1; j++)
            {
                float* ra = pa + j * F, rb = pb + (j - d) * F;
                float c = MathF.Min(MathF.Abs(ra[0] - rb[0]), cap) + MathF.Min(MathF.Abs(ra[1] - rb[1]), cap)
                        + MathF.Min(MathF.Abs(ra[2] - rb[2]), cap) + MathF.Min(MathF.Abs(ra[3] - rb[3]), cap)
                        + MathF.Min(MathF.Abs(ra[4] - rb[4]), cap) + MathF.Min(MathF.Abs(ra[5] - rb[5]), cap)
                        + MathF.Min(MathF.Abs(ra[6] - rb[6]), cap) + MathF.Min(MathF.Abs(ra[7] - rb[7]), cap);
                bool isBad = c > rowTol;
                if (qa[j] || qb[j - d] || isBad)
                {
                    info++;
                    sum += c;
                    if (isBad) bad++;
                }
            }
        }
        nInfo = info;
        valid = info >= minInfo && bad <= _opt.MaxBadRowFraction * info;
        return (sum + rowTol * Math.Max(0, minInfo - info)) / Math.Max(info, minInfo);
    }

    private static IEnumerable<int> BestLocalMinima(double[] scores, int count)
    {
        var minima = new List<int>();
        for (int i = 0; i < scores.Length; i++)
        {
            double s = scores[i];
            if ((i == 0 || s <= scores[i - 1]) && (i == scores.Length - 1 || s <= scores[i + 1])) minima.Add(i);
        }
        minima.Sort((a, b) => scores[a].CompareTo(scores[b]));
        return minima.Take(count);
    }

    /// <summary>Compare sampled full-resolution pixels of the overlap (moving columns only).</summary>
    private bool VerifyPixels(int d, int h, int m)
    {
        int j0 = Math.Max(0, d), j1 = Math.Min(m, m + d);
        int overlap = j1 - j0;
        if (overlap <= 0) return false;
        int rowStep = Math.Max(1, overlap / 160);

        // Sample x positions over the moving bins.
        var xs = new List<int>(800);
        int activeWidth = 0;
        for (int b = 0; b < _bins; b++)
            if (!_binStatic[b]) activeWidth += BinEnd(b) - BinStart(b);
        int sx = Math.Max(1, activeWidth / 640);
        for (int b = 0; b < _bins; b++)
        {
            if (_binStatic[b]) continue;
            for (int x = BinStart(b) + sx / 2; x < BinEnd(b); x += sx) xs.Add(x);
        }
        if (xs.Count == 0) return true;

        int tol = _opt.PixelTolerance;
        int maxBadPixels = (int)(_opt.MaxBadPixelFraction * xs.Count);
        int infoRows = 0, badRows = 0;
        int lineBytes = _cw * 4;
        fixed (byte* ra = _ref, rb = _cur)
        {
            for (int j = j0; j < j1; j += rowStep)
            {
                byte* la = ra + (long)(h + j) * lineBytes;
                byte* lb = rb + (long)(h + j - d) * lineBytes;
                int bad = 0;
                foreach (int x in xs)
                {
                    byte* a = la + x * 4, c = lb + x * 4;
                    int db = Math.Abs(a[0] - c[0]), dg = Math.Abs(a[1] - c[1]), dr = Math.Abs(a[2] - c[2]);
                    if (db > tol || dg > tol || dr > tol) bad++;
                }
                bool rowBad = bad > maxBadPixels;
                if (_infoA[j] || _infoB[j - d] || rowBad)
                {
                    infoRows++;
                    if (rowBad) badRows++;
                }
            }
        }
        return infoRows == 0 || badRows <= _opt.MaxBadPixelRowFraction * infoRows;
    }

    private int BinStart(int b) => (int)(((long)b * _cw + _bins - 1) / _bins);
    private int BinEnd(int b) => (int)(((long)(b + 1) * _cw + _bins - 1) / _bins);

    // ============================================================================================ profile

    private void ComputeProfile(byte[] frame, float[] prof)
    {
        int k = _bins, lineBytes = _cw * 4, samples = _sampleX.Length;
        int* sums = stackalloc int[k];
        fixed (byte* f = frame)
        fixed (float* p = prof, scale = _binScale)
        fixed (int* sx = _sampleX, sb = _sampleBin)
        {
            for (int line = 0; line < _lines; line++)
            {
                for (int b = 0; b < k; b++) sums[b] = 0;
                byte* row = f + (long)line * lineBytes;
                for (int s = 0; s < samples; s++)
                {
                    byte* px = row + sx[s] * 4;
                    sums[sb[s]] += px[0] * 29 + px[1] * 150 + px[2] * 77;
                }
                float* pl = p + line * k;
                for (int b = 0; b < k; b++) pl[b] = sums[b] * scale[b];
            }
        }
    }

    // ============================================================================================ output

    /// <summary>Copies output line <paramref name="index"/> (canonical layout, BGRA) into <paramref name="dst"/>.</summary>
    private void CopyLine(int index, Span<byte> dst) => CopyWorkingLine(_flipped ? Length - 1 - index : index, dst);

    /// <summary>Copies line <paramref name="index"/> of the result in working-space order.</summary>
    private void CopyWorkingLine(int index, Span<byte> dst)
    {
        int lineBytes = _cw * 4;
        if (_started && index < _frontier)
        {
            _store.Line(index).CopyTo(dst);
            return;
        }
        int src = _started ? TailStart + (index - _frontier) : index;
        _ref.AsSpan(src * lineBytes, lineBytes).CopyTo(dst);
        for (int a = 3; a < lineBytes; a += 4) dst[a] = 255;
    }

    /// <summary>
    /// Composes the result in the final orientation with <paramref name="trimStart"/> / <paramref name="trimEnd"/> lines
    /// removed (top/bottom in vertical mode, left/right in horizontal mode). Returns BGRA pixels (alpha 255).
    /// </summary>
    public (byte[] Pixels, int Width, int Height) Compose(int trimStart = 0, int trimEnd = 0)
    {
        int length = Length;
        if (length == 0) throw new InvalidOperationException("Nothing captured");
        int s = Math.Clamp(trimStart, 0, length - 1);
        int e = Math.Clamp(length - Math.Max(0, trimEnd), s + 1, length);
        int count = e - s, lineBytes = _cw * 4;
        var canonical = new byte[(long)count * lineBytes];
        for (int i = 0; i < count; i++) CopyLine(s + i, canonical.AsSpan(i * lineBytes, lineBytes));
        if (Direction == LongCaptureDirection.Vertical) return (canonical, _cw, count);
        var image = new byte[canonical.Length];
        Transpose(canonical, _cw, count, image);
        return (image, count, _cw);
    }

    /// <summary>
    /// Downscaled preview of the whole current result (final orientation), fitting into maxWidth × maxHeight.
    /// <paramref name="scale"/> = preview pixels per result pixel.
    /// </summary>
    public (byte[] Pixels, int Width, int Height) RenderPreview(int maxWidth, int maxHeight, out double scale)
    {
        int length = Length;
        scale = 0;
        if (length == 0 || maxWidth <= 0 || maxHeight <= 0) return (Array.Empty<byte>(), 0, 0);
        bool vertical = Direction == LongCaptureDirection.Vertical;
        double fw = vertical ? _cw : length, fh = vertical ? length : _cw;
        scale = Math.Min(1.0, Math.Min(maxWidth / fw, maxHeight / fh));
        int pw = Math.Max(1, (int)Math.Round(_cw * scale)), pl = Math.Max(1, (int)Math.Round(length * scale));
        var canonical = new byte[pw * pl * 4];
        var line = new byte[_cw * 4];
        var acc = new int[pw * 3];
        var cnt = new int[pw];
        double inv = 1.0 / scale;
        for (int ol = 0; ol < pl; ol++)
        {
            Array.Clear(acc);
            Array.Clear(cnt);
            int l0 = Math.Min(length - 1, (int)(ol * inv)), l1 = Math.Clamp((int)((ol + 1) * inv), l0 + 1, length);
            int lstep = Math.Max(1, (l1 - l0) / 3);
            for (int l = l0; l < l1; l += lstep)
            {
                CopyLine(l, line);
                for (int oc = 0; oc < pw; oc++)
                {
                    int c0 = Math.Min(_cw - 1, (int)(oc * inv)), c1 = Math.Clamp((int)((oc + 1) * inv), c0 + 1, _cw);
                    int cstep = Math.Max(1, (c1 - c0) / 3);
                    for (int c = c0; c < c1; c += cstep)
                    {
                        acc[oc * 3] += line[c * 4];
                        acc[oc * 3 + 1] += line[c * 4 + 1];
                        acc[oc * 3 + 2] += line[c * 4 + 2];
                        cnt[oc]++;
                    }
                }
            }
            int o = ol * pw * 4;
            for (int oc = 0; oc < pw; oc++, o += 4)
            {
                int c = Math.Max(1, cnt[oc]);
                canonical[o] = (byte)(acc[oc * 3] / c);
                canonical[o + 1] = (byte)(acc[oc * 3 + 1] / c);
                canonical[o + 2] = (byte)(acc[oc * 3 + 2] / c);
                canonical[o + 3] = 255;
            }
        }
        if (vertical) return (canonical, pw, pl);
        var image = new byte[canonical.Length];
        Transpose(canonical, pw, pl, image);
        return (image, pl, pw);
    }

    /// <summary>dst[x, y] = src[y, x] for 32-bit pixels: src is w × h, dst is h × w (row length h).</summary>
    internal static void Transpose(ReadOnlySpan<byte> src, int w, int h, Span<byte> dst)
    {
        const int block = 32;
        fixed (byte* s0 = src, d0 = dst)
        {
            uint* s = (uint*)s0, d = (uint*)d0;
            for (int y0 = 0; y0 < h; y0 += block)
            {
                int y1 = Math.Min(y0 + block, h);
                for (int x0 = 0; x0 < w; x0 += block)
                {
                    int x1 = Math.Min(x0 + block, w);
                    for (int y = y0; y < y1; y++)
                    {
                        uint* srow = s + (long)y * w;
                        for (int x = x0; x < x1; x++) d[(long)x * h + y] = srow[x];
                    }
                }
            }
        }
    }
}
