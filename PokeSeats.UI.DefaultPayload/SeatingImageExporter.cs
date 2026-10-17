using System.Globalization;
using System.Numerics;
using PokeSeats.IR.Base;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PokeSeats.UI.DefaultPayload;

/// <summary>Renders a solved seating layout into an opaque PNG without any UI or GPU dependency.</summary>
internal static class SeatingImageExporter
{
    private const string FontResourceName = "PokeSeats.UI.DefaultPayload.Fonts.HarmonyOS_Sans_SC_Regular.ttf";

    private const float Margin = 28f;
    private const float CellPadding = 12f;
    private const float CellFontSize = 20f;
    private const int MinCellFontSize = 1;
    private const float CellLineHeightFactor = 1.32f;
    private const float HeaderFontSize = 20f;
    private const float TitleFontSize = 32f;
    private const float TitleLineHeightFactor = 1.22f;
    private const float TitleGap = 22f;
    private const float BorderWidth = 1f;

    private const int MinCellWidth = 180;
    private const int MinCellHeight = 100;
    private const int MaxCellWidth = 220;
    private const int MaxCellHeight = 160;

    private static readonly FontCollection EmbeddedFontCollection = new();
    private static readonly Lazy<FontFamily> EmbeddedFontFamily =
        new(LoadEmbeddedFontFamily, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Writes the chart to <paramref name="path"/>. Cells are addressed by their original layout
    /// position; <see cref="SeatingImageOptions.PodiumAtBottom"/> rotates the display only.
    /// </summary>
    public static void SavePng(
        string path,
        LayoutGrid layout,
        IReadOnlyDictionary<GridPos, Person> occupants,
        SeatingImageOptions options)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A destination path is required.", nameof(path));
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(occupants);
        ArgumentNullException.ThrowIfNull(options);
        if (layout.Rows <= 0 || layout.Cols <= 0)
            throw new ArgumentException("The layout must contain at least one cell.", nameof(layout));

        FontFamily family = EmbeddedFontFamily.Value;
        Font measureFont = family.CreateFont(CellFontSize, FontStyle.Regular);
        var cells = new CellInfo[layout.Rows * layout.Cols];
        float widestLine = 0f;

        int cellIndex = 0;
        for (int displayRow = 0; displayRow < layout.Rows; displayRow++)
        {
            for (int displayCol = 0; displayCol < layout.Cols; displayCol++)
            {
                GridPos position = options.SourcePosition(layout, displayRow, displayCol);
                CellType type = layout.TypeAt(position);
                occupants.TryGetValue(position, out Person? person);
                string[] lines = SplitLines(options.GetText(position, type, person));
                cells[cellIndex++] = new CellInfo(options.GetColor(position, type, person), lines);

                foreach (string line in lines)
                    widestLine = Math.Max(widestLine, MeasureWidth(line, measureFont));
            }
        }

        int cellWidth = (int)MathF.Ceiling(Math.Clamp(
            widestLine + CellPadding * 2f, MinCellWidth, MaxCellWidth));

        int wrappedLineCount = 1;
        float cellTextWidth = cellWidth - CellPadding * 2f;
        foreach (CellInfo cell in cells)
        {
            List<string> wrapped = WrapLines(cell.Lines, measureFont, cellTextWidth);
            cell.BaseWrapped = wrapped;
            wrappedLineCount = Math.Max(wrappedLineCount, wrapped.Count);
        }

        int cellHeight = (int)MathF.Ceiling(Math.Clamp(
            wrappedLineCount * CellFontSize * CellLineHeightFactor + CellPadding * 2f,
            MinCellHeight,
            MaxCellHeight));

        Font headerFont = family.CreateFont(HeaderFontSize, FontStyle.Regular);
        var rowLabels = new string[layout.Rows];
        var columnLabels = new string[layout.Cols];
        float rowLabelWidth = 0f;
        float columnLabelHeight = 0f;
        for (int displayRow = 0; displayRow < layout.Rows; displayRow++)
        {
            int sourceRow = options.PodiumAtBottom ? layout.Rows - 1 - displayRow : displayRow;
            rowLabels[displayRow] = string.Format(PayloadStrings.SeatingRowHeaderFormat, sourceRow + 1);
            rowLabelWidth = MathF.Max(rowLabelWidth, MeasureWidth(rowLabels[displayRow], headerFont));
        }
        for (int displayCol = 0; displayCol < layout.Cols; displayCol++)
        {
            int sourceCol = options.PodiumAtBottom ? layout.Cols - 1 - displayCol : displayCol;
            columnLabels[displayCol] = string.Format(PayloadStrings.SeatingColumnHeaderFormat, sourceCol + 1);
            columnLabelHeight = MathF.Max(columnLabelHeight, MeasureHeight(columnLabels[displayCol], headerFont));
        }

        float rowHeaderWidth = options.ShowRowNumbers ? MathF.Max(56f, rowLabelWidth + 16f) : 0f;
        float columnHeaderHeight = options.ShowColumnNumbers ? MathF.Max(40f, columnLabelHeight + 14f) : 0f;
        float gridWidth = cellWidth * layout.Cols;
        float pageWidth = Margin * 2f + rowHeaderWidth + gridWidth;
        float titleWidth = MathF.Max(160f, pageWidth - Margin * 2f);

        List<string> titleLines = new();
        float titleSize = 0f;
        float titleLineHeight = 0f;
        if (!string.IsNullOrWhiteSpace(options.Title))
        {
            titleSize = TitleFontSize;
            while (true)
            {
                Font titleFont = family.CreateFont(titleSize, FontStyle.Regular);
                titleLines = WrapLines(SplitLines(options.Title), titleFont, titleWidth);
                titleLineHeight = titleSize * TitleLineHeightFactor;
                if (titleSize <= 18f || titleLines.Count <= 2 || titleLines.Count * titleLineHeight <= 120f)
                    break;
                titleSize -= 4f;
            }
        }

        float titleBlockHeight = titleLines.Count * titleLineHeight;
        float titleSpacing = titleLines.Count > 0 ? TitleGap : 0f;
        float gridLeft = Margin + rowHeaderWidth;
        float gridTop = Margin + titleBlockHeight + titleSpacing + columnHeaderHeight;

        int imageWidth = (int)MathF.Ceiling(pageWidth);
        int imageHeight = (int)MathF.Ceiling(gridTop + cellHeight * layout.Rows + Margin);

        Color textColor = ToColor(options.TextColor);
        Color borderColor = ToColor(options.BorderColor);
        using var image = new Image<Rgba32>(imageWidth, imageHeight, ToPixel(options.Background));

        image.Mutate(context =>
        {
            if (titleLines.Count > 0)
            {
                Font titleFont = family.CreateFont(titleSize, FontStyle.Regular);
                float titleY = Margin;
                foreach (string line in titleLines)
                {
                    if (line.Length > 0)
                    {
                        float titleX = (imageWidth - MeasureWidth(line, titleFont)) / 2f;
                        context.DrawText(line, titleFont, textColor, new PointF(titleX, titleY));
                    }
                    titleY += titleLineHeight;
                }
            }

            if (options.ShowColumnNumbers)
            {
                float labelHeight = MeasureHeight(columnLabels[0], headerFont);
                float headerY = Margin + titleBlockHeight + titleSpacing + (columnHeaderHeight - labelHeight) / 2f;
                for (int displayCol = 0; displayCol < layout.Cols; displayCol++)
                {
                    float labelWidth = MeasureWidth(columnLabels[displayCol], headerFont);
                    float headerX = gridLeft + displayCol * cellWidth + (cellWidth - labelWidth) / 2f;
                    context.DrawText(columnLabels[displayCol], headerFont, textColor, new PointF(headerX, headerY));
                }
            }

            var fontCache = new Dictionary<int, Font>();
            for (int displayRow = 0; displayRow < layout.Rows; displayRow++)
            {
                float rowTop = gridTop + displayRow * cellHeight;
                if (options.ShowRowNumbers)
                {
                    float labelWidth = MeasureWidth(rowLabels[displayRow], headerFont);
                    float labelHeight = MeasureHeight(rowLabels[displayRow], headerFont);
                    float labelX = Margin + (rowHeaderWidth - labelWidth) / 2f;
                    float labelY = rowTop + (cellHeight - labelHeight) / 2f;
                    context.DrawText(rowLabels[displayRow], headerFont, textColor, new PointF(labelX, labelY));
                }

                for (int displayCol = 0; displayCol < layout.Cols; displayCol++)
                {
                    CellInfo cell = cells[displayRow * layout.Cols + displayCol];
                    float cellX = gridLeft + displayCol * cellWidth;
                    var bounds = new RectangleF(cellX, rowTop, cellWidth, cellHeight);
                    context.Fill(ToColor(cell.Fill), bounds);
                    context.Draw(borderColor, BorderWidth, bounds);
                    DrawCellText(context, cell, measureFont, family, bounds, textColor, fontCache);
                }
            }
        });

        image.SaveAsPng(path, new PngEncoder { ColorType = PngColorType.Rgb });
    }

