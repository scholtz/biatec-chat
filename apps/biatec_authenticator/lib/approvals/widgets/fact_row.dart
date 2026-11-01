import 'package:flutter/material.dart';

import '../../theme.dart';
import 'mono.dart';

/// One labelled fact of the request. The value is shown in full (addresses and hashes are never shortened), can be selected, and with [mono] is set in a fixed-width face so each character can be compared. A screen reader hears the label and the value as one item.
class Fact extends StatelessWidget {
  const Fact(this.label, this.value, {super.key, this.mono = false});

  final String label;
  final String value;
  final bool mono;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return MergeSemantics(
      // A selectable value is a control to a screen reader, so the row is at least a tap target tall.
      child: ConstrainedBox(
        constraints: const BoxConstraints(minHeight: kMinTapTarget),
        child: Padding(
          padding: const EdgeInsets.symmetric(vertical: AppSpacing.xs),
          child:
              Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text(label,
                style: theme.textTheme.labelMedium
                    ?.copyWith(color: theme.colorScheme.onSurfaceVariant)),
            SelectableText(value, style: mono ? monoStyle : null),
          ]),
        ),
      ),
    );
  }
}
