import 'package:flutter/material.dart';

import 'status_banner.dart';

/// Shown when Biatec OIDC cannot be reached. Approvals need a live fetch, so a stale request is never signed.
class OfflineBanner extends StatelessWidget {
  const OfflineBanner({super.key, this.onRetry});

  final VoidCallback? onRetry;

  @override
  Widget build(BuildContext context) {
    return StatusBanner(
      severity: StatusSeverity.warning,
      icon: Icons.cloud_off,
      title: 'You are offline',
      message: 'Showing the last known state. Requests can only be approved '
          'with a live connection.',
      actionLabel: onRetry == null ? null : 'Try again',
      onAction: onRetry,
    );
  }
}
