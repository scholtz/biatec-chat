import 'package:flutter/material.dart';

/// The mark of Biatec Authenticator, drawn as a vector so it is sharp at any size and follows light and dark: a shield with a check. It is decoration (the name is always written next to it), so it is hidden from screen readers.
class BrandMark extends StatelessWidget {
  const BrandMark({super.key, this.size = 72, this.color, this.onColor});

  final double size;

  /// The shield; the theme's primary colour when null.
  final Color? color;

  /// The check; the theme's on-primary colour when null.
  final Color? onColor;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    return ExcludeSemantics(
      child: CustomPaint(
        size: Size.square(size),
        painter: BrandMarkPainter(
            color: color ?? scheme.primary,
            onColor: onColor ?? scheme.onPrimary),
      ),
    );
  }
}

class BrandMarkPainter extends CustomPainter {
  const BrandMarkPainter({required this.color, required this.onColor});

  final Color color;
  final Color onColor;

  /// The shield, in a 100 x 100 box.
  static Path shield() => Path()
    ..moveTo(50, 6)
    ..lineTo(88, 20)
    ..lineTo(88, 48)
    ..cubicTo(88, 72, 72, 88, 50, 96)
    ..cubicTo(28, 88, 12, 72, 12, 48)
    ..lineTo(12, 20)
    ..close();

  /// The check, in the same box.
  static Path check() => Path()
    ..moveTo(31, 50)
    ..lineTo(45, 64)
    ..lineTo(70, 36);

  @override
  void paint(Canvas canvas, Size size) {
    canvas.save();
    canvas.scale(size.width / 100, size.height / 100);
    canvas.drawPath(shield(), Paint()..color = color);
    canvas.drawPath(
        check(),
        Paint()
          ..color = onColor
          ..style = PaintingStyle.stroke
          ..strokeWidth = 9
          ..strokeCap = StrokeCap.round
          ..strokeJoin = StrokeJoin.round);
    canvas.restore();
  }

  @override
  bool shouldRepaint(BrandMarkPainter old) =>
      old.color != color || old.onColor != onColor;
}
