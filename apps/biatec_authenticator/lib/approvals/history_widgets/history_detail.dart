import 'dart:convert';

import 'package:material_ui/material_ui.dart';
import 'package:flutter/services.dart';

import '../../theme.dart';
import '../../ui/ui.dart';
import '../history_store.dart';
import '../widgets/fact_row.dart';
import '../widgets/mono.dart';
import 'day_groups.dart';
import 'history_status.dart';
import 'history_texts.dart';

/// One recorded decision in full: what it was, who asked, when, the receipt id (with copy) and the device key that signed it, and the stored receipt itself.
///
/// What is shown is what the history keeps, which has no binding code (see `redactReceipt`); it is taken out again here, so an entry that was not built by the store cannot show it either.
class HistoryDetailScreen extends StatelessWidget {
  const HistoryDetailScreen({super.key, required this.entry});

  final HistoryEntry entry;

  static Route<void> route(HistoryEntry entry) => MaterialPageRoute<void>(
        settings: RouteSettings(name: '/history/${entry.authReqId}'),
        builder: (_) => HistoryDetailScreen(entry: entry),
      );

  /// The device key id the receipt names, if it names one.
  static String? deviceKeyId(Map<String, Object?> receipt) {
    final device = receipt['device'];
    if (device is Map) {
      final kid = device['kid'];
      if (kid is String && kid.isNotEmpty) return kid;
    }
    return null;
  }

  Future<void> _copy(BuildContext context) async {
    await Clipboard.setData(ClipboardData(text: entry.authReqId));
    if (!context.mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(
        content: Text(HistoryTexts.ofContext(context).text('idCopied'))));
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final format = DayFormat.of(context);
    final t = HistoryTexts.of(format.locale);
    final receipt = redactReceipt(entry.receipt);
    final kid = deviceKeyId(receipt);
    final alg = receipt['alg'];
    final status = HistoryStatus.of(entry.decision);
    return Scaffold(
      appBar: AppBar(title: Text(t.text('receiptTitle'))),
      body: ListView(
        padding: const EdgeInsets.all(AppSpacing.m),
        children: [
          // Aligned, not in a Row, so the chip is bounded by the screen: at large text its word wraps instead of running off.
          Align(
              alignment: AlignmentDirectional.centerStart,
              child: StatusChip(decision: entry.decision)),
          const SizedBox(height: AppSpacing.s),
          Semantics(
            header: true,
            child: Text(entry.summary,
                key: const Key('receipt-summary'),
                style: theme.textTheme.titleLarge
                    ?.copyWith(fontWeight: FontWeight.w700)),
          ),
          if (status == HistoryStatus.refused) ...[
            const SizedBox(height: AppSpacing.m),
            StatusBanner(
              severity: StatusSeverity.danger,
              title: t.text('refusedTitle'),
              message: t.text('refusedBody'),
            ),
          ],
          const SizedBox(height: AppSpacing.m),
          Card(
            child: Padding(
              padding: const EdgeInsets.all(AppSpacing.m),
              child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Fact(t.text('decision'),
                        statusLabel(entry.decision, format.locale),
                        key: const Key('receipt-decision')),
                    Fact(t.text('client'), entry.clientLabel,
                        key: const Key('receipt-client')),
                    Fact(t.text('decided'), dayAndClock(entry.decidedAt, format),
                        key: const Key('receipt-decided')),
                    // The receipt id is the request id: Biatec OIDC serves the verifiable receipt under it.
                    Row(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Expanded(
                            child: Fact(t.text('receiptId'), entry.authReqId,
                                mono: true, key: const Key('receipt-id')),
                          ),
                          IconButton(
                            key: const Key('receipt-copy'),
                            tooltip: t.text('copyId'),
                            icon: const Icon(Icons.copy),
                            onPressed: () => _copy(context),
                          ),
                        ]),
                    if (kid != null)
                      Fact(t.text('deviceKeyId'), kid,
                          mono: true, key: const Key('receipt-kid')),
                    if (alg is String)
                      Fact(t.text('signature'), alg,
                          key: const Key('receipt-alg')),
                  ]),
            ),
          ),
          const SizedBox(height: AppSpacing.m),
          StatusBanner(
            severity: StatusSeverity.info,
            title: t.text('copyTitle'),
            message: t.text('copyBody'),
          ),
          const SizedBox(height: AppSpacing.m),
          Card(
            child: ExpansionTile(
              key: const Key('receipt-json-tile'),
              title: Text(t.text('storedReceipt')),
              subtitle: Text(t.text('asKept')),
              childrenPadding: const EdgeInsets.all(AppSpacing.m),
              expandedCrossAxisAlignment: CrossAxisAlignment.start,
              children: [
                SelectableText(
                  const JsonEncoder.withIndent('  ').convert(receipt),
                  key: const Key('receipt-json'),
                  style: monoStyle,
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}
