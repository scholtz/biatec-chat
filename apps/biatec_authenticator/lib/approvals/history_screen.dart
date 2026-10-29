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
  late Future<List<HistoryEntry>> _entries = widget.history.read();

  /// The list that is showing stays (and what is expanded) while it is read again.
  @override
  void onReload() {
    final entries = widget.history.read();
    setState(() {
      _entries = entries;
    });
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('Approval history')),
      body: FutureBuilder<List<HistoryEntry>>(
        future: _entries,
        builder: (context, snap) {
          if (snap.hasError) {
            return const ErrorState(
                title: 'History could not be read',
                message: 'The decisions stored on this device did not open.');
          }
          final list = snap.data;
          if (list == null) {
            return const LoadingSkeleton();
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