    private static FontFamily LoadEmbeddedFontFamily()
    {
        using Stream stream = typeof(SeatingImageExporter).Assembly.GetManifestResourceStream(FontResourceName)
            ?? throw new InvalidOperationException($"Embedded font '{FontResourceName}' was not found.");
        return EmbeddedFontCollection.Add(stream);
    }

    private static void DrawCellText(
        IImageProcessingContext context,
        CellInfo cell,
        Font baseFont,
        FontFamily family,
        RectangleF bounds,
        Color color,
        Dictionary<int, Font> fontCache)
    {
        bool hasContent = false;
        foreach (string line in cell.Lines)
        {
            if (line.Length == 0) continue;
            hasContent = true;
            break;
        }
        if (!hasContent)
            return;

        float availableWidth = bounds.Width - CellPadding * 2f;
        float availableHeight = bounds.Height - CellPadding * 2f;
        if (availableWidth <= 1f || availableHeight <= 1f)
            return;

        int size = (int)CellFontSize;
        Font font = baseFont;
        List<string> lines = cell.BaseWrapped ?? WrapLines(cell.Lines, font, availableWidth);
        while (size > MinCellFontSize && lines.Count * size * CellLineHeightFactor > availableHeight)
        {
            size--;
            font = GetFont(family, size, fontCache);
            lines = WrapLines(cell.Lines, font, availableWidth);
        }

        float lineHeight = size * CellLineHeightFactor;
        float lineY = bounds.Top + (bounds.Height - lines.Count * lineHeight) / 2f;
        if (lines.Count * lineHeight <= availableHeight + .5f)
        {
            DrawTextLines(context, lines, font, lineHeight, lineY, bounds, color);
            return;
        }

        // Pathological amounts of text cannot shrink below one pixel; confine the block to the cell.
        var clipBounds = new RectangleF(
            bounds.X + CellPadding,
            bounds.Y + CellPadding,
            availableWidth,
            availableHeight);
        context.Clip(
            new RectangularPolygon(clipBounds),
            clipped => DrawTextLines(clipped, lines, font, lineHeight, lineY, bounds, color));
    }

