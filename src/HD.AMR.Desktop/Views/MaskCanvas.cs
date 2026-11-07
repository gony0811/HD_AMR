using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using HD.AMR.App.Service.Vision;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ISImage = SixLabors.ImageSharp.Image;

namespace HD.AMR.Desktop.Views;

/// <summary>
/// DL 비드 마스크 브러시 에디터. 기존 label.js 이식 — 캡처 이미지 위에 반투명 빨강으로 비드 영역을
/// 칠/지우고, 저장 시 흑백 이진 마스크 PNG 를 돌려준다. 마스크는 이미지 원본 해상도로 유지되며 표시는
/// 균등 스케일이므로 저장 마스크는 항상 원본 해상도로 정렬된다.
/// <para><b>선 모드</b>(<see cref="LineMode"/>): 좌클릭으로 비드 중심선 점을 추가하고(기존 점 근처를 누르면 드래그로
/// 이동), <b>Shift+좌클릭</b>은 앞 점과 잇지 않고 새 선을 시작한다(떨어진 비드), 우클릭으로 마지막 점을 지운다. 점이 바뀔 때마다 마스크를 <see cref="LineThickness"/> 두께로 다시
/// 래스터화한다(<see cref="WeldLineLabel.Rasterize"/>). 선 라벨은 점(+두께)만 저장되고 학습 마스크는 변환 시 다시 만든다.</para>
/// </summary>
public sealed class MaskCanvas : Control
{
    private Bitmap? _base;
    private WriteableBitmap? _overlay;
    private byte[] _mask = Array.Empty<byte>();       // w*h, 0/1
    private byte[] _bgra = Array.Empty<byte>();       // w*h*4 오버레이 픽셀(빨강, alpha 140/0)
    private int _w, _h;
    private bool _painting;
    private readonly List<Point> _linePts = new();   // 원본 픽셀 좌표
    private readonly SortedSet<int> _lineBreaks = new();   // 이 인덱스의 점은 앞 점과 잇지 않음(새 선 시작)
    private int _dragIdx = -1;
    private int _lineThickness = WeldLineLabel.DefaultThickness;

    public bool Erase { get; set; }
    public int Radius { get; set; } = 12;
    public bool HasImage => _base is not null;

    /// <summary>true 면 클릭이 브러시 대신 중심선 점 편집으로 동작.</summary>
    public bool LineMode { get; set; }

    /// <summary>선 라벨 래스터화 두께(px). 바꾸면 점이 있을 때 즉시 다시 그린다.</summary>
    public int LineThickness
    {
        get => _lineThickness;
        set
        {
            if (_lineThickness == value) return;
            _lineThickness = Math.Max(1, value);
            if (_linePts.Count >= 2) RasterizeLine();
        }
    }

    /// <summary>현재 선 라벨. 이어진 선분이 하나도 없으면(점 2개 미만 등) null.</summary>
    public WeldLineLabel? GetLineLabel()
    {
        if (_w == 0 || _linePts.Count < 2) return null;
        var l = new WeldLineLabel
        {
            Width = _w,
            Height = _h,
            Thickness = _lineThickness,
            Points = _linePts.Select(p => new[] { Math.Round(p.X, 1), Math.Round(p.Y, 1) }).ToList(),
            Breaks = _lineBreaks.Count > 0 ? _lineBreaks.ToList() : null,
        };
        return l.IsValid ? l : null;
    }

    /// <summary>선 조각 수(끊김 + 1). 점이 없으면 0.</summary>
    public int LineSegmentCount => _linePts.Count == 0 ? 0 : _lineBreaks.Count + 1;

    /// <summary>저장된 선 라벨 표시 — 점을 불러와 두께대로 마스크를 다시 그린다. null/무효면 점만 비운다(마스크 유지).</summary>
    public void SetLine(WeldLineLabel? line)
    {
        _linePts.Clear(); _lineBreaks.Clear();
        if (line is { IsValid: true } && line.Width == _w && line.Height == _h)
        {
            _linePts.AddRange(line.Points.Select(p => new Point(p[0], p[1])));
            foreach (var b in line.Breaks ?? new()) if (b > 0 && b < _linePts.Count) _lineBreaks.Add(b);
            _lineThickness = line.Thickness;
            RasterizeLine();
            return;
        }
        InvalidateVisual();
    }

    /// <summary>칠해진 픽셀이 하나라도 있는지(빈 면 라벨 저장 = 삭제 판단용).</summary>
    public bool HasPaint => Array.IndexOf(_mask, (byte)1) >= 0;

