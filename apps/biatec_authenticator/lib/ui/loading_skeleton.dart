import 'package:flutter/material.dart';

import '../theme.dart';

/// Placeholder rows shown while a list loads. Pulses gently, and holds still when the system asks to reduce motion.
class LoadingSkeleton extends StatefulWidget {
  const LoadingSkeleton({super.key, this.rows = 3});

  final int rows;

  @override
  State<LoadingSkeleton> createState() => _LoadingSkeletonState();
}

class _LoadingSkeletonState extends State<LoadingSkeleton>
    with SingleTickerProviderStateMixin {
  late final AnimationController _pulse = AnimationController(
      vsync: this, duration: const Duration(milliseconds: 1100));

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    if (MediaQuery.disableAnimationsOf(context)) {
      _pulse.stop();
    } else if (!_pulse.isAnimating) {
      _pulse.repeat(reverse: true);
    }
  }

  @override
  void dispose() {
    _pulse.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final color = Theme.of(context).colorScheme.surfaceContainerHighest;
    return Semantics(
      label: 'Loading',
      liveRegion: true,
      child: ExcludeSemantics(
        child: FadeTransition(
          opacity: Tween<double>(begin: 0.55, end: 1).animate(_pulse),
          child: Padding(
            padding: const EdgeInsets.all(AppSpacing.m),
            child: Column(
              children: [
                for (var i = 0; i < widget.rows; i++)
                  Padding(
                    padding: const EdgeInsets.only(bottom: AppSpacing.m),
                    child: Row(
                      children: [
                        _Block(
                            color: color, width: 48, height: 48, circle: true),
                        const SizedBox(width: AppSpacing.m),
                        Expanded(
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              _Block(color: color, height: 14),
                              const SizedBox(height: AppSpacing.s),
                              _Block(
                                  color: color, height: 12, widthFactor: 0.6),
                            ],
                          ),
                        ),
                      ],
                    ),
                  ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class _Block extends StatelessWidget {
  const _Block({
    required this.color,
    required this.height,
    this.width,
    this.widthFactor,
    this.circle = false,
  });

  final Color color;
  final double height;
  final double? width;
  final double? widthFactor;
  final bool circle;

  @override
  Widget build(BuildContext context) {
    final box = Container(
      width: width,
      height: height,
      decoration: BoxDecoration(
        color: color,
        shape: circle ? BoxShape.circle : BoxShape.rectangle,
        borderRadius: circle ? null : BorderRadius.circular(AppRadius.s),
      ),
    );
    return widthFactor == null
        ? box
        : FractionallySizedBox(
            alignment: AlignmentDirectional.centerStart,
            widthFactor: widthFactor,
            child: box);
  }
}