    private static void DrawTextLines(
        IImageProcessingContext context,
        List<string> lines,
        Font font,
        float lineHeight,
        float startY,
        RectangleF bounds,
        Color color)
    {
        float lineY = startY;
        foreach (string line in lines)
        {
            if (line.Length > 0)
            {
                float lineWidth = MeasureWidth(line, font);
                float lineX = bounds.Left + (bounds.Width - lineWidth) / 2f;
                context.DrawText(line, font, color, new PointF(lineX, lineY));
            }
            lineY += lineHeight;
        }
    }

    private static Font GetFont(FontFamily family, int size, Dictionary<int, Font> fontCache)
    {
        if (fontCache.TryGetValue(size, out Font? font))
            return font;
        font = family.CreateFont(size, FontStyle.Regular);
        fontCache.Add(size, font);
        return font;
    }

    private static string[] SplitLines(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static List<string> WrapLines(IEnumerable<string> source, Font font, float maxWidth)
    {
        var result = new List<string>();
        foreach (string rawLine in source)
        {
            if (rawLine.Length == 0)
            {
                result.Add(string.Empty);
                continue;
            }
            if (MeasureWidth(rawLine, font) <= maxWidth)
            {
                result.Add(rawLine);
                continue;
            }

            // Grapheme boundaries keep surrogate pairs and combining marks intact.
            int[] starts = StringInfo.ParseCombiningCharacters(rawLine);
            int lineStart = 0;
            int elementStart = 0;
            while (elementStart < starts.Length)
            {
                int low = elementStart;
                int high = starts.Length - 1;
                int lastFit = elementStart - 1;
                while (low <= high)
                {
                    int middle = (low + high) / 2;
                    int end = middle + 1 < starts.Length ? starts[middle + 1] : rawLine.Length;
                    if (MeasureWidth(rawLine.Substring(lineStart, end - lineStart), font) <= maxWidth)
                    {
                        lastFit = middle;
                        low = middle + 1;
                    }
                    else
                    {
                        high = middle - 1;
                    }
                }

                if (lastFit < elementStart)
                    lastFit = elementStart;
                int cut = lastFit + 1 < starts.Length ? starts[lastFit + 1] : rawLine.Length;
                result.Add(rawLine.Substring(lineStart, cut - lineStart));
                lineStart = cut;
                elementStart = lastFit + 1;
            }
        }
        return result;
    }

    private static float MeasureWidth(string text, Font font)
        => text.Length == 0 ? 0f : TextMeasurer.MeasureSize(text, new TextOptions(font)).Width;

    private static float MeasureHeight(string text, Font font)
        => text.Length == 0 ? 0f : TextMeasurer.MeasureSize(text, new TextOptions(font)).Height;

    private static Color ToColor(Vector4 value)
        => Color.FromRgba(ToByte(value.X), ToByte(value.Y), ToByte(value.Z), byte.MaxValue);

    private static Rgba32 ToPixel(Vector4 value)
        => new(ToByte(value.X), ToByte(value.Y), ToByte(value.Z), byte.MaxValue);

    private static byte ToByte(float value)
        => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);

    private sealed class CellInfo
    {
        public CellInfo(Vector4 fill, string[] lines)
        {
            Fill = fill;
            Lines = lines;
        }

        public Vector4 Fill { get; }
        public string[] Lines { get; }
        public List<string>? BaseWrapped { get; set; }
    }
}
