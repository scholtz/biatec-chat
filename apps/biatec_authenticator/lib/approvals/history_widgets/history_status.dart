import 'package:material_ui/material_ui.dart';

import '../../theme.dart';
import 'history_texts.dart';

/// How a recorded decision ended, for the chip on a row and the filter. Expired requests are not recorded (the history only keeps decisions that were posted), so there is no such status here.
enum HistoryStatus {
  approved('approve', 'statusApproved', 'statusApproved', Icons.check_circle),
  denied('deny', 'statusDenied', 'statusDenied', Icons.block),
  refused('code_failed', 'statusRefused', 'statusRefusedLong', Icons.gpp_bad);

  const HistoryStatus(this.wire, this._label, this._longLabel, this.icon);

  /// The decision as the store keeps it.
  final String wire;

  final String _label;
  final String _longLabel;
  final IconData icon;

  /// A word for a chip, in the device's language.
  String get label => labelIn(null);

  /// A word for a chip, in the language of [locale] (the device's when null).
  String labelIn(Locale? locale) => HistoryTexts.of(locale).text(_label);

  /// A sentence part for the detail and for a screen reader, in the device's language.
  String get longLabel => longLabelIn(null);

  /// A sentence part in the language of [locale] (the device's when null).
  String longLabelIn(Locale? locale) =>
      HistoryTexts.of(locale).text(_longLabel);

  /// The status of a stored decision, or null for one this version does not know (it is still listed, under All).
  static HistoryStatus? of(String decision) {
    for (final s in values) {
      if (s.wire == decision) return s;
    }
    return null;
  }
}

/// The word for a stored decision, known or not, in the language of [locale] (the device's when null).
String statusLabel(String decision, [Locale? locale]) =>
    HistoryStatus.of(decision)?.longLabelIn(locale) ?? decision;

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
            child: Text(
                status?.labelIn(Localizations.maybeLocaleOf(context)) ??
                    decision,
                // A value from a newer version is shown as it is, but cannot make a row tall.
                maxLines: 2,
                overflow: TextOverflow.ellipsis,
                style: theme.textTheme.labelMedium
                    ?.copyWith(color: foreground, fontWeight: FontWeight.w700)),
          ),
        ]),
      ),
    );
  }
}
