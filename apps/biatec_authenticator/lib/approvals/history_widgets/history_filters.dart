import 'package:flutter/material.dart';

import '../../theme.dart';
import '../history_store.dart';
import 'history_status.dart';

/// Which decisions the list shows.
enum HistoryFilter {
  all('All', null),
  approved('Approved', HistoryStatus.approved),
  denied('Denied', HistoryStatus.denied),
  refused('Refused', HistoryStatus.refused);

  const HistoryFilter(this.label, this.status);

  final String label;

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
              label: Text(f.label),
              selected: f == selected,
              showCheckmark: true,
              onSelected: (_) => onSelected(f),
            ),
        ],
      ),
    );
  }
}
