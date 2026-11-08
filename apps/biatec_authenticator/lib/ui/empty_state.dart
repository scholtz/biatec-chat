import 'dart:math' show pi;

import 'package:flutter/material.dart';

import '../theme.dart';
import 'scrollable_center.dart';

/// Nothing to show yet: says what this place is for and, when there is one, what to do next.
class EmptyState extends StatelessWidget {
  const EmptyState({
    super.key,
    required this.icon,
    required this.title,
    this.message,
    this.actionLabel,
    this.onAction,
  });

  final IconData icon;
  final String title;
  final String? message;
  final String? actionLabel;
  final VoidCallback? onAction;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return ScrollableCenter(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          ExcludeSemantics(
            child: CustomPaint(
              painter: _Backdrop(
                  ring: theme.colorScheme.outlineVariant,
                  dot: theme.colorScheme.primary),
              child: SizedBox(
                width: 112,
                height: 112,
                child: Center(
                  child: Container(
                    width: 72,
                    height: 72,
                    decoration: BoxDecoration(
                      color: theme.colorScheme.secondaryContainer,
                      shape: BoxShape.circle,
                    ),
                    child: Icon(icon,
                        size: 36,
                        color: theme.colorScheme.onSecondaryContainer),
                  ),
                ),
              ),
            ),
          ),
          const SizedBox(height: AppSpacing.m),
          Semantics(
            header: true,
            child: Text(title,
                textAlign: TextAlign.center,
                style: theme.textTheme.titleMedium),
          ),
          if (message != null) ...[
            const SizedBox(height: AppSpacing.s),
            Text(message!,
                textAlign: TextAlign.center,
                style: theme.textTheme.bodyMedium
                    ?.copyWith(color: theme.colorScheme.onSurfaceVariant)),
          ],
          if (actionLabel != null && onAction != null) ...[
            const SizedBox(height: AppSpacing.l),
            FilledButton(onPressed: onAction, child: Text(actionLabel!)),
          ],
        ],
      ),
    );
  }
}

/// A small vector illustration behind the icon: a dashed orbit with two dots. Static.
class _Backdrop extends CustomPainter {
  const _Backdrop({required this.ring, required this.dot});

  final Color ring;
  final Color dot;

  @override
  void paint(Canvas canvas, Size size) {
    final c = size.center(Offset.zero);
    final r = size.width / 2 - 2;
    final stroke = Paint()
      ..color = ring
      ..style = PaintingStyle.stroke
      ..strokeWidth = 2;
    const dashes = 24;
    for (var i = 0; i < dashes; i++) {
      final start = i * 2 * pi / dashes;
      canvas.drawArc(Rect.fromCircle(center: c, radius: r), start, pi / dashes,
          false, stroke);
    }
    final fill = Paint()..color = dot;
    canvas.drawCircle(c + Offset(r * 0.71, -r * 0.71), 5, fill);
    canvas.drawCircle(c + Offset(-r * 0.92, r * 0.38), 3, fill);
  }

  @override
  bool shouldRepaint(_Backdrop old) => old.ring != ring || old.dot != dot;
}
