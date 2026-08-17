// =============================================================================
// PointsChart.cs
// "Points by hand": four score trajectories over the course of a game.
//
// Four lines on one chart is exactly the case where colour alone fails, so each
// line is separated three ways: by stroke width, by dash pattern, and by a
// label sitting at its own end point. Any one of the three is enough to follow
// a line from left to right, so the chart still works in greyscale and still
// works if two players' colours are indistinguishable to the reader.
//
// The end labels also remove the need for a legend, which would otherwise force
// the eye to travel between the key and the lines to answer "which one am I".
// =============================================================================

using Godot;
using System;
using System.Collections.Generic;

namespace RiichiMahjong.UI
{
    public partial class PointsChart : Control
    {
        private const int GridLines     = 3;
        private const int LabelGutter   = 92;   // room at the right for the end labels
        private const int AxisHeight    = 24;   // room at the bottom for the hand axis
        private const float PointRadius = 3f;

        // Stroke width and dash pattern per seat. Both differ, so either alone
        // distinguishes the four lines.
        private static readonly float[] StrokeWidths = { 3.5f, 2.5f, 2.0f, 1.5f };

        private static readonly float[][] DashPatterns =
        {
            Array.Empty<float>(),        // solid
            new[] { 12f, 6f },           // long dash
            new[] { 4f, 4f },            // dotted
            new[] { 16f, 4f, 3f, 4f },   // dash-dot
        };

        private static readonly Color[] LineColors =
        {
            DoloTokens.DoraGold,
            DoloTokens.Ivory,
            DoloTokens.BodyText,
            DoloTokens.DimText,
        };

        private IReadOnlyList<int[]> _series = Array.Empty<int[]>();
        private string[]             _names  = { "", "", "", "" };
        private string[]             _labels = Array.Empty<string>();

        /// <summary>
        /// Set the chart data.
        /// <paramref name="series"/> is one entry per point in time, each holding four
        /// seat totals; the first entry is the starting score.
        /// <paramref name="handLabels"/> names the x positions, e.g. E1..E4.
        /// </summary>
        public void SetData(IReadOnlyList<int[]> series, string[] names, string[] handLabels)
        {
            _series = series;
            _names  = names;
            _labels = handLabels;
            QueueRedraw();
        }

        public override void _Ready()
        {
            MouseFilter = MouseFilterEnum.Ignore;
            Resized += QueueRedraw;
        }

        public override void _Draw()
        {
            if (_series.Count < 2) return;

            float plotWidth  = Size.X - LabelGutter;
            float plotHeight = Size.Y - AxisHeight;
            if (plotWidth <= 0 || plotHeight <= 0) return;

            var font = DoloTheme.MonoFont;

            // ---- Scale ----
            int min = int.MaxValue, max = int.MinValue;
            foreach (var point in _series)
                for (int seat = 0; seat < 4; seat++)
                {
                    min = Math.Min(min, point[seat]);
                    max = Math.Max(max, point[seat]);
                }

            // A flat game would divide by zero; give it a nominal band instead.
            if (max - min < 1000) { min -= 500; max += 500; }

            float Y(int score) => plotHeight - (score - min) / (float)(max - min) * plotHeight;
            float X(int index) => _series.Count > 1
                ? index / (float)(_series.Count - 1) * plotWidth
                : 0f;

            // ---- Gridlines ----
            for (int i = 0; i < GridLines; i++)
            {
                int   value = min + (max - min) * i / (GridLines - 1);
                float y     = Y(value);

                DrawLine(new Vector2(0, y), new Vector2(plotWidth, y),
                         DoloTokens.Hairline(0.14f), 1f);

                if (font != null)
                    DrawString(font, new Vector2(2, y - 4), $"{value:N0}",
                               HorizontalAlignment.Left, -1,
                               DoloTokens.SizeMonoSmall, DoloTokens.MonoDim);
            }

            // ---- Hand axis ----
            if (font != null)
                for (int i = 0; i < _labels.Length && i + 1 < _series.Count; i++)
                    DrawString(font, new Vector2(X(i + 1) - 8, Size.Y - 6), _labels[i],
                               HorizontalAlignment.Left, -1,
                               DoloTokens.SizeMonoSmall, DoloTokens.MonoDim);

            // ---- One line per seat ----
            for (int seat = 0; seat < 4; seat++)
            {
                var points = new Vector2[_series.Count];
                for (int i = 0; i < _series.Count; i++)
                    points[i] = new Vector2(X(i), Y(_series[i][seat]));

                DrawSeries(points, LineColors[seat], StrokeWidths[seat], DashPatterns[seat]);

                // The end label: which line this is, said at the line itself.
                if (font == null) continue;
                var end = points[^1];
                DrawCircle(end, PointRadius, LineColors[seat]);
                DrawString(font, new Vector2(plotWidth + 8, end.Y + 4),
                           Truncate(_names[seat], 10),
                           HorizontalAlignment.Left, -1,
                           DoloTokens.SizeMonoSmall, LineColors[seat]);
            }
        }

        /// <summary>Draw a polyline solid, or as the given dash pattern.</summary>
        private void DrawSeries(Vector2[] points, Color color, float width, float[] pattern)
        {
            if (pattern.Length == 0)
            {
                DrawPolyline(points, color, width);
                return;
            }

            int   patternIndex = 0;
            float remaining    = pattern[0];
            bool  penDown      = true;

            for (int i = 0; i + 1 < points.Length; i++)
            {
                var from = points[i];
                var to   = points[i + 1];
                float segmentLength = from.DistanceTo(to);
                if (segmentLength <= 0f) continue;

                var direction = (to - from) / segmentLength;
                float travelled = 0f;

                while (travelled < segmentLength)
                {
                    float step = Math.Min(remaining, segmentLength - travelled);
                    if (penDown)
                        DrawLine(from + direction * travelled,
                                 from + direction * (travelled + step), color, width);

                    travelled += step;
                    remaining -= step;

                    if (remaining <= 0.001f)
                    {
                        patternIndex = (patternIndex + 1) % pattern.Length;
                        remaining    = pattern[patternIndex];
                        penDown      = !penDown;
                    }
                }
            }
        }

        private static string Truncate(string text, int max)
            => text.Length <= max ? text : text[..(max - 1)] + "…";
    }
}
