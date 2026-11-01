import 'package:flutter/material.dart';

import '../shell/tab_reload.dart';
import '../theme.dart';
import '../ui/ui.dart';
import 'history_store.dart';
import 'history_widgets/day_groups.dart';
import 'history_widgets/history_detail.dart';
import 'history_widgets/history_filters.dart';
import 'history_widgets/history_status.dart';
import 'history_widgets/history_tile.dart';

/// Decisions this device signed, newest first and grouped by day, with a filter by status. A decision opens its receipt.
class HistoryScreen extends StatefulWidget {
  const HistoryScreen({super.key, required this.history, this.clock});

  final HistoryStore history;

  /// The time "Today" is measured from; the system clock when null.
  final DateTime Function()? clock;

  static String label(String decision) => statusLabel(decision);

  @override
  State<HistoryScreen> createState() => _HistoryScreenState();
}

class _HistoryScreenState extends State<HistoryScreen> with TabReloadListener {
  List<HistoryEntry>? _entries;
  bool _failed = false;
  HistoryFilter _filter = HistoryFilter.all;

  @override
  void initState() {
    super.initState();
    _load();
  }

  /// The list that is showing stays (and what is expanded) while it is read again, and also when that read fails.
  @override
  void onReload() => _load();

  /// Counts reads. A list is shown unless a newer one already is, so a slow older read cannot replace a newer list, yet its good answer is not thrown away; a failure counts only for the newest read.
  int _reads = 0;
  int _shownRead = 0;
  int _failedRead = 0;

  final _refresher = GlobalKey<RefreshIndicatorState>();

  /// A retry button shows the same spinner as a pull, so the user sees it trying while the screen stays as it is.
  void _retry() => _refresher.currentState?.show();

  Future<void> _load() async {
    final read = ++_reads;
    try {
      final entries = await widget.history.read();
      if (!mounted || read < _shownRead) return;
      _shownRead = read;
      setState(() {
        _entries = entries;
        // A newer read that failed still says so.
        _failed = _failedRead > read;
      });
    } on Object {
      if (!mounted || read != _reads) return;
      _failedRead = read;
      setState(() => _failed = true);
    }
  }

  @override
  Widget build(BuildContext context) {
    final list = _entries;
    return Scaffold(
      appBar: AppBar(title: const Text('Approval history')),
      body: list == null
          ? (_failed
              ? RefreshIndicator(
                  key: _refresher,
                  onRefresh: _load,
                  child: ErrorState(
                      title: 'History could not be read',
                      message:
                          'The decisions stored on this device did not open.',
                      onRetry: _retry),
                )
              : const LoadingSkeleton())
          : Column(children: [
              // A reload that failed leaves the list as it was, and says so.
              if (_failed)
                Padding(
                  key: const Key('history-stale'),
                  padding: const EdgeInsets.all(AppSpacing.s),
                  child: StatusBanner(
                    severity: StatusSeverity.warning,
                    title: 'History could not be refreshed',
                    message: 'Showing what was read before.',
                    actionLabel: 'Try again',
                    onAction: _retry,
                  ),
                ),
              Expanded(
                child: RefreshIndicator(
                  key: _refresher,
                  onRefresh: _load,
                  child: _list(list),
                ),
              ),
            ]),
    );
  }

  Widget _list(List<HistoryEntry> list) {
    if (list.isEmpty) {
      return const EmptyState(
          icon: Icons.receipt_long_outlined,
          title: 'No approvals yet',
          message: 'Requests you approve or deny on this device are '
              'listed here with their receipts.');
    }
    final shown = list.where(_filter.matches).toList();
    final now = (widget.clock ?? DateTime.now)();
    return ListView(
      padding: const EdgeInsets.only(bottom: AppSpacing.l),
      children: [
        HistoryFilterBar(
          selected: _filter,
          onSelected: (f) => setState(() => _filter = f),
        ),
        if (shown.isEmpty)
          Padding(
            padding: const EdgeInsets.all(AppSpacing.l),
            child: Column(children: [
              Text('Nothing ${_filter.label.toLowerCase()} yet',
                  key: const Key('history-filter-empty'),
                  textAlign: TextAlign.center,
                  style: Theme.of(context).textTheme.titleMedium),
              const SizedBox(height: AppSpacing.s),
              const Text('No decision on this device matches this filter.',
                  textAlign: TextAlign.center),
              const SizedBox(height: AppSpacing.m),
              OutlinedButton(
                key: const Key('history-show-all'),
                onPressed: () => setState(() => _filter = HistoryFilter.all),
                child: const Text('Show all'),
              ),
            ]),
          )
        else
          for (final group in groupByDay(shown, now)) ...[
            Padding(
              padding: const EdgeInsets.fromLTRB(
                  AppSpacing.m, AppSpacing.m, AppSpacing.m, AppSpacing.xs),
              child: Semantics(
                header: true,
                child: Text(group.label,
                    key: Key('history-day-${group.label}'),
                    style: Theme.of(context).textTheme.titleSmall),
              ),
            ),
            for (final e in group.entries)
              Padding(
                padding: const EdgeInsets.symmetric(
                    horizontal: AppSpacing.m, vertical: AppSpacing.xs),
                child: HistoryTile(
                  entry: e,
                  onOpen: () =>
                      Navigator.of(context).push(HistoryDetailScreen.route(e)),
                ),
              ),
          ],
      ],
    );
  }
}
