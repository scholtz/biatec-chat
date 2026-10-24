import 'package:flutter/material.dart';

import '../theme.dart';
import 'scrollable_center.dart';

/// Something failed: says what, and offers a retry when [onRetry] is given. [offline] changes the icon and wording for a missing connection.
class ErrorState extends StatelessWidget {
  const ErrorState({
    super.key,
    required this.message,
    this.title,
    this.offline = false,
    this.onRetry,
    this.retryLabel = 'Try again',
  });

  final String message;
  final String? title;
  final bool offline;
  final VoidCallback? onRetry;
  final String retryLabel;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return ScrollableCenter(
      child: Semantics(
        liveRegion: true,
        container: true,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(offline ? Icons.cloud_off : Icons.error_outline,
                size: 48,
                color: theme.colorScheme.error,
                semanticLabel: offline ? 'Offline' : 'Error'),
            const SizedBox(height: AppSpacing.m),
            Text(
                title ?? (offline ? 'You are offline' : 'Something went wrong'),
                textAlign: TextAlign.center,
                style: theme.textTheme.titleMedium),
            const SizedBox(height: AppSpacing.s),
            Text(message,
                textAlign: TextAlign.center,
                style: theme.textTheme.bodyMedium
                    ?.copyWith(color: theme.colorScheme.onSurfaceVariant)),
            if (onRetry != null) ...[
              const SizedBox(height: AppSpacing.l),
              OutlinedButton.icon(
                onPressed: onRetry,
                icon: const Icon(Icons.refresh),
                label: Text(retryLabel),
              ),
            ],
          ],
        ),
      ),
    );
  }
}
