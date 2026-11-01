import 'package:flutter/material.dart';

import '../../theme.dart';
import '../../ui/ui.dart';

/// How a request ended (approved, denied, expired, already decided, refused after wrong codes): an icon, a title, what it means, and the one thing to do next.
class ResultView extends StatelessWidget {
  const ResultView({
    super.key,
    required this.icon,
    required this.title,
    this.message,
    this.actionLabel,
    this.onAction,
  });

  final IconData icon;
  final String title;
  final String? message;
  final String? actionLabel;
  final VoidCallback? onAction;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return ScrollableCenter(
      child: Semantics(
        liveRegion: true,
        container: true,
        child: Column(mainAxisSize: MainAxisSize.min, children: [
          ExcludeSemantics(
              child: Icon(icon, size: 56, color: theme.colorScheme.primary)),
          const SizedBox(height: AppSpacing.m),
          Semantics(
            header: true,
            child: Text(title,
                key: const Key('approval-message'),
                textAlign: TextAlign.center,
                style: theme.textTheme.titleLarge),
          ),
          if (message != null) ...[
            const SizedBox(height: AppSpacing.s),
            Text(message!,
                key: const Key('approval-message-detail'),
                textAlign: TextAlign.center,
                style: theme.textTheme.bodyMedium
                    ?.copyWith(color: theme.colorScheme.onSurfaceVariant)),
          ],
          if (actionLabel != null && onAction != null) ...[
            const SizedBox(height: AppSpacing.l),
            FilledButton(onPressed: onAction, child: Text(actionLabel!)),
          ],
        ]),
      ),
    );
  }
}