    private void RasterizeLine()
    {
        var label = GetLineLabel();
        Array.Clear(_mask); Array.Clear(_bgra);
        if (label is not null)
        {
            var m = label.Rasterize();
            for (var i = 0; i < m.Length; i++)
                if (m[i] != 0) SetPixel(i % _w, i / _w, true);
        }
        FlushRows(0, _h);
        InvalidateVisual();
    }

    /// <summary>이미지(+선택적 마스크 초안 PNG) 로드. 마스크는 흰색(>127)=비드.</summary>
    public void Open(byte[] imagePng, byte[]? maskPng)
    {
        using (var ms = new MemoryStream(imagePng)) _base = new Bitmap(ms);
        _w = _base.PixelSize.Width; _h = _base.PixelSize.Height;
        _mask = new byte[_w * _h];
        _bgra = new byte[_w * _h * 4];
        _overlay = new WriteableBitmap(new PixelSize(_w, _h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        if (maskPng is { Length: > 0 }) LoadMask(maskPng);
        FlushRows(0, _h);
        InvalidateVisual();
    }

    /// <summary>배경 이미지만 교체(같은 크기일 때) — 칠한 마스크·선 점은 그대로(예: CLAHE 로 보기 전환).</summary>
    public void SetBase(byte[] imagePng)
    {
        Bitmap nb;
        using (var ms = new MemoryStream(imagePng)) nb = new Bitmap(ms);
        if (nb.PixelSize.Width != _w || nb.PixelSize.Height != _h) { nb.Dispose(); return; }
        var old = _base;
        _base = nb;
        old?.Dispose();
        InvalidateVisual();
    }

    private void LoadMask(byte[] png)
    {
        try
        {
            using var img = ISImage.Load<L8>(png);
            if (img.Width != _w || img.Height != _h) img.Mutate(c => c.Resize(_w, _h));
            img.ProcessPixelRows(acc =>
            {
                for (var y = 0; y < _h; y++)
                {
                    var row = acc.GetRowSpan(y);
                    for (var x = 0; x < _w; x++) SetPixel(x, y, row[x].PackedValue > 127);
                }
            });
        }
        catch { /* 초안 없음/손상 — 빈 마스크 */ }
    }

    public void Clear()
    {
        if (_w == 0) return;
        Array.Clear(_mask); Array.Clear(_bgra);
        _linePts.Clear(); _lineBreaks.Clear();
        FlushRows(0, _h); InvalidateVisual();
    }

    /// <summary>칠해진 픽셀=흰색, 아니면 검정인 이진 마스크 PNG.</summary>
    public byte[]? ExportPng()
    {
        if (_w == 0) return null;
        using var img = new SixLabors.ImageSharp.Image<L8>(_w, _h);
        img.ProcessPixelRows(acc =>
        {
            for (var y = 0; y < _h; y++)
            {
                var row = acc.GetRowSpan(y);
                for (var x = 0; x < _w; x++) row[x] = new L8(_mask[y * _w + x] != 0 ? (byte)255 : (byte)0);
            }
        });
        using var ms = new MemoryStream();
        SixLabors.ImageSharp.ImageExtensions.SaveAsPng(img, ms);
        return ms.ToArray();
    }

    private void SetPixel(int x, int y, bool on)
    {
        var i = y * _w + x;
        _mask[i] = on ? (byte)1 : (byte)0;
        var o = i * 4;
        // premultiplied BGRA: 빨강 alpha 140 → (B,G,R,A) = (0,0,140,140)
        _bgra[o] = 0; _bgra[o + 1] = 0; _bgra[o + 2] = on ? (byte)140 : (byte)0; _bgra[o + 3] = on ? (byte)140 : (byte)0;
    }

    private void Stamp(double nx, double ny)
    {
        var r = Math.Max(1, Radius);
        int x0 = (int)Math.Max(0, nx - r), x1 = (int)Math.Min(_w - 1, nx + r);
        int y0 = (int)Math.Max(0, ny - r), y1 = (int)Math.Min(_h - 1, ny + r);
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
                if ((x - nx) * (x - nx) + (y - ny) * (y - ny) <= r * r) SetPixel(x, y, !Erase);
        FlushRows(y0, y1 + 1);
        InvalidateVisual();
    }

    // _bgra 의 [y0,y1) 행을 WriteableBitmap 으로 복사.
    private void FlushRows(int y0, int y1)
    {
        if (_overlay is null) return;
        using var fb = _overlay.Lock();
        for (var y = y0; y < y1; y++)
            Marshal.Copy(_bgra, y * _w * 4, fb.Address + y * fb.RowBytes, _w * 4);
    }

    // 표시 영역(균등 스케일·중앙) 계산.
    private Rect ImageRect()
    {
        var b = Bounds.Size;
        if (_w == 0 || b.Width <= 0 || b.Height <= 0) return default;
        var s = Math.Min(b.Width / _w, b.Height / _h);
        var dw = _w * s; var dh = _h * s;
        return new Rect((b.Width - dw) / 2, (b.Height - dh) / 2, dw, dh);
    }

    private (double x, double y)? ToNative(Point p)
    {
        var r = ImageRect();
        if (r.Width <= 0) return null;
        return ((p.X - r.X) / r.Width * _w, (p.Y - r.Y) / r.Height * _h);
    }

    // 표시 좌표 8px 이내의 기존 선 점 인덱스(없으면 -1).
    private int HitPoint(double nx, double ny)
    {
        var r = ImageRect();
        if (r.Width <= 0) return -1;
        var tol = 8.0 * _w / r.Width;
        for (var i = _linePts.Count - 1; i >= 0; i--)
        {
            var p = _linePts[i];
            if ((p.X - nx) * (p.X - nx) + (p.Y - ny) * (p.Y - ny) <= tol * tol) return i;
        }
        return -1;
    }

    private static Point ClampPt(double x, double y, int w, int h)
        => new(Math.Clamp(x, 0, w - 1), Math.Clamp(y, 0, h - 1));

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (_base is null) return;
        if (LineMode)
        {
            e.Handled = true;
            if (ToNative(e.GetPosition(this)) is not { } q) return;
            if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            {
                if (_linePts.Count > 0)
                {
                    _linePts.RemoveAt(_linePts.Count - 1);
                    _lineBreaks.Remove(_linePts.Count);   // 지운 점이 새 선의 시작이었으면 끊김도 제거
                    RasterizeLine();
                }
                return;
            }
            bool newSeg = e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _linePts.Count > 0;
            _dragIdx = newSeg ? -1 : HitPoint(q.x, q.y);
            if (_dragIdx < 0)
            {
                if (newSeg) _lineBreaks.Add(_linePts.Count);
                _linePts.Add(ClampPt(q.x, q.y, _w, _h));
                _dragIdx = _linePts.Count - 1;
                RasterizeLine();
            }
            return;
        }
        _painting = true;
        if (ToNative(e.GetPosition(this)) is { } n) Stamp(n.x, n.y);
        e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (LineMode)
        {
            if (_dragIdx < 0 || _dragIdx >= _linePts.Count) return;
            if (ToNative(e.GetPosition(this)) is { } q) { _linePts[_dragIdx] = ClampPt(q.x, q.y, _w, _h); RasterizeLine(); }
            return;
        }
        if (!_painting) return;
        if (ToNative(e.GetPosition(this)) is { } n) Stamp(n.x, n.y);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { _painting = false; _dragIdx = -1; }
    protected override void OnPointerExited(PointerEventArgs e) { _painting = false; _dragIdx = -1; }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        if (_base is null || _overlay is null) return;
        var r = ImageRect();
        ctx.DrawImage(_base, new Rect(0, 0, _w, _h), r);
        ctx.DrawImage(_overlay, new Rect(0, 0, _w, _h), r);

        // 선 라벨: 노란 중심선 + 점 핸들(표시 좌표).
        if (_linePts.Count == 0) return;
        double sx = r.Width / _w, sy = r.Height / _h;
        Point D(Point p) => new(r.X + (p.X + 0.5) * sx, r.Y + (p.Y + 0.5) * sy);
        var pen = new Pen(Brushes.Yellow, 1.5);
        for (var i = 0; i + 1 < _linePts.Count; i++)
            if (!_lineBreaks.Contains(i + 1)) ctx.DrawLine(pen, D(_linePts[i]), D(_linePts[i + 1]));
        // 새 선의 시작점은 테두리 원으로 구분.
        var startPen = new Pen(Brushes.Yellow, 2);
        for (var i = 0; i < _linePts.Count; i++)
            if (_lineBreaks.Contains(i)) ctx.DrawEllipse(null, startPen, D(_linePts[i]), 6, 6);
            else ctx.DrawEllipse(Brushes.Yellow, null, D(_linePts[i]), 4, 4);
    }
}
