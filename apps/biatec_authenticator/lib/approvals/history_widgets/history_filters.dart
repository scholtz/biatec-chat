import 'package:material_ui/material_ui.dart';

import '../../theme.dart';
import '../history_store.dart';
import 'history_status.dart';
import 'history_texts.dart';

/// Which decisions the list shows.
enum HistoryFilter {
  all('filterAll', null, 'filterAllEmpty'),
  approved('statusApproved', HistoryStatus.approved, 'filterApprovedEmpty'),
  denied('statusDenied', HistoryStatus.denied, 'filterDeniedEmpty'),
  refused('statusRefused', HistoryStatus.refused, 'filterRefusedEmpty');

  const HistoryFilter(this._label, this.status, this._emptyText);

  final String _label;
  final String _emptyText;

  /// The word on the chip, in the device's language.
  String get label => labelIn(null);

  /// The word on the chip, in the language of [locale] (the device's when null).
  String labelIn(Locale? locale) => HistoryTexts.of(locale).text(_label);

  /// What the list says when this filter matches nothing, in the device's language.
  String get emptyText => emptyTextIn(null);

  /// What the list says when this filter matches nothing, in the language of [locale] (the device's when null).
  String emptyTextIn(Locale? locale) =>
      HistoryTexts.of(locale).text(_emptyText);

  /// The status it keeps, or null for everything (including a decision this version does not know).
  final HistoryStatus? status;

  bool matches(HistoryEntry e) => status == null || e.decision == status!.wire;
}

/// The filter chips. A chosen chip has a check as well as its colour, and the chips wrap onto more lines at large text instead of scrolling out of reach.
class HistoryFilterBar extends StatelessWidget {
  const HistoryFilterBar(
      {super.key, required this.selected, required this.onSelected});

  final HistoryFilter selected;
  final ValueChanged<HistoryFilter> onSelected;

  @override
  Widget build(BuildContext context) {
    final locale = Localizations.maybeLocaleOf(context);
    return Padding(
      padding: const EdgeInsets.fromLTRB(
          AppSpacing.m, AppSpacing.s, AppSpacing.m, 0),
      child: Wrap(
        spacing: AppSpacing.s,
        runSpacing: AppSpacing.xs,
        children: [
          for (final f in HistoryFilter.values)
            FilterChip(
              key: Key('history-filter-${f.name}'),
              label: Text(f.labelIn(locale)),
              selected: f == selected,
              showCheckmark: true,
              onSelected: (_) => onSelected(f),
            ),
        ],
      ),
    );
  }
}
