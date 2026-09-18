namespace HD.AMR.App.Models;

/// <summary>AMR REST가 제공하는 SLAM 맵 좌표의 라이다 점.</summary>
public readonly record struct AmrLidarPoint(double X, double Y);
