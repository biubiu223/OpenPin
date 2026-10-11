namespace OpenPin.Annotation.Model;

internal enum LineDash { Solid, Dash, Dot }

/// <summary>Arrow head look (ANN-04).</summary>
internal enum ArrowStyle
{
    /// <summary>Filled, tapering from a thin tail to a wide head (default).</summary>
    Tapered,
    /// <summary>Uniform line + filled triangular head.</summary>
    Solid,
    /// <summary>Uniform line + open "V" head.</summary>
    Line,
    /// <summary>Uniform line + filled heads on both ends.</summary>
    Double,
}

/// <summary>End decoration of a line / polyline (ANN-03).</summary>
internal enum LineEnd { None, Arrow, OpenArrow, Dot }

internal enum LineJoinStyle { Round, Miter, Bevel }
internal enum LineCapStyle { Round, Flat, Square }

internal enum SerialType { Number, Letter, Roman }

/// <summary>Brush (freehand) or rectangle mode for highlighter / mosaic / eraser.</summary>
internal enum DrawMode
{
    Brush,
    Rect,
    /// <summary>Eraser only: erase parts of annotations under the brush (ANN-10 P1).</summary>
    Partial,
}

internal enum HighlightBlend { Multiply, Translucent }

internal enum MosaicEffect { Mosaic, Blur }

internal enum ShapeKind { Rectangle, Ellipse }

/// <summary>Ellipse variants (ANN-02 P1).</summary>
internal enum EllipseVariant { Full, Sector, Arc }

internal enum ConnectorStyle { None, Solid, Dashed }

internal enum WatermarkLayout { Tile, TopLeft, TopRight, BottomLeft, BottomRight, TopCenter, BottomCenter, Center }
