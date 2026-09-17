using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ISImage = SixLabors.ImageSharp.Image;

namespace HD.AMR.Desktop.Views;

/// <summary>
/// DL 비드 마스크 브러시 에디터. 기존 label.js 이식 — 캡처 이미지 위에 반투명 빨강으로 비드 영역을
/// 칠/지우고, 저장 시 흑백 이진 마스크 PNG 를 돌려준다. 마스크는 이미지 원본 해상도로 유지되며 표시는
/// 균등 스케일이므로 저장 마스크는 항상 원본 해상도로 정렬된다.
/// </summary>
public sealed class MaskCanvas : Control
{
    private Bitmap? _base;
    private WriteableBitmap? _overlay;
    private byte[] _mask = Array.Empty<byte>();       // w*h, 0/1
    private byte[] _bgra = Array.Empty<byte>();       // w*h*4 오버레이 픽셀(빨강, alpha 140/0)
    private int _w, _h;
    private bool _painting;

    public bool Erase { get; set; }
    public int Radius { get; set; } = 12;
    public bool HasImage => _base is not null;

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

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (_base is null) return;
        _painting = true;
        if (ToNative(e.GetPosition(this)) is { } n) Stamp(n.x, n.y);
        e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (!_painting) return;
        if (ToNative(e.GetPosition(this)) is { } n) Stamp(n.x, n.y);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) => _painting = false;
    protected override void OnPointerExited(PointerEventArgs e) => _painting = false;

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        if (_base is null || _overlay is null) return;
        var r = ImageRect();
        ctx.DrawImage(_base, new Rect(0, 0, _w, _h), r);
        ctx.DrawImage(_overlay, new Rect(0, 0, _w, _h), r);
    }
}
