import 'dart:convert';

import 'package:flutter/material.dart';

import '../shell/tab_reload.dart';
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

  Future<void> _load() async {
    try {
      final entries = await widget.history.read();
      if (!mounted) return;
      setState(() {
        _entries = entries;
        _failed = false;
      });
    } on Object {
      if (!mounted) return;
      setState(() => _failed = true);
    }
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('Approval history')),
      body: Builder(
        builder: (context) {
          final list = _entries;
          if (list == null) {
            return _failed
                ? ErrorState(
                    title: 'History could not be read',
                    message: 'The decisions stored on this device did not '
                        'open.',
                    onRetry: _load)
                : const LoadingSkeleton();
          }
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
        },
      ),
    );
  }
}
