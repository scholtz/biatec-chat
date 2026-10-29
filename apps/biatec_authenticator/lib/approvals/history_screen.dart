import 'dart:convert';

import 'package:flutter/material.dart';

import '../shell/tab_reload.dart';
import '../theme.dart';
import '../ui/ui.dart';
import 'history_store.dart';

/// Decisions this device signed, newest first, each with its receipt.
class HistoryScreen extends StatefulWidget {
  const HistoryScreen({super.key, required this.history});

  final HistoryStore history;

  static String label(String decision) => switch (decision) {
        'approve' => 'Approved',
        'deny' => 'Denied',
        'code_failed' => 'Refused after three wrong codes',
        _ => decision,
      };

  @override
  State<HistoryScreen> createState() => _HistoryScreenState();
}

class _HistoryScreenState extends State<HistoryScreen> with TabReloadListener {
  List<HistoryEntry>? _entries;
  bool _failed = false;

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
    return ListView(children: [
            for (final e in list)
              ExpansionTile(
                key: Key('history-${e.authReqId}'),
                leading: Icon(
                    e.decision == 'approve' ? Icons.check_circle : Icons.block),
                title: Text('${HistoryScreen.label(e.decision)}: ${e.summary}'),
                subtitle: Text('${e.clientLabel} · '
                    '${e.decidedAt.toLocal().toString().substring(0, 16)}'),
                children: [
                  Padding(
                    padding: const EdgeInsets.all(16),
                    child: SelectableText(
                      const JsonEncoder.withIndent('  ').convert(e.receipt),
                      style: const TextStyle(fontFamily: 'monospace'),
                    ),
                  ),
                ],
              ),
          ]);
  }
}
