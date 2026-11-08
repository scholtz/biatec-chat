import 'package:flutter/material.dart';

import 'motion.dart';

/// A ring that empties over a period, with the seconds left written inside: the time is never only a colour or a shape. The ring glides between the once-a-second values it is given; under reduce-motion it jumps instead.
class CountdownRing extends StatefulWidget {
  const CountdownRing({
    super.key,
    required this.fraction,
    required this.seconds,
    required this.urgent,
  });

  /// From 1 (just started) to 0 (about to change).
  final double fraction;
  final int seconds;
  final bool urgent;

  @override
  State<CountdownRing> createState() => _CountdownRingState();
}

class _CountdownRingState extends State<CountdownRing> {
  bool _snap = false;

  @override
  void didUpdateWidget(CountdownRing old) {
    super.didUpdateWidget(old);
    // A new period: the fraction goes up. It snaps to full instead of refilling slowly.
    _snap = widget.fraction > old.fraction;
  }

  @override
  Widget build(BuildContext context) {
    final fraction = widget.fraction;
    final seconds = widget.seconds;
    final urgent = widget.urgent;
    final theme = Theme.of(context);
    final color = urgent ? theme.colorScheme.error : theme.colorScheme.primary;
    final track = theme.colorScheme.surfaceContainerHighest;
    Widget ring(double value) => CircularProgressIndicator(
          value: value,
          strokeWidth: 4,
          color: color,
          backgroundColor: track,
        );
    // The seconds are spoken by the screen's own announcements (10 s, 5 s, renewal), not by this node every second.
    return ExcludeSemantics(
      child: SizedBox(
        width: 48,
        height: 48,
        child: Stack(alignment: Alignment.center, children: [
          SizedBox(
            width: 40,
            height: 40,
            child: Motion.reduced(context)
                ? ring(fraction)
                : TweenAnimationBuilder<double>(
                    tween: Tween<double>(end: fraction),
                    duration: _snap ? Duration.zero : Motion.ring,
                    curve: Curves.linear,
                    builder: (_, value, __) => ring(value),
                  ),
          ),
          Text('$seconds',
              style: theme.textTheme.labelLarge?.copyWith(
                  color: color,
                  fontWeight: FontWeight.w700,
                  fontFeatures: const [FontFeature.tabularFigures()])),
        ]),
      ),
    );
  }
}
