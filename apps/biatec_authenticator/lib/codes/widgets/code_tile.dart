import 'package:flutter/material.dart';

import '../../theme.dart';
import '../../totp/core/otp_account.dart';
import '../../totp/core/otp_generator.dart';
import '../../ui/countdown_ring.dart';
import '../codes_controller.dart';

/// The words a screen reader says for a code: the digits one by one, in the groups they are shown in, so "123 456" is "1 2 3, 4 5 6".
String spokenCode(String code) => OtpGenerator.group(code)
    .split(' ')
    .map((g) => g.split('').join(' '))
    .join(', ');

/// What the tile menu can do.
enum TileAction { rename, delete, moveUp, moveDown }

/// One account: who it is, its current code in groups of three, a countdown ring plus the seconds in text, and in the last five seconds the next code. Tap copies the code. Counter-based accounts have a button for the next code instead of a ring.
class CodeTile extends StatelessWidget {
  const CodeTile({
    super.key,
    required this.entry,
    required this.now,
    required this.hidden,
    required this.onTap,
    required this.onAction,
    required this.onNextCounter,
    required this.canMoveUp,
    required this.canMoveDown,
    this.dragHandle,
  });

  final CodeEntry entry;
  final DateTime now;

  /// Tap-to-reveal is on and this code has not been touched: it is shown as dots.
  final bool hidden;
  final VoidCallback onTap;
  final ValueChanged<TileAction> onAction;
  final VoidCallback onNextCounter;
  final bool canMoveUp;
  final bool canMoveDown;

  /// The reorder handle, when the list can be reordered.
  final Widget? dragHandle;

  static const nextCodeWithin = 5;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final account = entry.account;
    final info = entry.info;
    final hotp = account.type == OtpType.hotp;
    final code = OtpGenerator.code(account, now);
    final left = hotp ? 0 : OtpGenerator.secondsLeft(account, now);
    final showNext = !hotp && !hidden && left <= nextCodeWithin;
    final next = showNext ? OtpGenerator.nextCode(account, now) : null;
    final title = info.issuer.isEmpty ? info.label : info.issuer;
    final subtitle = info.issuer.isEmpty ? null : info.label;
    final name = subtitle == null ? title : '$title, $subtitle';
    final mono = theme.textTheme.headlineMedium?.copyWith(
      fontFeatures: const [FontFeature.tabularFigures()],
      fontWeight: FontWeight.w700,
      letterSpacing: 1,
      color: theme.colorScheme.onSurface,
    );
    final spoken = hidden
        ? '$name. Code hidden. Double tap to show it.'
        : '$name. Code ${spokenCode(code)}. Double tap to copy.';
    return Card(
      key: Key('code-${info.id}'),
      clipBehavior: Clip.antiAlias,
      child: Padding(
        padding: const EdgeInsets.fromLTRB(
            AppSpacing.m, AppSpacing.s, AppSpacing.xs, AppSpacing.s),
        child: Row(crossAxisAlignment: CrossAxisAlignment.center, children: [
          Expanded(
            child: Semantics(
              container: true,
              button: true,
              label: spoken,
              onTap: onTap,
              excludeSemantics: true,
              child: InkWell(
                key: Key('copy-${info.id}'),
                onTap: onTap,
                borderRadius: BorderRadius.circular(AppRadius.s),
                child: Padding(
                  padding: const EdgeInsets.symmetric(vertical: AppSpacing.xs),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(title,
                          style: theme.textTheme.titleMedium
                              ?.copyWith(fontWeight: FontWeight.w600)),
                      if (subtitle != null)
                        Text(subtitle,
                            style: theme.textTheme.bodyMedium?.copyWith(
                                color: theme.colorScheme.onSurfaceVariant)),
                      const SizedBox(height: AppSpacing.xs),
                      Text(
                        hidden ? '••• •••' : OtpGenerator.group(code),
                        key: Key('code-text-${info.id}'),
                        style: mono,
                      ),
                      if (next != null)
                        Text('Next: ${OtpGenerator.group(next)}',
                            key: Key('next-${info.id}'),
                            style: theme.textTheme.bodyMedium?.copyWith(
                                color: theme.colorScheme.onSurfaceVariant)),
                    ],
                  ),
                ),
              ),
            ),
          ),
          if (hotp)
            Semantics(
              button: true,
              label: 'Next code for $name',
              excludeSemantics: true,
              child: IconButton(
                key: Key('counter-${info.id}'),
                tooltip: 'Next code',
                constraints: const BoxConstraints(
                    minWidth: kMinTapTarget, minHeight: kMinTapTarget),
                onPressed: onNextCounter,
                icon: const Icon(Icons.refresh),
              ),
            )
          else
            CountdownRing(
                key: Key('ring-${info.id}'),
                fraction: OtpGenerator.fractionLeft(account, now),
                seconds: left,
                urgent: left <= nextCodeWithin),
          if (dragHandle != null) dragHandle!,
          PopupMenuButton<TileAction>(
            key: Key('menu-${info.id}'),
            tooltip: 'More for $name',
            onSelected: onAction,
            itemBuilder: (_) => [
              const PopupMenuItem(
                  value: TileAction.rename, child: Text('Rename')),
              if (canMoveUp)
                const PopupMenuItem(
                    value: TileAction.moveUp, child: Text('Move up')),
              if (canMoveDown)
                const PopupMenuItem(
                    value: TileAction.moveDown, child: Text('Move down')),
              const PopupMenuItem(
                  value: TileAction.delete, child: Text('Delete')),
            ],
          ),
        ]),
      ),
    );
  }
}
