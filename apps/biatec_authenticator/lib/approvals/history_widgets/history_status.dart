import 'package:flutter/material.dart';

import '../../theme.dart';

/// How a recorded decision ended, for the chip on a row and the filter. Expired requests are not recorded (the history only keeps decisions that were posted), so there is no such status here.
enum HistoryStatus {
  approved('approve', 'Approved', 'Approved', Icons.check_circle),
  denied('deny', 'Denied', 'Denied', Icons.block),
  refused('code_failed', 'Refused', 'Refused after three wrong codes',
      Icons.gpp_bad);

  const HistoryStatus(this.wire, this.label, this.longLabel, this.icon);

  /// The decision as the store keeps it.
  final String wire;

  /// A word for a chip.
  final String label;

  /// A sentence part for the detail and for a screen reader.
  final String longLabel;
  final IconData icon;

  /// The status of a stored decision, or null for one this version does not know (it is still listed, under All).
  static HistoryStatus? of(String decision) {
    for (final s in values) {
      if (s.wire == decision) return s;
    }
    return null;
  }
}

/// The word for a stored decision, known or not.
String statusLabel(String decision) =>
    HistoryStatus.of(decision)?.longLabel ?? decision;

/// An icon and a word, so a status is never colour alone.
class StatusChip extends StatelessWidget {
  const StatusChip({super.key, required this.decision});

  final String decision;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colors = context.statusColors;
    final status = HistoryStatus.of(decision);
    final (background, foreground) = switch (status) {
      HistoryStatus.approved => (colors.success, colors.onSuccess),
      HistoryStatus.denied => (colors.info, colors.onInfo),
      HistoryStatus.refused => (colors.danger, colors.onDanger),
      null => (colors.warning, colors.onWarning),
    };
    return DecoratedBox(
      decoration: BoxDecoration(
        color: background,
        borderRadius: BorderRadius.circular(AppRadius.l),
      ),
      child: Padding(
        padding: const EdgeInsets.symmetric(
            horizontal: AppSpacing.s + 2, vertical: AppSpacing.xs),
        child: Row(mainAxisSize: MainAxisSize.min, children: [
          ExcludeSemantics(
              child: Icon(status?.icon ?? Icons.help_outline,
                  size: 16, color: foreground)),
          const SizedBox(width: AppSpacing.xs),
          Flexible(
            child: Text(status?.label ?? decision,
                style: theme.textTheme.labelMedium
                    ?.copyWith(color: foreground, fontWeight: FontWeight.w700)),
          ),
        ]),
      ),
    );
  }
}
