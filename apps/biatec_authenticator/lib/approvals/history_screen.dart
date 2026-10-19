import 'dart:convert';

import 'package:flutter/material.dart';

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

class _HistoryScreenState extends State<HistoryScreen> {
  late final Future<List<HistoryEntry>> _entries = widget.history.read();

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('Approval history')),
      body: FutureBuilder<List<HistoryEntry>>(
        future: _entries,
        builder: (context, snap) {
          if (snap.hasError) {
            return const Center(child: Text('History could not be read.'));
          }
          final list = snap.data;
          if (list == null) {
            return const Center(child: CircularProgressIndicator());
          }
          if (list.isEmpty) {
            return const Center(child: Text('No approvals yet.'));
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
