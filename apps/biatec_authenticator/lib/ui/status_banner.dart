import 'package:flutter/material.dart';

import '../theme.dart';

enum StatusSeverity { info, success, warning, danger }

/// A message about a state the user must know. It always carries an icon and words, so it never relies on color alone.
class StatusBanner extends StatelessWidget {
  const StatusBanner({
    super.key,
    required this.severity,
    required this.message,
    this.title,
    this.icon,
    this.actionLabel,
    this.onAction,
  });

  final StatusSeverity severity;
  final String message;
  final String? title;

  /// Replaces the severity's default icon.
  final IconData? icon;
  final String? actionLabel;
  final VoidCallback? onAction;

  static IconData defaultIcon(StatusSeverity s) => switch (s) {
        StatusSeverity.info => Icons.info_outline,
        StatusSeverity.success => Icons.check_circle_outline,
        StatusSeverity.warning => Icons.warning_amber_rounded,
        StatusSeverity.danger => Icons.error_outline,
      };

  /// The word a screen reader hears before the message, as color is not available to it.
  static String severityWord(StatusSeverity s) => switch (s) {
        StatusSeverity.info => 'Note',
        StatusSeverity.success => 'Success',
        StatusSeverity.warning => 'Warning',
        StatusSeverity.danger => 'Problem',
      };

  @override
  Widget build(BuildContext context) {
    final colors = context.statusColors;
    final (background, foreground) = switch (severity) {
      StatusSeverity.info => (colors.info, colors.onInfo),
      StatusSeverity.success => (colors.success, colors.onSuccess),
      StatusSeverity.warning => (colors.warning, colors.onWarning),
      StatusSeverity.danger => (colors.danger, colors.onDanger),
    };
    final theme = Theme.of(context).textTheme;
    return Semantics(
      container: true,
      liveRegion: severity == StatusSeverity.danger,
      child: DecoratedBox(
        decoration: BoxDecoration(
          color: background,
          borderRadius: BorderRadius.circular(AppRadius.m),
        ),
        child: Padding(
          padding: const EdgeInsets.all(AppSpacing.m),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Icon(icon ?? defaultIcon(severity),
                  color: foreground, semanticLabel: severityWord(severity)),
              const SizedBox(width: AppSpacing.m),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    if (title != null)
                      Text(title!,
                          style: theme.titleSmall?.copyWith(
                              color: foreground, fontWeight: FontWeight.w700)),
                    Text(message,
                        style: theme.bodyMedium?.copyWith(color: foreground)),
                    if (actionLabel != null && onAction != null)
                      Align(
                        alignment: AlignmentDirectional.centerStart,
                        child: TextButton(
                          onPressed: onAction,
                          style: TextButton.styleFrom(
                              foregroundColor: foreground,
                              padding: EdgeInsets.zero),
                          child: Text(actionLabel!),
                        ),
                      ),
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
