import 'package:flutter/material.dart';

import '../../theme.dart';

/// Approve and Deny in a bar that stays at the bottom of the screen, within reach of a thumb, whatever the text size. Side by side when there is room (Deny on the left, Approve on the right), stacked with Approve on top at large text or on a narrow screen, so neither label is cut.
///
/// A button is off (null callback) when it cannot be used. Why is said in the banner at the top of the scrolling card, not here: at large text a reason line and two stacked buttons would take the screen from the card.
class ApprovalActionBar extends StatelessWidget {
  const ApprovalActionBar({
    super.key,
    required this.onApprove,
    required this.onDeny,
    this.busy = false,
    this.approveLabel = 'Approve',
    this.denyLabel = 'Deny',
    this.approveIcon = Icons.check,
    this.approveKey = const Key('approve'),
    this.denyKey = const Key('deny'),
  });

  /// The words and keys of the two buttons: the link screen reuses this bar as Link and Don't link.
  final String approveLabel;
  final String denyLabel;
  final IconData approveIcon;
  final Key approveKey;
  final Key denyKey;

  final VoidCallback? onApprove;
  final VoidCallback? onDeny;

  /// A biometric check, signing or posting is running.
  final bool busy;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    // While the keyboard is up the bar gives up its progress line and some padding, so the card and the field keep room.
    final typing = MediaQuery.viewInsetsOf(context).bottom > 0;
    final stacked = MediaQuery.textScalerOf(context).scale(1) >= 1.5 ||
        MediaQuery.sizeOf(context).width < 360;
    final approve = FilledButton.icon(
      key: approveKey,
      onPressed: onApprove,
      icon: Icon(approveIcon),
      label: Text(approveLabel),
    );
    final deny = OutlinedButton.icon(
      key: denyKey,
      onPressed: onDeny,
      icon: const Icon(Icons.close),
      label: Text(denyLabel),
    );
    return Material(
      elevation: 3,
      color: scheme.surfaceContainer,
      child: SafeArea(
        top: false,
        child: Padding(
          padding: EdgeInsets.all(typing ? AppSpacing.s : AppSpacing.m),
          child: Column(mainAxisSize: MainAxisSize.min, children: [
            if (busy && !typing) ...[
              Semantics(
                  label: 'Working', child: const LinearProgressIndicator()),
              const SizedBox(height: AppSpacing.s),
            ],
            if (stacked) ...[
              SizedBox(width: double.infinity, child: approve),
              const SizedBox(height: AppSpacing.s),
              SizedBox(width: double.infinity, child: deny),
            ] else
              Row(children: [
                Expanded(child: deny),
                const SizedBox(width: AppSpacing.m),
                Expanded(child: approve),
              ]),
          ]),
        ),
      ),
    );
  }
}
