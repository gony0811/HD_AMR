using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using HD.AMR.App.Models;
using HD.AMR.App.Service;

namespace HD.AMR.Desktop.Views;

/// <summary>
/// 평탄면 검출 스냅샷을 정지 화면 위 캔버스에 그린다 — 카메라 페이지와 시퀀스 모니터가 공유.
/// 셀 σ 히트맵(유효 셀 안에서 상대 비교), 검출 영역, ROI·중심 십자선, 2순위 셀(얇은 점선),
/// 선택 셀(굵은 테두리), ROI 중심→선택 셀 이동 벡터.
/// </summary>
internal static class FlatOverlayRenderer
{
    private const double CrossArm = 8;
    private static readonly IBrush RoiBrush = new SolidColorBrush(Color.Parse("#FFD000"));
    private static readonly IBrush PickBrush = new SolidColorBrush(Color.Parse("#5DCAA5"));
    private static readonly IBrush GridLineBrush = new SolidColorBrush(Color.Parse("#59FFFFFF"));
    private static readonly IBrush InvalidBrush = new SolidColorBrush(Color.Parse("#99888780"));
    // σ 상대 순위 4단계(평탄 → 거침). 반투명이라 아래 깊이 컬러맵이 비친다.
    private static readonly IBrush[] SigmaBrushes =
    {
        new SolidColorBrush(Color.Parse("#803B6D11")),
        new SolidColorBrush(Color.Parse("#7397C459")),
        new SolidColorBrush(Color.Parse("#73EF9F27")),
        new SolidColorBrush(Color.Parse("#73E24B4A")),
    };

    public static void Draw(Canvas canvas, FlatDetectionSnapshot? snap, Size size)
    {
        canvas.Children.Clear();
        if (snap is null || size.Width <= 0 || size.Height <= 0) return;
        double w = size.Width, h = size.Height;
        var a = snap.Analysis;

        double minSigma = double.MaxValue, maxSigma = 0;
        foreach (var c in a.Cells)
        {
            if (!c.Valid) continue;
            minSigma = Math.Min(minSigma, c.SigmaMm);
            maxSigma = Math.Max(maxSigma, c.SigmaMm);
        }
        double span = maxSigma - minSigma;

        foreach (var c in a.Cells)
        {
            IBrush fill = InvalidBrush;
            if (c.Valid)
            {
                var t = span > 1e-6 ? (c.SigmaMm - minSigma) / span : 0;
                fill = SigmaBrushes[Math.Min(SigmaBrushes.Length - 1, (int)(t * SigmaBrushes.Length))];
            }
            AddRect(canvas, c.U0 * w, c.V0 * h, (c.U1 - c.U0) * w, (c.V1 - c.V0) * h, fill, GridLineBrush, 0.5);
        }

        // 검출 영역이 전체 ROI 와 다르면(폐루프 2회차 이후 중앙 축소 재검출) 점선으로 구분.
        bool refined = Math.Abs(a.RoiW - snap.FullRoiW) > 1e-6 || Math.Abs(a.RoiH - snap.FullRoiH) > 1e-6;
        if (refined)
            AddRect(canvas, a.RoiX * w, a.RoiY * h, a.RoiW * w, a.RoiH * h, null, RoiBrush, 1, dashed: true);
        AddRect(canvas, snap.FullRoiX * w, snap.FullRoiY * h, snap.FullRoiW * w, snap.FullRoiH * h, null, RoiBrush, 1.5);

        double cx = (snap.FullRoiX + snap.FullRoiW / 2) * w;
        double cy = (snap.FullRoiY + snap.FullRoiH / 2) * h;
        AddLine(canvas, cx - CrossArm, cy, cx + CrossArm, cy, RoiBrush, 1.5);
        AddLine(canvas, cx, cy - CrossArm, cx, cy + CrossArm, RoiBrush, 1.5);

        if (a.SecondIndex >= 0)
        {
            var c2 = a.Cells[a.SecondIndex];
            AddRect(canvas, c2.U0 * w, c2.V0 * h, (c2.U1 - c2.U0) * w, (c2.V1 - c2.V0) * h, null, PickBrush, 1, dashed: true);
        }
        var best = a.Cells[a.BestIndex];
        AddRect(canvas, best.U0 * w, best.V0 * h, (best.U1 - best.U0) * w, (best.V1 - best.V0) * h, null, PickBrush, 3);

        // 이동 벡터: ROI 중심 → 선택 셀 중심 (코봇이 이 방향으로 옮겨 셀을 중심에 맞춘다).
        double bx = a.Best.U * w, by = a.Best.V * h;
        double dx = bx - cx, dy = by - cy, len = Math.Sqrt(dx * dx + dy * dy);
        if (len > 6)
        {
            AddLine(canvas, cx, cy, bx, by, PickBrush, 2);
            double ux = dx / len, uy = dy / len, head = 7;
            canvas.Children.Add(new Polygon
            {
                Fill = PickBrush,
                Points = new AvaloniaList<Point>
                {
                    new(bx, by),
                    new(bx - ux * head - uy * head * 0.5, by - uy * head + ux * head * 0.5),
                    new(bx - ux * head + uy * head * 0.5, by - uy * head - ux * head * 0.5),
                },
            });
        }
        var dot = new Ellipse { Width = 5, Height = 5, Fill = PickBrush };
        Canvas.SetLeft(dot, bx - 2.5);
        Canvas.SetTop(dot, by - 2.5);
        canvas.Children.Add(dot);
    }

    /// <summary>프레임 픽셀 ROI 를 캔버스 크기에 맞춰 사각형 하나로 그린다(라이브 영상 위 ROI 표시용).</summary>
    public static void DrawRoi(Canvas canvas, RoiRect? roi, int frameW, int frameH, Size size, IBrush stroke)
    {
        canvas.Children.Clear();
        if (roi is null || frameW <= 0 || frameH <= 0 || size.Width <= 0 || size.Height <= 0) return;
        double sx = size.Width / frameW, sy = size.Height / frameH;
        AddRect(canvas, roi.X * sx, roi.Y * sy, roi.Width * sx, roi.Height * sy, null, stroke, 2);
    }

    private static void AddRect(Canvas canvas, double x, double y, double w, double h, IBrush? fill, IBrush stroke,
        double thickness, bool dashed = false)
    {
        var r = new Rectangle
        {
            Width = Math.Max(0, w), Height = Math.Max(0, h),
            Fill = fill, Stroke = stroke, StrokeThickness = thickness,
        };
        if (dashed) r.StrokeDashArray = new AvaloniaList<double> { 4, 3 };
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        canvas.Children.Add(r);
    }

    private static void AddLine(Canvas canvas, double x0, double y0, double x1, double y1, IBrush stroke, double thickness)
        => canvas.Children.Add(new Line
        {
            StartPoint = new Point(x0, y0), EndPoint = new Point(x1, y1),
            Stroke = stroke, StrokeThickness = thickness,
        });
}
