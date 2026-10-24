import 'package:flutter/material.dart';

import '../theme.dart';

/// Nothing to show yet: says what this place is for and, when there is one, what to do next.
class EmptyState extends StatelessWidget {
  const EmptyState({
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
    return Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: kMaxContentWidth),
        child: Padding(
          padding: const EdgeInsets.all(AppSpacing.l),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              ExcludeSemantics(
                child: Container(
                  width: 72,
                  height: 72,
                  decoration: BoxDecoration(
                    color: theme.colorScheme.secondaryContainer,
                    shape: BoxShape.circle,
                  ),
                  child: Icon(icon,
                      size: 36, color: theme.colorScheme.onSecondaryContainer),
                ),
              ),
              const SizedBox(height: AppSpacing.m),
              Semantics(
                header: true,
                child: Text(title,
                    textAlign: TextAlign.center,
                    style: theme.textTheme.titleMedium),
              ),
              if (message != null) ...[
                const SizedBox(height: AppSpacing.s),
                Text(message!,
                    textAlign: TextAlign.center,
                    style: theme.textTheme.bodyMedium
                        ?.copyWith(color: theme.colorScheme.onSurfaceVariant)),
              ],
              if (actionLabel != null && onAction != null) ...[
                const SizedBox(height: AppSpacing.l),
                FilledButton(onPressed: onAction, child: Text(actionLabel!)),
              ],
            ],
          ),
        ),
      ),
    );
  }
}
