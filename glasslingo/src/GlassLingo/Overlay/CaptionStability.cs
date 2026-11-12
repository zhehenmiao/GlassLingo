using System;
using System.Windows;

namespace GlassLingo.Overlay
{
    // One fixed type size and anchor while a caption streams. New OCR evidence is
    // counted once, never once per animation frame. Real layout changes still follow.
    internal sealed class CaptionStability
    {
        private object? _evidence;
        private double _scale, _font, _x, _y;
        private int _width, _height, _fontChanges, _moves;
        private double _candidateFont, _candidateX, _candidateY;
        internal (double Font, double X, double Y) Observe(object evidence, Int32Rect region, double scale, double font, double x, double y)
        {
            if (_font == 0 || region.Width != _width || region.Height != _height || Math.Abs(_scale - scale) > .01)
            { _font = font; _x = x; _y = y; _width = region.Width; _height = region.Height; _scale = scale; _fontChanges = _moves = 0; _evidence = evidence; }
            else if (!ReferenceEquals(_evidence, evidence))
            {
                _evidence = evidence;
                if (Math.Abs(font - _font) > Math.Max(3, _font * .2))
                {
                    _fontChanges = Math.Abs(font - _candidateFont) <= 2 ? _fontChanges + 1 : 1; _candidateFont = font;
                    if (_fontChanges >= 3) { _font = font; _fontChanges = 0; }
                }
                else _fontChanges = 0;
                double tolerance = Math.Max(8, _font * .6);
                if (Math.Abs(x - _x) > tolerance || Math.Abs(y - _y) > tolerance)
                {
                    _moves = Math.Abs(x - _candidateX) < tolerance && Math.Abs(y - _candidateY) < tolerance ? _moves + 1 : 1;
                    _candidateX = x; _candidateY = y;
                    if (_moves >= 3) { _x = x; _y = y; _moves = 0; }
                }
                else _moves = 0;
            }
            return (_font, _x, _y);
        }
    }
}
