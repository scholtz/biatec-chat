import 'package:flutter/material.dart';

import '../../theme.dart';
import '../history_store.dart';
import 'day_groups.dart';
import 'history_status.dart';

/// [s] as one spoken sentence: trimmed, and ending in exactly one of . ! ? (an empty text stays empty), so a text that already ends in a stop is not read with two.
String sentence(String s) {
  final t = s.trim();
  if (t.isEmpty) return '';
  return RegExp(r'[.!?]$').hasMatch(t) ? t : '$t.';
}

/// One recorded decision: its status (icon and word), what it was about, who asked and when. The whole card opens the receipt. A screen reader hears it as one button with everything in full.
class HistoryTile extends StatelessWidget {
  const HistoryTile({super.key, required this.entry, required this.onOpen});

  final HistoryEntry entry;
  final VoidCallback onOpen;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final time = clockText(entry.decidedAt);
    // The day is spoken too: a screen reader moving by item does not read the day heading with it.
    // Without a stop of its own: it sits mid-sentence, before the date.
    final client =
        entry.clientLabel.trim().replaceFirst(RegExp(r'[.!?]+$'), '');
    final spoken = [
      sentence(statusLabel(entry.decision)),
      sentence(entry.summary),
      sentence('${client.isEmpty ? 'At' : 'From $client,'} '
          '${dayAndClock(entry.decidedAt)}'),
      'Double tap to open the receipt.',
    ].where((s) => s.isNotEmpty).join(' ');
    return Card(
      clipBehavior: Clip.antiAlias,
      child: Semantics(
        container: true,
        button: true,
        label: spoken,
        onTap: onOpen,
        excludeSemantics: true,
        child: InkWell(
          key: Key('history-${entry.authReqId}'),
          onTap: onOpen,
          child: Padding(
            padding: const EdgeInsets.all(AppSpacing.m),
            child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Expanded(
                child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      StatusChip(decision: entry.decision),
                      const SizedBox(height: AppSpacing.s),
                      Text(entry.summary,
                          style: theme.textTheme.titleMedium
                              ?.copyWith(fontWeight: FontWeight.w600)),
                      const SizedBox(height: AppSpacing.xs),
                      Text('${entry.clientLabel} · $time',
                          style: theme.textTheme.bodyMedium?.copyWith(
                              color: theme.colorScheme.onSurfaceVariant)),
                    ]),
              ),
              const ExcludeSemantics(child: Icon(Icons.chevron_right)),
            ]),
          ),
        ),
      ),
    );
  }
}
